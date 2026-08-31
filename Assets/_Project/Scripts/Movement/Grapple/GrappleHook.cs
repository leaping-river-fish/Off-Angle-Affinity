// =============================================================================
// GrappleHook — server-authoritative networked grapple projectile spawned by
// PlayerGrapple. Flies out, decides what it's allowed to attach to, and
// reports the outcome back through a plain callback (no damage, no
// GunData/ShotBehavior involvement - this is a movement ability, not a weapon).
//
// AUTHORITY:
//   Same model as Projectile.cs: movement and collision are computed ONLY on
//   the server (IsServerInitialized gate below). NetworkTransform (added to
//   this prefab by hand, see the Unity walkthrough) replicates the server's
//   position to every peer so everyone sees the same flight and the same
//   embedded rest position once it attaches.
//
// COLLISION MODEL — "any surface, not entities, configurable":
//   The Collider on this prefab MUST be a TRIGGER (not solid). A trigger
//   collider generates no physical collision response at all, so the hook's
//   Rigidbody flies straight through anything it hits and this class alone
//   decides - in OnTriggerEnter, in script - whether that overlap counts as
//   a valid attach point. This sidesteps Unity's Physics Layer Collision
//   Matrix entirely: no project-settings changes are needed to get
//   "collides with world geometry but passes through players/entities."
//
//   A collider counts as a valid surface unless:
//     1. its root is the attacker's own root (never self-attach), or
//     2. _ignoreDamageableEntities is true AND it has an IDamageable in its
//        parent chain (the same signal HitResolution.cs already uses to mean
//        "this is an entity, not world geometry" - see Player/Dummy, both
//        of which carry Health/IDamageable), or
//     3. its layer is included in _excludedLayers (hard override for cases
//        the IDamageable check can't express, e.g. glass/water/VFX-only
//        colliders).
//   Everything else attaches - which means ordinary world geometry (ground,
//   walls, pillars) needs ZERO per-object setup to become "grappleable".
//
// LIFECYCLE:
//   ServerSetOwner() is called once by PlayerGrapple BEFORE ServerManager.Spawn
//   (see that method's doc comment for why the ordering matters), then
//   ServerInitialize() is called once immediately after. Unlike Projectile.cs
//   (which despawns itself the instant it resolves), a hook that ATTACHES
//   stays alive and embedded -
//   it's the visual anchor/rope endpoint for as long as the player is being
//   pulled. PlayerGrapple calls ServerRelease() once the pull ends (arrival,
//   cancel, or death) to despawn it. A hook that MISSES (excluded surface,
//   lifetime timeout) despawns itself immediately, same as Projectile.cs.
//
// SETUP (see Unity walkthrough): this prefab needs a NetworkObject,
// NetworkTransform, Rigidbody, and a TRIGGER Collider in addition to this script.
// =============================================================================

using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using OffAngle.Combat;
using OffAngle.Networking;
using UnityEngine;

namespace OffAngle.Movement.Grapple
{
    [RequireComponent(typeof(Rigidbody))]
    public class GrappleHook : NetworkBehaviour
    {
        [Header("Targeting / Collision")]
        [Tooltip("Layers always excluded from attachment, on top of whatever PlayerGrapple passes in via ServerInitialize (e.g. Ignore Raycast, water, glass, VFX-only colliders) - a per-prefab floor that applies no matter which player fired this hook.")]
        [SerializeField] private LayerMask _defaultExcludedLayers = 0;

        // Replicated so remote observers can attribute this hook to its
        // owner for future VFX/audio hooks (see GrappleEvents). FishNet
        // requires SyncVar<T> fields to be readonly-initialized.
        private readonly SyncVar<NetworkObject> _ownerSync = new SyncVar<NetworkObject>();
        private readonly SyncVar<ushort> _castIdSync = new SyncVar<ushort>();

        private Rigidbody _rigidbody;

        // Server-only bookkeeping - never read on clients.
        private Transform _attackerRoot;
        private bool _ignoreDamageableEntities;
        private LayerMask _excludedLayers;
        private Action<bool, Vector3, Vector3> _onResolved;
        private float _despawnAtTime;
        private bool _initialized;
        private bool _resolved;
        private bool _attached;

        private void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();

            // Discrete (the prefab's serialized default) only tests for
            // overlap once per physics step - at _hookSpeed (55 m/s default)
            // against a 0.02s fixed timestep that's ~1.1m of travel per
            // step, easily enough to skip clean over a thin panel, a
            // pillar's edge, or a glancing hit on angled geometry without
            // ever registering an overlap, so OnTriggerEnter silently never
            // fires and the hook flies on to time out as a miss - exactly
            // the "tracer goes into the wall but never pulls" symptom.
            // ContinuousSpeculative works with triggers (unlike Continuous)
            // and does swept collision detection to catch fast-moving objects.
            _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // Owner: if we already spawned a visual-only predicted hook, hand off
            // to this networked instance and do NOT RaiseHookFired again. A second
            // HookFired would snap the rope onto this transform while its mesh is
            // still hidden and NetworkTransform may still be interpolating from origin.

            bool ownerReconciled = false;
            if (base.IsOwner
                && _castIdSync.Value != 0
                && _ownerSync.Value != null
                && _ownerSync.Value.TryGetComponent<PlayerGrapple>(out PlayerGrapple grapple))
                {
                    ownerReconciled = grapple.ReconcilePredictedHook(_castIdSync.Value, base.NetworkObject);
                }
            
            if (!ownerReconciled)
            {
                GrappleEvents.RaiseHookFired(_ownerSync.Value, transform, transform.position, transform.forward);
            }
        }

        /// <summary>
        /// Server-only. MUST be called BEFORE ServerManager.Spawn() - a
        /// SyncVar's initial replicated value is whatever it holds at the
        /// moment Spawn() runs; anything set afterward only reaches
        /// observers as a later "changed" message, which arrives too late
        /// for OnStartClient() (already fired at spawn time) to see. Since
        /// OnStartClient() is exactly where this hook raises
        /// GrappleEvents.HookFired with _ownerSync.Value (see below), owner
        /// used to always read back as null there - GrappleRopeRenderer's
        /// owner-comparison filter could then never match ANY player,
        /// making the rope invisible for literally everyone, always,
        /// regardless of who fired it or who's watching. Calling this
        /// before Spawn() fixes that by baking the correct owner into the
        /// hook's initial spawn payload.
        /// </summary>
        public void ServerSetOwner(NetworkObject owner, ushort castId)
        {
            _ownerSync.Value = owner;
            _castIdSync.Value = castId;
        }

        /// <summary>
        /// Owner-only. Same flight setup as ServerInitialize, but this instance is a
        /// regular GameObject (never Spawned). Collision still no-ops because
        /// OnTriggerEnter / Update miss-timeout are gated on IsServerInitialized.
        /// </summary>
        public void ClientInitializePredicted(Transform attackerRoot, Vector3 velocity, bool useGravity, float lifetime)
        {
            _attackerRoot = attackerRoot;
            _rigidbody.useGravity = useGravity;
            _rigidbody.linearVelocity = velocity;
            _despawnAtTime = Time.time + Mathf.Max(0.01f, lifetime);
            _initialized = true;
        }

        /// <summary>
        /// Server-only. Called once by PlayerGrapple immediately after this
        /// instance is spawned (see ServerSetOwner above for what must
        /// happen BEFORE spawn instead).
        /// </summary>
        public void ServerInitialize(
            Transform attackerRoot,
            Vector3 velocity,
            bool useGravity,
            float lifetime,
            bool ignoreDamageableEntities,
            LayerMask excludedLayers,
            Action<bool, Vector3, Vector3> onResolved)
        {
            if (!IsServerInitialized) return;

            _attackerRoot = attackerRoot;
            _ignoreDamageableEntities = ignoreDamageableEntities;
            _excludedLayers = excludedLayers | _defaultExcludedLayers;
            _onResolved = onResolved;

            _rigidbody.useGravity = useGravity;
            _rigidbody.linearVelocity = velocity;
            _despawnAtTime = Time.time + Mathf.Max(0.01f, lifetime);
            _initialized = true;
        }

        private void Update()
        {
            if (!IsServerInitialized || !_initialized || _attached) return;

            if (Time.time >= _despawnAtTime)
                ResolveMiss();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!IsServerInitialized || !_initialized || _resolved) return;

            if (_attackerRoot != null && other.transform.root == _attackerRoot)
                return; // Never self-attach to the shooter's own colliders.

            // Ground-effect zones (fire patches, Hadal Zone, ...) are triggers
            // for occupant detection only, not real geometry - same reasoning
            // Projectile.cs already excludes them for. Without this, their
            // trigger collider reads as valid surface here, so a hook fired
            // from inside (or through) one attaches to thin air at the
            // zone's collider boundary instead of flying on to real
            // geometry - the exact "grapple hits an invisible wall" symptom.
            if (other.GetComponentInParent<GroundEffectZone>() != null)
                return;

            if (_ignoreDamageableEntities && other.GetComponentInParent<IDamageable>() != null)
                return; // Entity - pass through untouched (see class header).

            if ((_excludedLayers.value & (1 << other.gameObject.layer)) != 0)
                return; // Hard-excluded layer - pass through.

            GetSurfaceContact(other, out Vector3 point, out Vector3 normal);
            ResolveHit(point, normal);
        }

        /// <summary>
        /// Computes an attach point/normal on <paramref name="other"/> without
        /// Collider.ClosestPoint - that method (and its Physics.ClosestPoint
        /// static twin) only supports Box/Sphere/Capsule/convex Mesh colliders
        /// and throws on anything else, which includes most non-convex
        /// terrain/level MeshColliders (exactly what world geometry tends to
        /// use). Collider.Raycast has no such restriction - it works on every
        /// collider type - so this casts from just behind the hook's current
        /// position back along its direction of travel to find the real
        /// surface point/normal it just flew into.
        /// </summary>
        private void GetSurfaceContact(Collider other, out Vector3 point, out Vector3 normal)
        {
            Vector3 travelDir = _rigidbody.linearVelocity.sqrMagnitude > 0.0001f
                ? _rigidbody.linearVelocity.normalized
                : transform.forward;

            const float castBack = 1f;
            Ray ray = new Ray(transform.position - travelDir * castBack, travelDir);

            if (other.Raycast(ray, out RaycastHit hit, castBack + 0.5f))
            {
                point = hit.point;
                normal = hit.normal;
                return;
            }

            // Fallback: Bounds.ClosestPoint is plain AABB math with no
            // convexity requirement, so it never throws - just less precise
            // on angled surfaces. Only reached if the raycast above somehow
            // misses (e.g. the hook is already past the surface this frame).
            point = other.bounds.ClosestPoint(transform.position);
            normal = -travelDir;
        }

        // ------------------------------------------------------------------
        // Server-only resolution
        // ------------------------------------------------------------------

        private void ResolveHit(Vector3 point, Vector3 normal)
        {
            _resolved = true;
            _attached = true;

            // Freeze in place at the attach point - stays spawned as the
            // visual anchor/rope endpoint until PlayerGrapple.ServerRelease()
            // despawns it (pull completed, cancelled, or owner died).
            _rigidbody.linearVelocity = Vector3.zero;
            _rigidbody.isKinematic = true;
            transform.position = point;

            RpcAttached(point, normal);
            _onResolved?.Invoke(true, point, normal);
        }

        private void ResolveMiss()
        {
            _resolved = true;
            _initialized = false;

            RpcMissed();
            _onResolved?.Invoke(false, Vector3.zero, Vector3.zero);
            ServerDespawn();
        }

        /// <summary>
        /// Server-only. Called by PlayerGrapple once a hook's outcome is no
        /// longer needed - either it never attached (in-flight cancel) or an
        /// attached pull has ended (arrival, slingshot cancel, or death).
        /// Safe to call multiple times/on an already-despawned hook.
        /// </summary>
        public void ServerRelease()
        {
            if (!IsServerInitialized) return;
            RpcReleased();
            ServerDespawn();
        }

        private void ServerDespawn()
        {
            if (!IsServerInitialized) return;
            _initialized = false;
            base.NetworkObject.Despawn();
        }

        // ------------------------------------------------------------------
        // Cosmetic feedback (UX only - never mutates game state)
        // ------------------------------------------------------------------

        [ObserversRpc]
        private void RpcAttached(Vector3 point, Vector3 normal) => GrappleEvents.RaiseHookAttached(_ownerSync.Value, point, normal);

        [ObserversRpc]
        private void RpcMissed() => GrappleEvents.RaiseHookMissed(_ownerSync.Value);

        [ObserversRpc]
        private void RpcReleased() => GrappleEvents.RaiseHookReleased(_ownerSync.Value);
    }
}
