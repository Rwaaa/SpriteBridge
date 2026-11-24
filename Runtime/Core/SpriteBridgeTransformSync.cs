using System;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

namespace SpriteBridge
{
    /// <summary>
    /// Zero-GC TransformAccessArray + IJobParallelForTransform synchronizer for DynamicTransform sprites.
    /// Uses CoreModule IJobParallelForTransform out of the box (0 external dependencies) and automatically
    /// upgrades to a Burst-compiled SIMD parallel job when SpriteBridge.Burst is active (com.unity.burst installed).
    /// </summary>
    public sealed class SpriteBridgeTransformSync : IDisposable
    {
        public delegate JobHandle BurstScheduleDelegate(
            TransformAccessArray transforms,
            NativeArray<GpuSpriteInstance> instances,
            NativeArray<SpriteSyncSlotMeta> slotMeta);

        /// <summary>
        /// Registered automatically by SpriteBridge.Burst when com.unity.burst is present.
        /// </summary>
        public static BurstScheduleDelegate BurstScheduler;

        private TransformAccessArray _transformAccessArray;
        private NativeArray<SpriteSyncSlotMeta> _slotMeta;
        private int _count;
        private int _capacity;
        private bool _isCreated;

        public int DynamicCount => _count;
        public bool IsUsingBurst => BurstScheduler != null;

        public SpriteBridgeTransformSync(int initialCapacity = 256)
        {
            _capacity = Mathf.Max(64, initialCapacity);
            _transformAccessArray = new TransformAccessArray(_capacity);
            _slotMeta = new NativeArray<SpriteSyncSlotMeta>(_capacity, Allocator.Persistent);
            _count = 0;
            _isCreated = true;
        }

        public int Add(Transform transform, int instanceSlotIndex, float flipSignX, float flipSignY, float baseSortKey)
        {
            if (!_isCreated || transform == null)
            {
                return -1;
            }

            if (_count >= _capacity)
            {
                GrowCapacity(_capacity * 2);
            }

            int dynamicIndex = _count;
            _transformAccessArray.Add(transform);
            _slotMeta[dynamicIndex] = new SpriteSyncSlotMeta
            {
                InstanceSlotIndex = instanceSlotIndex,
                FlipSignX = flipSignX,
                FlipSignY = flipSignY,
                BaseSortKey = baseSortKey
            };
            _count++;
            return dynamicIndex;
        }

        public void UpdateMeta(int dynamicIndex, int instanceSlotIndex, float flipSignX, float flipSignY, float baseSortKey)
        {
            if (!_isCreated || dynamicIndex < 0 || dynamicIndex >= _count)
            {
                return;
            }

            _slotMeta[dynamicIndex] = new SpriteSyncSlotMeta
            {
                InstanceSlotIndex = instanceSlotIndex,
                FlipSignX = flipSignX,
                FlipSignY = flipSignY,
                BaseSortKey = baseSortKey
            };
        }

        public void UpdateTargetInstanceSlot(int dynamicIndex, int newInstanceSlotIndex)
        {
            if (!_isCreated || dynamicIndex < 0 || dynamicIndex >= _count)
            {
                return;
            }

            SpriteSyncSlotMeta meta = _slotMeta[dynamicIndex];
            meta.InstanceSlotIndex = newInstanceSlotIndex;
            _slotMeta[dynamicIndex] = meta;
        }

        /// <summary>
        /// Swap-and-Pop removal in O(1). Returns the instanceSlotIndex of the element that was moved
        /// into dynamicIndex (or -1 if removed element was the last item).
        /// </summary>
        public int RemoveAtSwapBack(int dynamicIndex, out int movedInstanceSlot)
        {
            movedInstanceSlot = -1;
            if (!_isCreated || dynamicIndex < 0 || dynamicIndex >= _count)
            {
                return -1;
            }

            int lastIndex = _count - 1;
            _transformAccessArray.RemoveAtSwapBack(dynamicIndex);

            if (dynamicIndex != lastIndex)
            {
                SpriteSyncSlotMeta moved = _slotMeta[lastIndex];
                _slotMeta[dynamicIndex] = moved;
                movedInstanceSlot = moved.InstanceSlotIndex;
            }

            _count--;
            return dynamicIndex;
        }

        public JobHandle ScheduleSync(NativeArray<GpuSpriteInstance> instances)
        {
            if (!_isCreated || _count <= 0 || !instances.IsCreated)
            {
                return default;
            }

            if (BurstScheduler != null)
            {
                return BurstScheduler(_transformAccessArray, instances, _slotMeta);
            }

            ManagedTransformSyncJob fallbackJob = new ManagedTransformSyncJob
            {
                SlotMeta = _slotMeta,
                Instances = instances
            };
            return fallbackJob.Schedule(_transformAccessArray);
        }

        private void GrowCapacity(int newCapacity)
        {
            int targetCap = Mathf.Max(newCapacity, _capacity * 2);

            TransformAccessArray newTransforms = new TransformAccessArray(targetCap);
            for (int i = 0; i < _count; i++)
            {
                newTransforms.Add(_transformAccessArray[i]);
            }
            _transformAccessArray.Dispose();
            _transformAccessArray = newTransforms;

            NativeArray<SpriteSyncSlotMeta> newMeta = new NativeArray<SpriteSyncSlotMeta>(targetCap, Allocator.Persistent);
            if (_count > 0)
            {
                NativeArray<SpriteSyncSlotMeta>.Copy(_slotMeta, newMeta, _count);
            }
            _slotMeta.Dispose();
            _slotMeta = newMeta;

            _capacity = targetCap;
        }

        public void Dispose()
        {
            if (!_isCreated)
            {
                return;
            }

            if (_transformAccessArray.isCreated)
            {
                _transformAccessArray.Dispose();
            }

            if (_slotMeta.IsCreated)
            {
                _slotMeta.Dispose();
            }

            _count = 0;
            _capacity = 0;
            _isCreated = false;
        }

        /// <summary>
        /// Zero-dependency multi-threaded fallback job (UnityEngine.CoreModule).
        /// Extracts exact signed lossyScale from localToWorldMatrix without sqrt.
        /// </summary>
        private struct ManagedTransformSyncJob : IJobParallelForTransform
        {
            [ReadOnly] public NativeArray<SpriteSyncSlotMeta> SlotMeta;
            [NativeDisableParallelForRestriction] public NativeArray<GpuSpriteInstance> Instances;

            public void Execute(int index, TransformAccess transform)
            {
                if (!transform.isValid)
                {
                    return;
                }

                SpriteSyncSlotMeta meta = SlotMeta[index];
                int slot = meta.InstanceSlotIndex;
                if (slot < 0 || slot >= Instances.Length)
                {
                    return;
                }

                GpuSpriteInstance inst = Instances[slot];
                Vector3 pos = transform.position;
                Quaternion rot = transform.rotation;
                Matrix4x4 l2w = transform.localToWorldMatrix;

                Vector3 rotRight = rot * Vector3.right;
                Vector3 rotUp = rot * Vector3.up;
                Vector3 rotFwd = rot * Vector3.forward;

                Vector3 c0 = new Vector3(l2w.m00, l2w.m10, l2w.m20);
                Vector3 c1 = new Vector3(l2w.m01, l2w.m11, l2w.m21);
                Vector3 c2 = new Vector3(l2w.m02, l2w.m12, l2w.m22);

                float sx = Vector3.Dot(c0, rotRight) * meta.FlipSignX;
                float sy = Vector3.Dot(c1, rotUp) * meta.FlipSignY;
                float sz = Vector3.Dot(c2, rotFwd);

                inst.Position = pos;
                inst.Rotation = new Vector4(rot.x, rot.y, rot.z, rot.w);
                inst.Scale = new Vector3(sx, sy, sz);
                Instances[slot] = inst;
            }
        }
    }
}
