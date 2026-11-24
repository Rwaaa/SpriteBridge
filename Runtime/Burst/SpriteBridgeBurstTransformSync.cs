using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

namespace SpriteBridge.Burst
{
    /// <summary>
    /// Automatically registers a Burst-compiled SIMD IJobParallelForTransform implementation into
    /// SpriteBridgeTransformSync whenever com.unity.burst is installed in the project.
    /// Zero references required from SpriteBridge.Runtime.asmdef.
    /// </summary>
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    internal static class SpriteBridgeBurstTransformSync
    {
        static SpriteBridgeBurstTransformSync()
        {
            Register();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register()
        {
            SpriteBridgeTransformSync.BurstScheduler = ScheduleBurstSync;
        }

        private static JobHandle ScheduleBurstSync(
            TransformAccessArray transforms,
            NativeArray<GpuSpriteInstance> instances,
            NativeArray<SpriteSyncSlotMeta> slotMeta)
        {
            BurstTransformSyncJob job = new BurstTransformSyncJob
            {
                SlotMeta = slotMeta,
                Instances = instances
            };
            return job.Schedule(transforms);
        }

        [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
        private struct BurstTransformSyncJob : IJobParallelForTransform
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
