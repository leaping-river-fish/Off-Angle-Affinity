// =============================================================================
// GrappleRopeRenderer — draws the visual tether/rope from the player to their
// grapple hook for the hook's entire flight-through-attached lifetime.
//
// WHY THIS EXISTS:
// GrappleHook is a real flying projectile (see GrappleHook.cs's COLLISION
// MODEL header) with no visual line connecting it back to the player. With
// nothing tracing that connection, moving/strafing/looking around mid-pull
// has no spatial anchor to read - the pull can feel disconnected from what
// you're looking at. This draws that missing line every frame.
//
// PURELY COSMETIC (UX only), exactly like BulletTracer.cs - never mutates
// gameplay state and is never read back by GrapplePullDriver/PlayerGrapple.
// Lives on the player prefab (one per player) rather than only under the
// owner's camera, and filters GrappleEvents by comparing the raised owner
// NetworkObject to this instance's own - so each peer's copy of this script
// only reacts to ITS player's hook, and (unlike AmmoHUD/HealthHUD, which are
// owner-only screen-space HUD) this renders for every peer watching that
// player, not just the owner.
//
// LIFECYCLE: HookFired enables the line and follows the hook's own live
// Transform WHILE IN FLIGHT ONLY. HookAttached switches the rope over to a
// cached, fixed world-space point instead of continuing to trust the hook's
// networked Transform - see the ATTACHED POINT note below for why.
// HookMissed/HookReleased disable it again.
//
// ATTACHED POINT, NOT LIVE TRANSFORM:
// Once attached, the hook is meant to sit perfectly motionless (see
// GrappleHook.ResolveHit), but its NetworkTransform has proven unreliable
// for this (e.g. authority/interpolation quirks letting it visually drift
// away from the true embed point over the course of a wall-hold - exactly
// what an actual rope should never do since it's rigidly anchored). Rather
// than keep chasing every possible sync edge case, the attached phase uses
// the exact same fixed point GrappleEvents.HookAttached already carries -
// the SAME value GrapplePullDriver's anchor is built from (see
// PlayerGrapple.TargetRpcHookAttached), which is already proven correct
// since arrival always lands you in the right place. This makes the
// visually-critical stationary phase immune to ANY future hook-transform
// sync issue by construction, rather than needing yet another network fix.
// =============================================================================
using FishNet.Object;
using UnityEngine;

namespace OffAngle.Movement.Grapple
{
    public class GrappleRopeRenderer : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("This player's own NetworkObject - used to filter GrappleEvents down to just this player's hook. Leave unset to auto-find via GetComponentInParent.")]
        [SerializeField] private NetworkObject _playerNetworkObject;
        [Tooltip("World-space point the rope should originate from (e.g. a hand bone, the gun's FirePoint, or the camera). Required - the rope stays hidden without one.")]
        [SerializeField] private Transform _ropeOrigin;
        [Tooltip("Optional. Leave unset to use a shared runtime-generated material (Sprites/Default), same fallback BulletTracer.cs uses.")]
        [SerializeField] private Material _material;

        [Header("Appearance")]
        [SerializeField] private Color _color = new Color(0.85f, 0.85f, 0.9f, 1f);
        [SerializeField, Min(0.001f)] private float _width = 0.03f;

        private static Material _fallbackMaterial;

        private LineRenderer _line;
        private Transform _activeHookTransform;
        private bool _isAttached;
        private Vector3 _attachedPoint;

        // ------------------------------------------------------------------
        // Setup
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_playerNetworkObject == null)
                _playerNetworkObject = GetComponentInParent<NetworkObject>();

            _line = GetComponent<LineRenderer>();
            if (_line == null)
                _line = gameObject.AddComponent<LineRenderer>();

            _line.useWorldSpace = true;
            _line.positionCount = 2;
            _line.widthMultiplier = _width;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows = false;
            _line.sharedMaterial = _material != null ? _material : GetFallbackMaterial();
            _line.startColor = _color;
            _line.endColor = _color;
            _line.enabled = false;
        }

        private void Start()
        {
            if (_playerNetworkObject == null)
                Debug.LogWarning($"[{nameof(GrappleRopeRenderer)}] No NetworkObject assigned or found in parents for '{name}'.", this);
            if (_ropeOrigin == null)
                Debug.LogWarning($"[{nameof(GrappleRopeRenderer)}] No Rope Origin assigned for '{name}' - the rope will never appear.", this);

            GrappleEvents.HookFired += HandleHookFired;
            GrappleEvents.HookAttached += HandleHookAttached;
            GrappleEvents.HookMissed += HandleHookEnded;
            GrappleEvents.HookReleased += HandleHookEnded;
        }

        private void OnDestroy()
        {
            GrappleEvents.HookFired -= HandleHookFired;
            GrappleEvents.HookAttached -= HandleHookAttached;
            GrappleEvents.HookMissed -= HandleHookEnded;
            GrappleEvents.HookReleased -= HandleHookEnded;
        }

        // ------------------------------------------------------------------
        // GrappleEvents handlers
        // ------------------------------------------------------------------

        private void HandleHookFired(NetworkObject owner, Transform hookTransform, Vector3 origin, Vector3 direction)
        {
            if (owner != _playerNetworkObject) return;
            if (_isAttached) return;

            _activeHookTransform = hookTransform;
            _isAttached = false;
            _line.enabled = _ropeOrigin != null;
        }

        /// <summary>
        /// Switches the rope from following the hook's live (networked)
        /// Transform to a cached fixed point - see this file's ATTACHED
        /// POINT header note for why.
        /// </summary>
        private void HandleHookAttached(NetworkObject owner, Vector3 point, Vector3 normal)
        {
            if (owner != _playerNetworkObject) return;

            _attachedPoint = point;
            _isAttached = true;
        }

        private void HandleHookEnded(NetworkObject owner)
        {
            if (owner != _playerNetworkObject) return;

            _activeHookTransform = null;
            _isAttached = false;
            _line.enabled = false;
        }

        // ------------------------------------------------------------------
        // Per-frame line update
        // ------------------------------------------------------------------

        private void LateUpdate()
        {
            if (_ropeOrigin == null)
                return;

            if (_isAttached)
            {
                _line.SetPosition(0, _ropeOrigin.position);
                _line.SetPosition(1, _attachedPoint);
                return;
            }

            if (_activeHookTransform == null)
                return;

            // The hook can be despawned by the server a moment after
            // HookMissed/HookReleased fires - guard against the Unity "fake
            // null" window between despawn and this component's own event
            // handler running.
            if (!_activeHookTransform)
            {
                _activeHookTransform = null;
                _line.enabled = false;
                return;
            }

            _line.SetPosition(0, _ropeOrigin.position);
            _line.SetPosition(1, _activeHookTransform.position);
        }

        // ------------------------------------------------------------------
        // Fallback material - see BulletTracer.cs for why Sprites/Default
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
