// =============================================================================
// BulletTracer — short-lived visual streak drawn from a shot's origin to its
// impact (or max-range) point.
//
// Pure client-side VFX, not a NetworkObject. PlayerWeaponController's
// RpcPlayTracer instantiates one locally on every peer after the server
// resolves a shot — "server decides, ObserversRpc informs, client renders".
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
        [SerializeField] private Color _color = new Color(1f, 0.95f, 0.55f, 1f);

        [SerializeField, Min(0.001f)] private float _width = 0.08f;
        [SerializeField, Min(0.05f)] private float _tracerLength = 0.8f;
        [SerializeField, Min(0.02f)] private float _travelTime = 0.12f;
        [SerializeField, Range(0, 16)] private int _capVertices = 8;

        private static Material _fallbackMaterial;

        private LineRenderer _line;
        private Vector3 _start, _end, _dir;
        private float _elapsed;
        private float _distance;

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
            _line.numCapVertices = _capVertices;
            _line.numCornerVertices = 0;
            _line.widthCurve = new AnimationCurve(
                new Keyframe(0f, 0.6f),  // tail (thin)
                new Keyframe(0.7f, 0.45f),
                new Keyframe(1f, 1f)      // head (pill)
            );

            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(_color, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),      // muzzle: gone
                    new GradientAlphaKey(0.2f, 0.65f),
                    new GradientAlphaKey(1f, 1f)       // head: bright
                });
            _line.colorGradient = gradient;
        }

        // ------------------------------------------------------------------
        // Public — called immediately after Instantiate
        // ------------------------------------------------------------------

        /// <summary>Positions the streak and starts its fade-out countdown.</summary>
        public void Play(Vector3 start, Vector3 end)
        {
            _start = start;
            _end = end;
            _dir = end - start;
            _distance = _dir.magnitude;
            _dir = _distance > 0.001f ? _dir / _distance : Vector3.forward;
            _elapsed = 0f;
            ApplyPositions(0f);
        }

        // ------------------------------------------------------------------
        // Fade + self-destroy
        // ------------------------------------------------------------------

        private void Update()
        {
            _elapsed += Time.deltaTime;
            float t = _travelTime > 0f ? _elapsed / _travelTime : 1f;
            if (t >= 1f)
            {
                Destroy(gameObject);
                return;
            }
            ApplyPositions(t);
        }

        
        private void ApplyPositions(float t)
        {
            Vector3 head = Vector3.Lerp(_start, _end, Mathf.Clamp01(t));
            _line.SetPosition(0, _start);
            _line.SetPosition(1, head);
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
