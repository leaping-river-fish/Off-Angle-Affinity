// =============================================================================
// Projectile — server-authoritative networked projectile spawned by
// ProjectileShotBehavior. Rockets/grenades/plasma bolts/arrows.
//
// AUTHORITY:
//   Movement and collision are computed ONLY on the server (IsServerInitialized
//   gate below). Clients never evaluate a collision or apply damage - FishNet's
//   NetworkTransform (added to this prefab by hand, see the Unity walkthrough)
//   replicates the server's position to every peer so everyone sees the same
//   flight path. Because clients never run this class's collision logic,
//   duplicate client/server damage cannot happen by construction - there is no
//   "already hit" flag to get out of sync, there is simply only one place hits
//   are ever evaluated.
//
// LIFECYCLE:
//   ServerInitialize() is called once by ProjectileShotBehavior right after
//   ServerManager.Spawn. From there the server moves it via Rigidbody
//   velocity, watches for a collision or a lifetime timeout, and despawns it
//   through NetworkObject.Despawn() either way - no manual client cleanup
//   needed, FishNet removes the object on every peer automatically.
//
// SETUP (see Unity walkthrough): this prefab needs a NetworkObject,
// NetworkTransform, Rigidbody, and a Collider in addition to this script.
// =============================================================================

using FishNet.Object;
using FishNet.Object.Synchronizing;
using OffAngle.Combat;
using UnityEngine;

namespace OffAngle.Weapons
{
    [RequireComponent(typeof(Rigidbody))]
    public class Projectile : NetworkBehaviour
    {
        // Replicated so remote observers can attribute this projectile to a
        // shooter for future VFX/kill-feed hooks (e.g. ShotEvents.ProjectileSpawned).
        // FishNet requires SyncVar<T> fields to be readonly-initialized.
        private readonly SyncVar<NetworkObject> _attackerSync = new SyncVar<NetworkObject>();

        private Rigidbody _rigidbody;

        // Server-only bookkeeping - never read on clients.
        private Transform _attackerRoot;
        private GunData _weaponData;
        private ProjectileShotBehavior _config;
        private float _despawnAtTime;
        private bool _initialized;
        private bool _impactResolved;

        private void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            // Fires "for free" on every peer via the existing spawn message -
            // no extra RPC needed. Weapon context isn't synced (GunData is a
            // project asset, not a networked object) so it's passed as null to
            // remote observers; the correct visual prefab already tells you
            // which weapon this came from.
            ShotEvents.RaiseProjectileSpawned(_attackerSync.Value, null, base.NetworkObject);
        }

        /// <summary>
        /// Server-only. Called once by ProjectileShotBehavior immediately
        /// after this instance is spawned.
        /// </summary>
        public void ServerInitialize(NetworkObject attacker, Transform attackerRoot, GunData weaponData, ProjectileShotBehavior config, Vector3 velocity)
        {
            if (!IsServerInitialized) return;

            _attackerSync.Value = attacker;
            _attackerRoot = attackerRoot;
            _weaponData = weaponData;
            _config = config;

            _rigidbody.useGravity = config.UseGravity;
            _rigidbody.linearVelocity = velocity;
            _despawnAtTime = Time.time + Mathf.Max(0.01f, config.Lifetime);
            _initialized = true;
        }

        private void Update()
        {
            if (!IsServerInitialized || !_initialized) return;

            if (Time.time >= _despawnAtTime)
                ServerDespawn();
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!IsServerInitialized || !_initialized) return;
            ContactPoint contact = collision.GetContact(0);
            ResolveImpact(collision.collider, contact.point, contact.normal);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!IsServerInitialized || !_initialized) return;
            ResolveImpact(other, transform.position, -transform.forward);
        }

        // ------------------------------------------------------------------
        // Server-only impact resolution
        // ------------------------------------------------------------------

        private void ResolveImpact(Collider hitCollider, Vector3 point, Vector3 normal)
        {
            // A single physics step can raise multiple collision callbacks
            // (e.g. multiple colliders on the same target) - only the first
            // ever resolves damage.
            if (_impactResolved) return;
            _impactResolved = true;

            HitResolution.TryResolveAndApply(hitCollider, point, normal, _attackerRoot, _attackerSync.Value, _weaponData, _weaponData.Damage, _weaponData.HeadshotDamage, out _);

            if (_config.SplashRadius > 0f)
                ApplySplashDamage(point);

            RpcImpacted(point, normal);
            ServerDespawn();
        }

        private void ApplySplashDamage(Vector3 point)
        {
            Collider[] hits = Physics.OverlapSphere(point, _config.SplashRadius, _weaponData.HitMask, QueryTriggerInteraction.Ignore);
            foreach (Collider hit in hits)
            {
                IDamageable damageable = hit.GetComponentInParent<IDamageable>();
                if (damageable == null) continue;
                if (hit.transform.root == _attackerRoot) continue;

                float distance = Vector3.Distance(point, hit.transform.position);
                float falloff = Mathf.Clamp01(1f - (distance / _config.SplashRadius));
                float amount = _config.SplashDamage * falloff;
                if (amount <= 0f) continue;

                Vector3 pushDirection = hit.transform.position - point;
                Vector3 normal = pushDirection.sqrMagnitude > 0.0001f ? pushDirection.normalized : Vector3.up;

                DamageInfo info = new DamageInfo(amount, _attackerSync.Value, _weaponData, _weaponData.Affinity, point, normal, DamageCategory.Normal);
                damageable.ApplyDamage(info);
            }
        }

        private void ServerDespawn()
        {
            if (!IsServerInitialized) return;
            _initialized = false;
            base.NetworkObject.Despawn();
        }

        // ------------------------------------------------------------------
        // Cosmetic feedback (UX only - never mutates game state). One-shot,
        // sent right before despawn - not a per-frame message.
        // ------------------------------------------------------------------

        [ObserversRpc]
        private void RpcImpacted(Vector3 point, Vector3 normal)
        {
            ShotEvents.RaiseProjectileImpacted(_attackerSync.Value, null, point, normal);
        }
    }
}
