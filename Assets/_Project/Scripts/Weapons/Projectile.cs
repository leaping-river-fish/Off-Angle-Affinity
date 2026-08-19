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
// COLLISION MODEL:
//   Fast projectiles cannot rely on OnTriggerEnter alone. At typical speeds
//   (40+ m/s) a small collider travels most of a meter per physics step, so
//   discrete trigger overlaps tunnel through thin/angled geometry and small
//   hitboxes (heads) depending on approach angle. Hit detection is therefore
//   a swept SphereCast along the motion each FixedUpdate - same approach FPS
//   projectiles use - with OnTriggerEnter kept only as a slow-speed fallback.
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

using FishNet;
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
        private float _castRadius;

        // Server-only bookkeeping - never read on clients.
        private Transform _attackerRoot;
        private GunData _weaponData;
        private ProjectileShotBehavior _config;
        private float _despawnAtTime;
        private bool _initialized;
        private bool _impactResolved;
        private Vector3 _previousPosition;

        private void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();
            // Speculative CCD works with triggers; ContinuousDynamic does not.
            // Still secondary to the swept cast below - kept for the fallback
            // OnTriggerEnter path and for solid (non-trigger) collider setups.
            _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            _castRadius = ResolveCastRadius();
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
            _previousPosition = _rigidbody.position;
            _initialized = true;
        }

        private void Update()
        {
            if (!IsServerInitialized || !_initialized) return;

            if (Time.time >= _despawnAtTime)
                ServerDespawn();
        }

        private void FixedUpdate()
        {
            if (!IsServerInitialized || !_initialized || _impactResolved) return;

            Vector3 current = _rigidbody.position;
            Vector3 delta = current - _previousPosition;
            float distance = delta.magnitude;

            if (distance > 0.0001f)
            {
                Vector3 direction = delta / distance;
                LayerMask mask = _weaponData != null ? _weaponData.HitMask : ~0;
                RaycastHit[] hits = Physics.SphereCastAll(
                    _previousPosition,
                    _castRadius,
                    direction,
                    distance,
                    mask,
                    QueryTriggerInteraction.Ignore);

                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

                foreach (RaycastHit hit in hits)
                {
                    if (_attackerRoot != null && hit.collider.transform.root == _attackerRoot)
                        continue;

                    // Ground-effect zones (fire patches, ...) are triggers a
                    // projectile should fly straight through, not detonate
                    // against - otherwise aiming repeatedly at the same spot
                    // stacks zones instead of refreshing/overlapping them.
                    if (hit.collider.GetComponentInParent<GroundEffectZone>() != null)
                        continue;

                    ResolveImpact(hit.collider, hit.point, hit.normal);
                    break;
                }
            }

            _previousPosition = _rigidbody.position;
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

            // Muzzle spawn often overlaps the shooter's capsule - ignore those
            // contacts so the slug doesn't instantly despawn on the firer.
            if (_attackerRoot != null && hitCollider.transform.root == _attackerRoot)
                return;

            // Same reasoning as the sphere-cast loop's check above - this is
            // the backstop for the OnCollisionEnter/OnTriggerEnter fallback
            // paths, which have no "try the next hit" loop to fall through to.
            if (hitCollider.GetComponentInParent<GroundEffectZone>() != null)
                return;

            _impactResolved = true;
            _rigidbody.linearVelocity = Vector3.zero;
            _rigidbody.position = point;

            HitResolution.TryResolveAndApply(hitCollider, point, normal, _attackerRoot, _attackerSync.Value, _weaponData, _weaponData.Damage, _weaponData.HeadshotDamage, out _);

            if (_config.SplashRadius > 0f)
                ApplySplashDamage(point);

            if (_config.ImpactZonePrefab != null)
                SpawnImpactZone(point, hitCollider.transform.root);

            RpcImpacted(point, normal);
            ServerDespawn();
        }

        /// <summary>Server-only. Spawns _config.ImpactZonePrefab (e.g. a fireball's burning patch) on the floor beneath the impact point, attributed to this projectile's attacker.</summary>
        private void SpawnImpactZone(Vector3 point, Transform hitRoot)
        {
            GroundEffectZone prefab = _config.ImpactZonePrefab;
            if (prefab == null || prefab.NetworkObject == null) return;

            Vector3 spawnPoint = ResolveGroundPoint(point, hitRoot);

            NetworkObject instance = Instantiate(prefab.NetworkObject, spawnPoint, Quaternion.identity);
            InstanceFinder.ServerManager.Spawn(instance);

            GroundEffectZone zone = instance.GetComponent<GroundEffectZone>();
            zone?.Initialize(_attackerSync.Value);
        }

        /// <summary>
        /// Finds the floor beneath an impact point via a downward raycast, so
        /// a direct hit on a player (or anything else standing above the
        /// ground) still leaves the ground-effect zone sitting on the floor
        /// instead of floating at impact height. hitRoot is excluded from the
        /// probe so it never re-detects the very thing that was just hit as
        /// "the floor" - e.g. a headshot's probe skips that player's own body/
        /// head colliders and keeps going down to the real ground. Falls back
        /// to the raw impact point if no floor is found within range (e.g. an
        /// impact over a pit).
        /// </summary>
        private Vector3 ResolveGroundPoint(Vector3 point, Transform hitRoot)
        {
            const float probeUpOffset = 0.2f;
            const float probeDistance = 50f;

            Vector3 origin = point + Vector3.up * probeUpOffset;
            LayerMask mask = _weaponData != null ? _weaponData.HitMask : ~0;

            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, probeDistance, mask, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (RaycastHit hit in hits)
            {
                // Only skip re-detecting the very thing we just hit as "the
                // floor" when it was a living target (a player/Dummy's own
                // body) - if the fireball hit static terrain directly (a
                // ramp, wall, platform, ledge), that IS legitimate ground and
                // must not be tunnelled through down to whatever sits
                // further below it.
                if (hitRoot != null && hit.collider.transform.root == hitRoot && hitRoot.GetComponent<IDamageable>() != null)
                    continue;
                if (hit.collider.GetComponentInParent<GroundEffectZone>() != null) continue;
                return hit.point;
            }

            return point;
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

        private float ResolveCastRadius()
        {
            if (TryGetComponent(out SphereCollider sphere))
            {
                Vector3 scale = transform.lossyScale;
                float maxScale = Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
                return Mathf.Max(0.01f, sphere.radius * maxScale);
            }

            Bounds bounds = GetComponent<Collider>().bounds;
            return Mathf.Max(0.01f, Mathf.Max(bounds.extents.x, Mathf.Max(bounds.extents.y, bounds.extents.z)));
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
