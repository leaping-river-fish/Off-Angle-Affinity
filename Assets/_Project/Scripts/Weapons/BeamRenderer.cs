// =============================================================================
// BeamRenderer — draws the visible laser beam for continuous (beam) weapons
// while a beam is held.
//
// RIGIDLY ATTACHED TO THE GUN WHEN A LIVE FIREPOINT EXISTS (owner):
// ShotEvents.BeamUpdated only arrives at the beam's TickRate (10/sec by
// default - see BeamShotBehavior), which is fine for damage but far too slow
// to redraw a beam's start point/direction every frame without it looking
// like it lags behind the gun as the player turns. So this splits the two:
//   - DIRECTION/ORIGIN: read from the currently equipped Gun's FirePoint
//     every single frame in LateUpdate, so the beam instantly and smoothly
//     tracks wherever the gun currently points (including camera pitch,
//     since FirePoint is nested under the same pivot the camera pitches -
//     see PlayerCameraController's _pitchTarget) - it looks like a fixed
//     part of the gun, never behind, exactly like a laser sight bolted to
//     the barrel.
//   - LENGTH: only updated on each BeamUpdated tick (distance between the
//     networked origin/endPoint), so the point where the beam visually stops
//     (e.g. against a wall) still reflects the last server-confirmed hit.
// A small mismatch between the live direction and the tick-rate hit distance
// is possible for a fraction of a second right after a fast turn, but is far
// less noticeable than freezing the whole beam between ticks.
//
// REMOTE OBSERVERS (no CurrentGun / FirePoint):
// PlayerWeaponEquipper only calls SetGun for the owner's first-person copy
// (and the server's logic copy). Remote peers never get a CurrentGun, so the
// live-FirePoint path above would leave the line disabled forever even though
// RpcBeamVisualUpdate is arriving. When no FirePoint can be resolved, the
// line is drawn directly from the last networked origin/endPoint - same
// approach BulletTracer already uses for discrete shots. Tick-rate stepping
// is acceptable for remotes; they don't have the owner's camera-pitched
// FirePoint to track between ticks anyway.
//
// WHY THE ORIGIN IS RESOLVED THROUGH PlayerWeaponController, NOT A FIXED
// SERIALIZED TRANSFORM:
// PlayerWeaponEquipper instantiates/destroys the actual Gun prefab instance
// at runtime whenever the loadout changes (see its SetGun/RebuildFirstPerson
// flow), so there is no single permanent "Laser FirePoint" Transform sitting
// in the Player prefab to drag into an Inspector field - it only exists
// after a Laser is equipped in Play mode. _weaponController.CurrentGun is
// re-read every frame instead, so this automatically follows whichever gun
// (and whichever gun's own FirePoint offset/angle) is equipped right now,
// with no per-weapon wiring required.
//
// Placeholder look for the Laser: a plain red line for the beam's entire
// held duration, self-configuring its own LineRenderer exactly like
// BulletTracer/GrappleRopeRenderer do - no manual material/shader setup
// required on the prefab.
//
// PURELY COSMETIC (UX only), exactly like BulletTracer.cs/GrappleRopeRenderer
// - never mutates gameplay state and is never read back by
// BeamShotBehavior/PlayerWeaponController. Subscribes to ShotEvents.Beam*
// (raised from PlayerWeaponController's ObserversRpcs) and filters by
// comparing the raised attacker NetworkObject to this instance's own,
// exactly like GrappleRopeRenderer filters GrappleEvents by owner - so each
// peer's copy only reacts to ITS player's beam, but renders for every peer
// watching that player, not just the owner.
// =============================================================================
using FishNet.Object;
using OffAngle.Networking;
using UnityEngine;

namespace OffAngle.Weapons
{
    public class BeamRenderer : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("This player's own NetworkObject - used to filter ShotEvents.Beam* down to just this player's beam. Leave unset to auto-find via GetComponentInParent.")]
        [SerializeField] private NetworkObject _playerNetworkObject;
        [Tooltip("This player's PlayerWeaponController - CurrentGun.FirePoint is read every frame to find the beam's live origin/direction (see header for why this can't just be a fixed Transform field). Leave unset to auto-find via GetComponentInParent.")]
        [SerializeField] private PlayerWeaponController _weaponController;
        [Tooltip("Optional fallback origin used only while no Gun is currently equipped (or _weaponController couldn't be resolved), so this never hard-fails. Safe to leave unset - remotes fall back to networked origin/endPoint instead.")]
        [SerializeField] private Transform _fallbackBeamOrigin;
        [Tooltip("Optional. Leave unset to use a shared runtime-generated material (Sprites/Default), same fallback BulletTracer.cs uses.")]
        [SerializeField] private Material _material;

        [Header("Appearance")]
        [Tooltip("Placeholder tracer color for the laser - plain red for now.")]
        [SerializeField] private Color _color = new Color(1f, 0.05f, 0.05f, 1f);
        [SerializeField, Min(0.001f)] private float _width = 0.03f;

        private static Material _fallbackMaterial;

        private LineRenderer _line;
        private bool _isActive;
        private float _currentDistance;
        private Vector3 _networkedOrigin;
        private Vector3 _networkedEndPoint;

        // ------------------------------------------------------------------
        // Setup
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_playerNetworkObject == null)
                _playerNetworkObject = GetComponentInParent<NetworkObject>();
            if (_weaponController == null)
                _weaponController = GetComponentInParent<PlayerWeaponController>();

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
                Debug.LogWarning($"[{nameof(BeamRenderer)}] No NetworkObject assigned or found in parents for '{name}'.", this);

            ShotEvents.BeamUpdated += HandleBeamUpdated;
            ShotEvents.BeamStopped += HandleBeamStopped;
        }

        private void OnDestroy()
        {
            ShotEvents.BeamUpdated -= HandleBeamUpdated;
            ShotEvents.BeamStopped -= HandleBeamStopped;
        }

        // ------------------------------------------------------------------
        // ShotEvents handlers — cache networked origin/end for remotes, and
        // the hit distance for the owner's live-FirePoint path. See header.
        // ------------------------------------------------------------------

        private void HandleBeamUpdated(NetworkObject attacker, GunData weapon, Vector3 origin, Vector3 endPoint, bool didHit)
        {
            if (attacker != _playerNetworkObject) return;

            _isActive = true;
            _networkedOrigin = origin;
            _networkedEndPoint = endPoint;
            _currentDistance = Vector3.Distance(origin, endPoint);
        }

        private void HandleBeamStopped(NetworkObject attacker, GunData weapon)
        {
            if (attacker != _playerNetworkObject) return;

            _isActive = false;
            _line.enabled = false;
        }

        // ------------------------------------------------------------------
        // Per-frame line update — owner uses live FirePoint + networked
        // length; remotes (no FirePoint) use the last networked segment.
        // ------------------------------------------------------------------

        private void LateUpdate()
        {
            if (!_isActive) return;

            Transform origin = ResolveBeamOrigin();
            Vector3 start;
            Vector3 end;

            if (origin != null)
            {
                start = origin.position;
                end = start + origin.forward * _currentDistance;
            }
            else
            {
                start = _networkedOrigin;
                end = _networkedEndPoint;
            }

            _line.enabled = true;
            _line.SetPosition(0, start);
            _line.SetPosition(1, end);
        }

        /// <summary>Prefers the currently equipped Gun's live FirePoint (re-read every frame - see header); falls back to _fallbackBeamOrigin if no Gun is equipped right now. Returns null when neither is available so LateUpdate can draw from networked points instead.</summary>
        private Transform ResolveBeamOrigin()
        {
            Gun gun = _weaponController != null ? _weaponController.CurrentGun : null;
            if (gun != null && gun.FirePoint != null) return gun.FirePoint;
            return _fallbackBeamOrigin;
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
