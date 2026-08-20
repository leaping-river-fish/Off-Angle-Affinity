// =============================================================================
// FireParticleDiagnostic — TEMPORARY. Attach to the "Fire Particles" child to
// log its active/enabled/playing state every frame for 2 seconds after it
// becomes active, so we can see exactly when/why it stops rendering on the
// fireball. Delete this file and the component once the bug is found.
// =============================================================================

using UnityEngine;

namespace OffAngle.Debugging
{
    public class FireParticleDiagnostic : MonoBehaviour
    {
        private ParticleSystem _particleSystem;
        private ParticleSystemRenderer _renderer;
        private float _startTime;

        private void OnEnable()
        {
            _particleSystem = GetComponent<ParticleSystem>();
            _renderer = GetComponent<ParticleSystemRenderer>();
            _startTime = Time.time;
            Debug.Log($"[FireParticleDiag] OnEnable '{gameObject.name}' t=0.000 activeInHierarchy={gameObject.activeInHierarchy} worldPos={transform.position} localScale={transform.localScale}");
        }

        private void OnDisable()
        {
            Debug.Log($"[FireParticleDiag] OnDisable '{gameObject.name}' t={Time.time - _startTime:F3}");
        }

        private void Update()
        {
            float elapsed = Time.time - _startTime;
            if (elapsed > 2f) return;

            bool? rendererEnabled = _renderer != null ? _renderer.enabled : (bool?)null;
            bool? isPlaying = _particleSystem != null ? _particleSystem.isPlaying : (bool?)null;
            int count = _particleSystem != null ? _particleSystem.particleCount : -1;
            float psTime = _particleSystem != null ? _particleSystem.time : -1f;
            bool? emissionEnabled = _particleSystem != null ? _particleSystem.emission.enabled : (bool?)null;
            float rate = _particleSystem != null ? _particleSystem.emission.rateOverTime.constant : -1f;

            string particleDetail = "";
            if (_particleSystem != null && count > 0)
            {
                ParticleSystem.Particle[] buffer = new ParticleSystem.Particle[count];
                int written = _particleSystem.GetParticles(buffer);
                if (written > 0)
                {
                    ParticleSystem.Particle p = buffer[0];
                    particleDetail = $" p0[remainingLifetime={p.remainingLifetime:F3} startLifetime={p.startLifetime:F3} position={p.position} startSize={p.startSize:F4}]";
                }
            }

            Debug.Log($"[FireParticleDiag] t={elapsed:F3} active={gameObject.activeInHierarchy} rendererEnabled={rendererEnabled} isPlaying={isPlaying} psTime={psTime:F3} emissionEnabled={emissionEnabled} rate={rate} count={count} worldPos={transform.position} timeScale={Time.timeScale}{particleDetail}");
        }
    }
}
