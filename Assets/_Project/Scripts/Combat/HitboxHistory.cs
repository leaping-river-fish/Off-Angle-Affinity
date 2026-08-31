// =============================================================================
// HitboxHistory — server-only ring buffer of hitbox world poses.
//
// Each networked damageable (player, dummy) that a hitscan can hit registers
// the transforms that actually have colliders (body + head). Every FishNet
// tick the server stores position/rotation. Later lag compensation will
// temporarily move those colliders back to a FireTick, raycast, then restore.
//
// Clients never run the buffer. World geometry is not registered.
// =============================================================================
// Record: every server FishNet tick, store world pose of assigned colliders
// (body + head). Clients never record. Walls are not registered.
//
// Rewind: HitboxHistoryRegistry.Rewind moves those colliders to FireTick
// minus NetworkTransform interpolation ticks, SyncTransforms, hitscan
// raycasts, Restore puts them back. Same-frame only.
// =============================================================================

using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;
namespace OffAngle.Combat
{
    public static class HitboxHistoryRegistry
    {
        private static readonly List<HitboxHistory> _all = new List<HitboxHistory>();
        private static bool _rewound;
        public static IReadOnlyList<HitboxHistory> All => _all;
        public static void Add(HitboxHistory history)
        {
            if (history == null) return;
            if (_all.Contains(history)) return;
            _all.Add(history);
        }
        public static void Remove(HitboxHistory history)
        {
            if (history == null) return;
            _all.Remove(history);
        }
        /// <summary>
        /// interpolationTicks should match spectator NetworkTransform interpolation (2 on the player).
        /// </summary>
        public static void Rewind(uint fireTick, int interpolationTicks = 2)
        {
            if (_rewound)
                Restore();
            uint extra = interpolationTicks > 0 ? (uint)interpolationTicks : 0u;
            uint targetTick = fireTick > extra ? fireTick - extra : 0u;
            for (int i = 0; i < _all.Count; i++)
                _all[i].RewindTo(targetTick);
            Physics.SyncTransforms();
            _rewound = true;
        }
        public static void Restore()
        {
            if (!_rewound)
                return;
            for (int i = 0; i < _all.Count; i++)
                _all[i].RestoreFromRewind();
            Physics.SyncTransforms();
            _rewound = false;
        }
    }
    [DisallowMultipleComponent]
    public class HitboxHistory : NetworkBehaviour
    {
        private struct PoseSample
        {
            public uint Tick;
            public Vector3 Position;
            public Quaternion Rotation;
        }
        [Header("Colliders to record")]
        [Tooltip("World poses recorded each server tick. Assign the body (player root / CharacterController) and the Head child. Do not assign walls.")]
        [SerializeField] private Transform[] _colliders;
        [Header("Buffer")]
        [Tooltip("How many seconds of pose history to keep. Must be >= the gameplay FireTick age cap (0.25s). Extra margin so the cap is policy, not an empty buffer.")]
        [SerializeField, Min(0.05f)] private float _historySeconds = 0.4f;
        [Header("Debug")]
        [Tooltip("When > 0, logs buffer status on this interval. Set to 0 after you confirm recording works.")]
        [SerializeField, Min(0f)] private float _debugLogIntervalSeconds = 0f;
        private PoseSample[][] _buffers;
        private PoseSample[] _restore;
        private bool[] _characterControllerWasEnabled;
        private int _capacity;
        private int _writeIndex;
        private int _count;
        private float _nextDebugLogTime;
        private bool _rewound;
        public override void OnStartServer()
        {
            base.OnStartServer();
            AllocateBuffers();
            TimeManager.OnTick += RecordTick;
            HitboxHistoryRegistry.Add(this);
        }
        public override void OnStopServer()
        {
            if (_rewound)
                RestoreFromRewind();
            if (TimeManager != null)
                TimeManager.OnTick -= RecordTick;
            HitboxHistoryRegistry.Remove(this);
            base.OnStopServer();
        }
        private void AllocateBuffers()
        {
            float tickRate = TimeManager != null && TimeManager.TickRate > 0
                ? TimeManager.TickRate
                : 60f;
            _capacity = Mathf.Max(2, Mathf.CeilToInt(_historySeconds * tickRate));
            int colliderCount = _colliders != null ? _colliders.Length : 0;
            _buffers = new PoseSample[colliderCount][];
            _restore = new PoseSample[colliderCount];
            _characterControllerWasEnabled = new bool[colliderCount];
            for (int i = 0; i < colliderCount; i++)
                _buffers[i] = new PoseSample[_capacity];
            _writeIndex = 0;
            _count = 0;
        }
        private void RecordTick()
        {
            if (_rewound)
                return;
            if (_buffers == null || _buffers.Length == 0)
                return;
            uint tick = (uint)TimeManager.Tick;
            for (int i = 0; i < _colliders.Length; i++)
            {
                Transform t = _colliders[i];
                if (t == null)
                    continue;
                t.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
                _buffers[i][_writeIndex] = new PoseSample
                {
                    Tick = tick,
                    Position = position,
                    Rotation = rotation
                };
            }
            _writeIndex = (_writeIndex + 1) % _capacity;
            if (_count < _capacity)
                _count++;
            if (_debugLogIntervalSeconds > 0f && Time.unscaledTime >= _nextDebugLogTime)
            {
                _nextDebugLogTime = Time.unscaledTime + _debugLogIntervalSeconds;
                Vector3 head = _colliders != null && _colliders.Length > 1 && _colliders[1] != null
                    ? _colliders[1].position
                    : transform.position;
                Debug.Log(
                    $"[HitboxHistory] {name} tick={tick} samples={_count}/{_capacity} colliders={_colliders.Length} head={head}",
                    this);
            }
        }
        internal void RewindTo(uint targetTick)
        {
            if (_rewound || _colliders == null || _count == 0)
                return;
            for (int i = 0; i < _colliders.Length; i++)
            {
                Transform t = _colliders[i];
                if (t == null)
                    continue;
                t.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
                _restore[i] = new PoseSample { Position = position, Rotation = rotation };
                if (!TryGetPose(i, targetTick, out Vector3 historicalPosition, out Quaternion historicalRotation))
                    continue;
                ApplyWorldPose(t, i, historicalPosition, historicalRotation);
            }
            _rewound = true;
        }
        internal void RestoreFromRewind()
        {
            if (!_rewound)
                return;
            for (int i = 0; i < _colliders.Length; i++)
            {
                Transform t = _colliders[i];
                if (t == null)
                    continue;
                ApplyWorldPose(t, i, _restore[i].Position, _restore[i].Rotation);
            }
            _rewound = false;
        }
        private void ApplyWorldPose(Transform t, int colliderIndex, Vector3 position, Quaternion rotation)
        {
            CharacterController cc = t.GetComponent<CharacterController>();
            if (cc != null)
            {
                _characterControllerWasEnabled[colliderIndex] = cc.enabled;
                if (cc.enabled)
                    cc.enabled = false;
            }
            t.SetPositionAndRotation(position, rotation);
            if (cc != null)
                cc.enabled = _characterControllerWasEnabled[colliderIndex];
        }
        private bool TryGetPose(int colliderIndex, uint targetTick, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;
            PoseSample older = default;
            PoseSample newer = default;
            bool hasOlder = false;
            bool hasNewer = false;
            for (int k = 0; k < _count; k++)
            {
                int slot = (_writeIndex - _count + k + _capacity) % _capacity;
                PoseSample sample = _buffers[colliderIndex][slot];
                if (sample.Tick <= targetTick)
                {
                    older = sample;
                    hasOlder = true;
                }
                if (sample.Tick >= targetTick && !hasNewer)
                {
                    newer = sample;
                    hasNewer = true;
                }
            }
            if (hasOlder && hasNewer)
            {
                if (newer.Tick == older.Tick)
                {
                    position = older.Position;
                    rotation = older.Rotation;
                    return true;
                }
                float alpha = (targetTick - older.Tick) / (float)(newer.Tick - older.Tick);
                position = Vector3.Lerp(older.Position, newer.Position, alpha);
                rotation = Quaternion.Slerp(older.Rotation, newer.Rotation, alpha);
                return true;
            }
            if (hasOlder)
            {
                position = older.Position;
                rotation = older.Rotation;
                return true;
            }
            if (hasNewer)
            {
                position = newer.Position;
                rotation = newer.Rotation;
                return true;
            }
            return false;
        }
    }
}