// =============================================================================
// GroundEffectZone — generic, reusable spatial trigger volume that applies a
// GroundEffectPayload to whoever stands inside it. Fire patches, and later a
// water speed-boost stream or an ice momentum-preserving patch, are all this
// same component with a different payload asset - see GroundEffectPayload.cs.
//
// AUTHORITY:
//   Server-only, mirroring Projectile.cs's model: occupant detection and every
//   payload callback are gated on IsServerInitialized. The zone's existence
//   and position replicate "for free" via the normal NetworkObject spawn
//   message (see Initialize/the spawner) - no SyncVars are needed here since
//   nothing about the zone itself changes after it spawns.
//
// OCCUPANT TRACKING:
//   OnTriggerEnter/Exit (server-only) maintain a dictionary of currently-inside
//   occupants keyed by root Transform, resolving each one's AffinityRuntimeContext
//   through PlayerAffinity - the exact same context bag UltimateBehavior.
//   ServerActivate receives, so a payload gets Health/Shield/Movement/Stats
//   with zero new resolution code. An internal timer calls the payload's
//   OnTick on every current occupant at _tickInterval; OnEnter/OnExit fire on
//   membership transitions. On expiry, every remaining occupant gets OnExit
//   before despawn - guarantees cleanup for a stateful payload (e.g. a future
//   speed boost) even if nobody ever steps back out.
//
// WHETHER THE OWNER IS EXCLUDED IS A PAYLOAD DECISION, NOT A ZONE DECISION:
//   The zone tracks every occupant, caster included, and exposes
//   OwnerNetworkObject so a payload can compare and decide for itself - e.g.
//   FireGroundEffectPayload skips damaging the caster, but a future
//   self-buff zone might deliberately want to include them. Hard-excluding
//   the owner here would take that choice away from every future payload.
// =============================================================================

using System.Collections.Generic;
using FishNet.Object;
using OffAngle.Affinities;
using OffAngle.Networking;
using UnityEngine;

namespace OffAngle.Combat
{
    [RequireComponent(typeof(Collider))]
    public class GroundEffectZone : NetworkBehaviour
    {
        [Header("Config (overridable per-instance via Initialize)")]
        [SerializeField] private GroundEffectPayload _payload;
        [SerializeField, Min(0.1f)] private float _duration = 4f;
        [SerializeField, Min(0.05f)] private float _tickInterval = 0.5f;

        // Server-only bookkeeping - never read on clients.
        private NetworkObject _owner;
        private float _despawnAtTime;
        private float _tickAccumulator;
        private bool _initialized;
        private readonly Dictionary<Transform, AffinityRuntimeContext> _occupants = new();

        /// <summary>The player who created this zone (e.g. the fireball's shooter), or null. Payloads consult this to decide whether to affect their own creator - see this file's header note.</summary>
        public NetworkObject OwnerNetworkObject => _owner;

        /// <summary>
        /// Server-only. Called once by the spawner immediately after
        /// ServerManager.Spawn, mirroring Projectile.ServerInitialize. Any
        /// parameter left at its default (null / negative) keeps the
        /// prefab's own serialized value instead of overriding it.
        /// </summary>
        public void Initialize(NetworkObject owner, GroundEffectPayload payload = null, float duration = -1f, float tickInterval = -1f)
        {
            if (!IsServerInitialized) return;

            _owner = owner;
            if (payload != null) _payload = payload;
            if (duration > 0f) _duration = duration;
            if (tickInterval > 0f) _tickInterval = tickInterval;

            _despawnAtTime = Time.time + _duration;
            _initialized = true;
        }

        private void Update()
        {
            if (!IsServerInitialized || !_initialized) return;

            if (Time.time >= _despawnAtTime)
            {
                ServerEndAllOccupants();
                base.NetworkObject.Despawn();
                return;
            }

            _tickAccumulator += Time.deltaTime;
            if (_tickAccumulator < _tickInterval) return;
            _tickAccumulator -= _tickInterval;

            if (_payload == null || _occupants.Count == 0) return;

            foreach (KeyValuePair<Transform, AffinityRuntimeContext> pair in _occupants)
                _payload.OnTick(pair.Value, this, _tickInterval);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!IsServerInitialized || !_initialized) return;

            Transform root = other.transform.root;
            if (_occupants.ContainsKey(root)) return;

            AffinityRuntimeContext context = ResolveContext(root);
            if (context == null) return;

            _occupants[root] = context;
            _payload?.OnEnter(context, this);
        }

        private void OnTriggerExit(Collider other)
        {
            if (!IsServerInitialized || !_initialized) return;

            Transform root = other.transform.root;
            if (!_occupants.TryGetValue(root, out AffinityRuntimeContext context)) return;

            _occupants.Remove(root);
            _payload?.OnExit(context, this);
        }

        /// <summary>Server-only. Ends every currently-inside occupant before despawn - see this file's header note on why.</summary>
        private void ServerEndAllOccupants()
        {
            if (_payload != null)
            {
                foreach (KeyValuePair<Transform, AffinityRuntimeContext> pair in _occupants)
                    _payload.OnExit(pair.Value, this);
            }

            _occupants.Clear();
        }

        private static AffinityRuntimeContext ResolveContext(Transform root)
        {
            PlayerAffinity affinity = root.GetComponent<PlayerAffinity>();
            if (affinity != null) return affinity.Context;

            // Not a full player (e.g. the test Dummy target, which has Health
            // but no PlayerAffinity) - fall back to a minimal context built
            // directly from whatever combat components are present, so a
            // ground hazard still affects any damageable entity, not just
            // players. Matches Projectile.ApplySplashDamage's convention of
            // treating any IDamageable uniformly.
            Health health = root.GetComponent<Health>();
            if (health == null) return null;

            return new AffinityRuntimeContext
            {
                PlayerRoot = root.gameObject,
                NetworkObject = root.GetComponent<NetworkObject>(),
                Health = health,
                Shield = root.GetComponent<Shield>(),
            };
        }
    }
}
