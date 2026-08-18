// =============================================================================
// CombatMemory — server-side record of who hit whom, when, and how many times
// in a row. The relational state nothing in the project previously tracked.
//
// WHAT IT UNLOCKS:
//   Perks whose condition is a RELATIONSHIP rather than a number:
//     "bonus damage to enemies who have not damaged you recently"
//     "your opening hit on a target is empowered"
//     "every Nth consecutive hit on the same target does something"
//   None of these are answerable from the hit alone.
//
// FED BY ONE EVENT:
//   Health.ServerDamageApplied, the same server-side broadcast that drives
//   ultimate charge. Each CombatMemory subscribes itself and filters for hits
//   where it is the victim or the attacker, so no central router is needed.
//
// ORDERING (important):
//   Health raises that event AFTER damage lands, which means modifiers running
//   inside DamagePipeline see this memory as it was BEFORE the current hit.
//   That is what makes "is this my first hit on them?" answerable at all - if
//   the record were written first, every opener perk would answer its own
//   question and never fire.
//
// KEYED BY ObjectId, NOT BY REFERENCE:
//   NetworkObjects despawn constantly. Holding them as dictionary keys would
//   keep destroyed Unity objects alive as keys whose == null is true but whose
//   Equals still matches. An int id has none of those hazards.
//
// BOUNDED:
//   Cleared on respawn, and pruned by TTL once the table grows past a
//   threshold, so a long match cannot accumulate an entry per player per life
//   forever.
// =============================================================================

using System;
using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;

namespace OffAngle.Combat
{
    public class CombatMemory : NetworkBehaviour
    {
        [Header("Streaks")]
        [Tooltip("Longest gap between two hits on the same target that still continues a streak. A perk asking for a LONGER window than this is clamped to it - raise this before authoring one that needs more.")]
        [SerializeField, Min(0.1f)] private float _streakMaxGapSeconds = 5f;

        [Header("Bounds")]
        [Tooltip("Once the table holds more entries than this, entries untouched for longer than the TTL are dropped.")]
        [SerializeField, Min(4)] private int _pruneThreshold = 24;

        [Tooltip("How long an untouched relationship is kept before pruning. Must comfortably exceed the longest window any perk asks about.")]
        [SerializeField, Min(1f)] private float _relationTtlSeconds = 30f;

        private class Relation
        {
            public float LastTheyDamagedMe = float.NegativeInfinity;
            public float LastIDamagedThem = float.NegativeInfinity;
            public float LastStreakHitTime = float.NegativeInfinity;
            public int Streak;
            public float LastTouched = float.NegativeInfinity;
        }

        private readonly Dictionary<int, Relation> _relations = new Dictionary<int, Relation>();

        [Tooltip("Optional. Leave null to auto-resolve. Memory is wiped when this player respawns - a grudge from a previous life should not carry into the next one.")]
        [SerializeField] private PlayerLifecycleController _lifecycle;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_lifecycle == null) _lifecycle = GetComponent<PlayerLifecycleController>();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            Health.ServerDamageApplied += HandleServerDamageApplied;

            if (_lifecycle != null)
                _lifecycle.OnServerRespawned += ServerClear;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            // Runs synchronously inside FishNet's despawn broadcast - contain any
            // exception rather than letting it escape into the transport.
            try
            {
                Health.ServerDamageApplied -= HandleServerDamageApplied;

                if (_lifecycle != null)
                    _lifecycle.OnServerRespawned -= ServerClear;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }

            _relations.Clear();
        }

        private void OnDestroy()
        {
            // Belt and braces: a static event holding a destroyed instance would
            // keep firing into it for the rest of the session.
            try
            {
                Health.ServerDamageApplied -= HandleServerDamageApplied;

                if (_lifecycle != null)
                    _lifecycle.OnServerRespawned -= ServerClear;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        // ------------------------------------------------------------------
        // Queries (server-side; safe to call from an IDamageModifier)
        // ------------------------------------------------------------------

        /// <summary>
        /// Seconds since <paramref name="other"/> last damaged this entity.
        /// PositiveInfinity if they never have - so "hasn't hit me in 5s" is
        /// simply <c>TimeSinceDamagedBy(x) &gt; 5f</c> and is true for strangers.
        /// </summary>
        public float TimeSinceDamagedBy(NetworkObject other)
        {
            Relation relation = Find(other);
            return relation == null || float.IsNegativeInfinity(relation.LastTheyDamagedMe)
                ? float.PositiveInfinity
                : Time.time - relation.LastTheyDamagedMe;
        }

        /// <summary>Seconds since this entity last damaged <paramref name="other"/>. PositiveInfinity if never.</summary>
        public float TimeSinceDealtDamageTo(NetworkObject other)
        {
            Relation relation = Find(other);
            return relation == null || float.IsNegativeInfinity(relation.LastIDamagedThem)
                ? float.PositiveInfinity
                : Time.time - relation.LastIDamagedThem;
        }

        /// <summary>
        /// How many consecutive hits this entity has landed on <paramref name="other"/>
        /// without a gap longer than <paramref name="expireAfterSeconds"/>. Returns 0
        /// once the streak lapses. Windows longer than the configured max gap are
        /// clamped to it.
        /// </summary>
        public int HitStreakOn(NetworkObject other, float expireAfterSeconds)
        {
            Relation relation = Find(other);
            if (relation == null || relation.Streak <= 0) return 0;

            float window = Mathf.Min(expireAfterSeconds, _streakMaxGapSeconds);
            return Time.time - relation.LastStreakHitTime > window ? 0 : relation.Streak;
        }

        /// <summary>
        /// True if this entity has not damaged <paramref name="other"/> within
        /// <paramref name="windowSeconds"/> - i.e. the hit being resolved right now
        /// is an opener. Only meaningful when called from inside DamagePipeline,
        /// before the current hit has been recorded.
        /// </summary>
        public bool IsFirstContactWith(NetworkObject other, float windowSeconds)
            => TimeSinceDealtDamageTo(other) > windowSeconds;

        /// <summary>
        /// Server-only. Forgets everything. Called on respawn - a grudge from a
        /// previous life should not carry into the next one.
        /// </summary>
        public void ServerClear()
        {
            if (!IsServerInitialized) return;
            _relations.Clear();
        }

        // ------------------------------------------------------------------
        // Recording
        // ------------------------------------------------------------------

        private void HandleServerDamageApplied(NetworkObject victim, DamageInfo info, float amountApplied)
        {
            if (!IsServerInitialized) return;

            NetworkObject self = base.NetworkObject;
            if (self == null) return;

            NetworkObject attacker = info.Attacker;
            float now = Time.time;

            // Recorded in neither direction when there is no distinct attacker:
            // a hazard hit is not a relationship, and counting self-damage would
            // let a player farm their own streak and opener conditions.
            if (attacker == null || attacker == self) return;

            if (victim == self)
            {
                Relation relation = GetOrCreate(attacker.ObjectId, now);
                relation.LastTheyDamagedMe = now;
            }
            else if (attacker == self && victim != null)
            {
                Relation relation = GetOrCreate(victim.ObjectId, now);

                // A gap longer than the max resets rather than extends, so the
                // stored streak can never outlive the engagement that built it.
                if (now - relation.LastStreakHitTime > _streakMaxGapSeconds)
                    relation.Streak = 0;

                relation.Streak++;
                relation.LastStreakHitTime = now;
                relation.LastIDamagedThem = now;
            }
        }

        // ------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------

        private Relation Find(NetworkObject other)
        {
            if (other == null) return null;
            return _relations.TryGetValue(other.ObjectId, out Relation relation) ? relation : null;
        }

        private Relation GetOrCreate(int objectId, float now)
        {
            if (!_relations.TryGetValue(objectId, out Relation relation))
            {
                if (_relations.Count >= _pruneThreshold)
                    Prune(now);

                relation = new Relation();
                _relations[objectId] = relation;
            }

            relation.LastTouched = now;
            return relation;
        }

        private void Prune(float now)
        {
            // Collecting keys first because a Dictionary cannot be modified while
            // being enumerated. Only runs when the table is already oversized.
            List<int> stale = null;

            foreach (KeyValuePair<int, Relation> pair in _relations)
            {
                if (now - pair.Value.LastTouched <= _relationTtlSeconds) continue;
                stale ??= new List<int>();
                stale.Add(pair.Key);
            }

            if (stale == null) return;

            for (int i = 0; i < stale.Count; i++)
                _relations.Remove(stale[i]);
        }
    }
}
