// =============================================================================
// BulletTracer — short-lived visual streak drawn from a shot's origin to its
// impact (or max-range) point.
//
// Pure client-side VFX, not a NetworkObject. PlayerWeaponController's
// RpcPlayTracer instantiates one locally on every peer after the server
// resolves a shot — the same "server decides, ObserversRpc informs, client
// renders" pattern Health.RpcOnDamaged uses for damage-number feedback.
//
// Self-configures its own LineRenderer (and a shared fallback material) in
// Awake, so the prefab needs nothing beyond this script attached — no manual
// material/shader setup required.
// =============================================================================

using UnityEngine;
using UnityEngine.Rendering;

namespace OffAngle.Weapons
{
    public class BulletTracer : MonoBehaviour
    {
        [Tooltip("Optional. Leave unset to use a shared runtime-generated material (Sprites/Default), which renders LineRenderer vertex colors correctly under both Built-in and URP.")]
        [SerializeField] private Material _material;

        [SerializeField] private Color _color = new Color(1f, 0.85f, 0.35f, 1f);
        [SerializeField, Min(0.001f)] private float _width = 0.02f;
        [SerializeField, Min(0.01f)] private float _lifetime = 0.06f;

        private static Material _fallbackMaterial;

        private LineRenderer _line;
        private float _deathTime;

        // ------------------------------------------------------------------
        // Setup
        // ------------------------------------------------------------------

        private void Awake()
        {
            _line = GetComponent<LineRenderer>();
            if (_line == null)
                _line = gameObject.AddComponent<LineRenderer>();

            _line.useWorldSpace = true;
            _line.positionCount = 2;
            _line.widthMultiplier = _width;
            _line.shadowCastingMode = ShadowCastingMode.Off;
            _line.receiveShadows = false;
            _line.sharedMaterial = _material != null ? _material : GetFallbackMaterial();
            _line.startColor = _color;
            _line.endColor = _color;
        }

        // ------------------------------------------------------------------
        // Public — called immediately after Instantiate
        // ------------------------------------------------------------------

        /// <summary>Positions the streak and starts its fade-out countdown.</summary>
        public void Play(Vector3 start, Vector3 end)
        {
            _line.SetPosition(0, start);
            _line.SetPosition(1, end);
            _deathTime = Time.time + _lifetime;
        }

        // ------------------------------------------------------------------
        // Fade + self-destroy
        // ------------------------------------------------------------------

        private void Update()
        {
            float remaining = _deathTime - Time.time;
            if (remaining <= 0f)
            {
                Destroy(gameObject);
                return;
            }

            Color c = _color;
            c.a = _color.a * Mathf.Clamp01(remaining / _lifetime);
            _line.startColor = c;
            _line.endColor = c;
        }

        // ------------------------------------------------------------------
        // Fallback material — Sprites/Default multiplies its diffuse by
        // LineRenderer vertex colors and is safe under both the Built-in and
        // Universal render pipelines, so it's a sane default when no
        // designer-supplied material is assigned.
        // ------------------------------------------------------------------

        private static Material GetFallbackMaterial()
        {
            if (_fallbackMaterial != null) return _fallbackMaterial;

            Shader shader = Shader.Find("Sprites/Default")
                ?? Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Unlit/Color");

            _fallbackMaterial = new Material(shader) { hideFlags = HideFlags.DontSave };
            return _fallbackMaterial;
        }
    }
}
