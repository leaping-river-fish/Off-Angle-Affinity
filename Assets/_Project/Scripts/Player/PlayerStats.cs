// =============================================================================
// PlayerStats — handle-based modifier stack for every numeric the player owns.
// This is what makes OVERLAPPING, TIMED buffs work.
//
// THE PROBLEM IT SOLVES:
//   MovementStateContext.SpeedMultiplier is a single float. If a fire trail
//   writes 1.2 and a frost slow writes 0.7, they clobber each other - and
//   whichever ends FIRST resets it to 1 while the other is still running. Any
//   system where two effects can touch the same number needs a stack, not a
//   slot.
//
//   Each registration returns a HANDLE. Add and remove are therefore symmetric
//   and order-independent: two effects can hold modifiers on the same stat and
//   each removes only its own, whatever order they end in.
//
// BOTH FORMS ARE NEEDED:
//   effective = (baseline + SUM of additives) * PRODUCT of multipliers
//   "+20% fire rate" is multiplicative; "shield regen starts 1s sooner" is
//   additive (-1). A stack with only one form cannot express the other.
//
// ADOPTING A STAT IS FREE:
//   Evaluate() returns the baseline untouched when nothing is registered, so
//   wrapping a read site changes no behaviour until some effect actually
//   registers. Integrate stats one at a time, when the first effect needs one.
//
// CLAMPING IS THE READ SITE'S JOB:
//   Nothing here bounds the result - a multiplier of 0 and a negative additive
//   are both legal and meaningful. A read site with a floor (regen delay must
//   not go negative, fire rate must stay above zero) clamps after Evaluate.
//
// AUTHORITY:
//   NOT networked. Every peer computes its own from the same synced loadout,
//   so server and owner agree without extra traffic. A transient buff applied
//   BY another player must be registered on the server and mirrored to the
//   victim's owner via a TargetRpc - that is the effect author's job, and it is
//   unavoidable because MovementStateMachine is disabled on non-owner players.
// =============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace OffAngle.Player
{
    /// <summary>
    /// Every stat an effect may modify. Adding one costs nothing until a read
    /// site wraps it - see the integration notes on each entry.
    /// </summary>
    public enum PlayerStat
    {
        /// <summary>Ground/air/slide speed. Read via MovementStateContext.SpeedMultiplier.</summary>
        MoveSpeed = 0,

        /// <summary>Gravity accumulation while airborne. Wall-run gravity is separate (MovementSettings.WallRunGravityScale).</summary>
        Gravity,

        /// <summary>Jump impulse height.</summary>
        JumpHeight,

        /// <summary>Shots per second. Must be applied at BOTH Gun.TryFire and PlayerWeaponController's server rate check.</summary>
        FireRate,

        /// <summary>Outgoing weapon damage. For UNCONDITIONAL scaling only - conditional damage belongs in the damage pipeline.</summary>
        WeaponDamage,

        /// <summary>Reload duration.</summary>
        ReloadSpeed,

        /// <summary>Seconds of no damage before shield regen begins. Use a NEGATIVE additive to start regen sooner.</summary>
        ShieldRegenDelay,

        /// <summary>Shield points restored per second.</summary>
        ShieldRegenRate,

        MaxHealth,
        MaxShield,

        /// <summary>Grapple pull speed.</summary>
        GrappleSpeed,
    }

    public class PlayerStats : MonoBehaviour
    {
        private readonly struct Modifier
        {
            public readonly int Handle;
            public readonly float Multiplier;
            public readonly float Additive;

            public Modifier(int handle, float multiplier, float additive)
            {
                Handle = handle;
                Multiplier = multiplier;
                Additive = additive;
            }
        }

        private List<Modifier>[] _modifiers;
        private float[] _multiplier;
        private float[] _additive;
        private int _nextHandle = 1;

        /// <summary>Raised whenever a stat's effective value changes. A read site that caches (e.g. movement) re-pulls here.</summary>
        public event Action<PlayerStat> Changed;

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Registers a modifier and returns the handle needed to remove it. Pass
        /// multiplier 1 for a purely additive modifier, or additive 0 for a purely
        /// multiplicative one. Returns 0 for a no-op registration, which
        /// RemoveModifier safely ignores.
        /// </summary>
        public int AddModifier(PlayerStat stat, float multiplier = 1f, float additive = 0f)
        {
            EnsureInitialized();

            // Nothing to track, and returning a real handle would make callers
            // believe they own something they need to release.
            if (Mathf.Approximately(multiplier, 1f) && Mathf.Approximately(additive, 0f))
                return 0;

            int index = (int)stat;
            _modifiers[index] ??= new List<Modifier>(2);

            int handle = _nextHandle++;
            _modifiers[index].Add(new Modifier(handle, multiplier, additive));

            Recalculate(stat);
            return handle;
        }

        /// <summary>
        /// Removes a previously registered modifier. Passing 0, or a handle that
        /// was already removed, is a safe no-op - so an effect's OnDetach can
        /// release unconditionally without tracking whether it ever registered.
        /// </summary>
        public void RemoveModifier(PlayerStat stat, int handle)
        {
            if (handle == 0) return;
            EnsureInitialized();

            List<Modifier> list = _modifiers[(int)stat];
            if (list == null) return;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Handle != handle) continue;

                list.RemoveAt(i);
                Recalculate(stat);
                return;
            }
        }

        /// <summary>
        /// Applies every registered modifier to a baseline:
        /// (baseline + SUM additives) * PRODUCT multipliers.
        /// Returns baseline unchanged when nothing is registered.
        /// </summary>
        public float Evaluate(PlayerStat stat, float baseline)
        {
            EnsureInitialized();
            int index = (int)stat;
            return (baseline + _additive[index]) * _multiplier[index];
        }

        /// <summary>Product of every multiplier on this stat. 1 when none are registered.</summary>
        public float GetMultiplier(PlayerStat stat)
        {
            EnsureInitialized();
            return _multiplier[(int)stat];
        }

        /// <summary>Sum of every additive on this stat. 0 when none are registered.</summary>
        public float GetAdditive(PlayerStat stat)
        {
            EnsureInitialized();
            return _additive[(int)stat];
        }

        /// <summary>
        /// Drops every modifier on every stat. Intended for teardown only - effects
        /// release their own handles in OnDetach, and calling this instead would
        /// strip modifiers belonging to effects that are still attached.
        /// </summary>
        public void ClearAll()
        {
            EnsureInitialized();

            for (int i = 0; i < _modifiers.Length; i++)
            {
                if (_modifiers[i] == null || _modifiers[i].Count == 0) continue;

                _modifiers[i].Clear();
                Recalculate((PlayerStat)i);
            }
        }

        // ------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------

        // Lazy rather than Awake-based: PlayerAffinity resolves its context in
        // Awake, and relying on component order within a single Awake pass is
        // exactly the kind of ordering bug that only shows up on one peer.
        private void EnsureInitialized()
        {
            if (_modifiers != null) return;

            int count = Enum.GetValues(typeof(PlayerStat)).Length;
            _modifiers = new List<Modifier>[count];
            _multiplier = new float[count];
            _additive = new float[count];

            for (int i = 0; i < count; i++)
                _multiplier[i] = 1f;
        }

        private void Recalculate(PlayerStat stat)
        {
            int index = (int)stat;
            List<Modifier> list = _modifiers[index];

            float multiplier = 1f;
            float additive = 0f;

            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    multiplier *= list[i].Multiplier;
                    additive += list[i].Additive;
                }
            }

            _multiplier[index] = multiplier;
            _additive[index] = additive;

            Changed?.Invoke(stat);
        }
    }
}
