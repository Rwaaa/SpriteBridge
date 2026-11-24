using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace SpriteBridge
{
    /// <summary>
    /// Zero-config URP Compute RenderPass injected via RenderPipelineManager.beginCameraRendering.
    /// Executes at RenderPassEvent.BeforeRenderingOpaques (URP 3D) / RenderPassEvent2D.BeforeRendering (URP 2D).
    /// Supports Unity 6 RenderGraph (URP 17+) and Compatibility Mode / Unity 2022.3 LTS (URP 14-16),
    /// including optional per-bucket GPU Bitonic Y-Sort (strictly <= 2 UAVs per kernel).
    /// </summary>
    internal sealed class SpriteBridgeComputePass : ScriptableRenderPass
    {
        private static readonly ProfilingSampler s_CullProfilingSampler = new ProfilingSampler("SpriteBridge_GpuCullAndAnimate");
        private static readonly ProfilingSampler s_SortProfilingSampler = new ProfilingSampler("SpriteBridge_GpuBitonicSort");
        private static readonly Plane[] s_ScratchPlanes = new Plane[6];

        // Cached Shader Property IDs (Zero-GC)
        private static readonly int s_SourceSpritesId = Shader.PropertyToID("_SourceSprites");
        private static readonly int s_AnimClipsId = Shader.PropertyToID("_AnimClips");
        private static readonly int s_AnimFramesId = Shader.PropertyToID("_AnimFrames");
        private static readonly int s_VisibleSpritesId = Shader.PropertyToID("_VisibleSprites");
        private static readonly int s_IndirectArgsId = Shader.PropertyToID("_IndirectArgs");

        private static readonly int s_VisibleSpritesROId = Shader.PropertyToID("_VisibleSpritesRO");
        private static readonly int s_IndirectArgsROId = Shader.PropertyToID("_IndirectArgsRO");
        private static readonly int s_SortEntriesId = Shader.PropertyToID("_SortEntries");

        private static readonly int s_ActiveSpriteCountId = Shader.PropertyToID("_ActiveSpriteCount");
        private static readonly int s_BucketCountId = Shader.PropertyToID("_BucketCount");
        private static readonly int s_MaxInstancesPerBucketId = Shader.PropertyToID("_MaxInstancesPerBucket");
        private static readonly int s_AnimClipCountId = Shader.PropertyToID("_AnimClipCount");
        private static readonly int s_GlobalTimeId = Shader.PropertyToID("_GlobalTime");
        private static readonly int s_EnableFrustumCullingId = Shader.PropertyToID("_EnableFrustumCulling");
        private static readonly int s_FrustumPlanesId = Shader.PropertyToID("_FrustumPlanes");
        private static readonly int s_CameraWorldPosId = Shader.PropertyToID("_CameraWorldPos");
        private static readonly int s_CameraRightWSId = Shader.PropertyToID("_CameraRightWS");
        private static readonly int s_CameraUpWSId = Shader.PropertyToID("_CameraUpWS");
        private static readonly int s_YSortRangeId = Shader.PropertyToID("_YSortRange");

        private static readonly int s_SortBucketIndexId = Shader.PropertyToID("_SortBucketIndex");
        private static readonly int s_SortBaseOffsetId = Shader.PropertyToID("_SortBaseOffset");
        private static readonly int s_SortCountPow2Id = Shader.PropertyToID("_SortCountPow2");
        private static readonly int s_SortKId = Shader.PropertyToID("_SortK");
        private static readonly int s_SortJId = Shader.PropertyToID("_SortJ");

        private readonly SpriteBridgeManager _manager;
        private readonly List<SortedBucketDispatch> _scratchSortDispatches = new List<SortedBucketDispatch>(16);
        private readonly Vector4[] _immediateFrustumPlanes = new Vector4[6];

        internal struct SortedBucketDispatch
        {
            public int BucketIndex;
            public int BaseOffset;
            public int SortCountPow2;
        }

#if !UNITY_6000_0_OR_NEWER || URP_COMPATIBILITY_MODE
        private readonly Vector4[] _compatFrustumPlanes = new Vector4[6];
#endif

        public SpriteBridgeComputePass(SpriteBridgeManager manager)
        {
            _manager = manager;
            renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
        }

        /// <summary>
        /// Built-in Render Pipeline (BiRP) execution path invoked from Camera.onPreCull when no SRP is active.
        /// Dispatches GPU culling, 2D animation evaluation, and optional GPU Bitonic Y-Sort directly.
        /// </summary>
        public void ExecuteImmediateForBuiltInRP(Camera cam)
        {
            if (_manager == null || !_manager.IsSupportedAndReady || _manager.ActiveSpriteCount <= 0 || cam == null)
            {
                return;
            }

            int bucketCount = _manager.ActiveBucketCount;
            ComputeShader cs = _manager.SimulationCompute;
            if (bucketCount <= 0 || cs == null)
            {
                return;
            }

            GeometryUtility.CalculateFrustumPlanes(cam, s_ScratchPlanes);
            for (int i = 0; i < 6; i++)
            {
                Plane p = s_ScratchPlanes[i];
                Vector3 n = p.normal;
                _immediateFrustumPlanes[i] = new Vector4(n.x, n.y, n.z, p.distance);
            }

            Transform camTr = cam.transform;
            Vector3 camPos = camTr.position;
            Vector3 camRight = camTr.right;
            Vector3 camUp = camTr.up;
            float maxCullDistSq = _manager.MaxCullDistance > 0f ? (_manager.MaxCullDistance * _manager.MaxCullDistance) : 0f;
            Vector4 ySortRange = _manager.GetYSortRangeVector();

            int resetKernel = _manager.ResetKernel;
            int cullKernel = _manager.CullAndAnimateKernel;
            int activeSprites = _manager.ActiveSpriteCount;

            cs.SetInt(s_BucketCountId, bucketCount);
            cs.SetBuffer(resetKernel, s_IndirectArgsId, _manager.IndirectArgsBuffer);
            cs.Dispatch(resetKernel, (bucketCount + 63) / 64, 1, 1);

            cs.SetInt(s_ActiveSpriteCountId, activeSprites);
            cs.SetInt(s_MaxInstancesPerBucketId, _manager.MaxInstancesPerBucket);
            cs.SetInt(s_AnimClipCountId, _manager.AnimClipCount);
            cs.SetFloat(s_GlobalTimeId, Application.isPlaying ? Time.time : (float)Time.realtimeSinceStartup);
            cs.SetInt(s_EnableFrustumCullingId, _manager.EnableFrustumCulling ? 1 : 0);
            cs.SetVectorArray(s_FrustumPlanesId, _immediateFrustumPlanes);
            cs.SetVector(s_CameraWorldPosId, new Vector4(camPos.x, camPos.y, camPos.z, maxCullDistSq));
            cs.SetVector(s_CameraRightWSId, new Vector4(camRight.x, camRight.y, camRight.z, 0f));
            cs.SetVector(s_CameraUpWSId, new Vector4(camUp.x, camUp.y, camUp.z, 0f));
            cs.SetVector(s_YSortRangeId, ySortRange);

            cs.SetBuffer(cullKernel, s_SourceSpritesId, _manager.SourceSpritesBuffer);
            cs.SetBuffer(cullKernel, s_AnimClipsId, _manager.AnimClipsBuffer);
            cs.SetBuffer(cullKernel, s_AnimFramesId, _manager.AnimFramesBuffer);
            cs.SetBuffer(cullKernel, s_VisibleSpritesId, _manager.VisibleSpritesBuffer);
            cs.SetBuffer(cullKernel, s_IndirectArgsId, _manager.IndirectArgsBuffer);

            cs.Dispatch(cullKernel, (activeSprites + 63) / 64, 1, 1);

            _manager.CollectSortedBucketDispatches(_scratchSortDispatches);
            if (_scratchSortDispatches.Count > 0 && _manager.SortEntriesBuffer != null && _manager.SortEntriesBuffer.IsValid())
            {
                int buildKernel = _manager.BuildSortEntriesKernel;
                int localKernel = _manager.BitonicLocalKernel;
                int globalKernel = _manager.BitonicGlobalKernel;
                int mergeKernel = _manager.BitonicLocalMergeKernel;

                for (int i = 0; i < _scratchSortDispatches.Count; i++)
                {
                    SortedBucketDispatch sd = _scratchSortDispatches[i];
                    cs.SetInt(s_SortBucketIndexId, sd.BucketIndex);
                    cs.SetInt(s_SortBaseOffsetId, sd.BaseOffset);
                    cs.SetInt(s_SortCountPow2Id, sd.SortCountPow2);

                    cs.SetBuffer(buildKernel, s_VisibleSpritesROId, _manager.VisibleSpritesBuffer);
                    cs.SetBuffer(buildKernel, s_IndirectArgsROId, _manager.IndirectArgsBuffer);
                    cs.SetBuffer(buildKernel, s_SortEntriesId, _manager.SortEntriesBuffer);
                    cs.Dispatch(buildKernel, (sd.SortCountPow2 + 63) / 64, 1, 1);

                    int groups256 = sd.SortCountPow2 / 256;
                    cs.SetBuffer(localKernel, s_SortEntriesId, _manager.SortEntriesBuffer);
                    cs.Dispatch(localKernel, groups256, 1, 1);

                    for (int k = 512; k <= sd.SortCountPow2; k <<= 1)
                    {
                        cs.SetInt(s_SortKId, k);
                        for (int j = k >> 1; j >= 256; j >>= 1)
                        {
                            cs.SetInt(s_SortJId, j);
                            cs.SetBuffer(globalKernel, s_SortEntriesId, _manager.SortEntriesBuffer);
                            cs.Dispatch(globalKernel, groups256, 1, 1);
                        }
                        cs.SetBuffer(mergeKernel, s_SortEntriesId, _manager.SortEntriesBuffer);
                        cs.Dispatch(mergeKernel, groups256, 1, 1);
                    }
                }
            }
        }

#if !UNITY_6000_0_OR_NEWER || URP_COMPATIBILITY_MODE
#if UNITY_6000_0_OR_NEWER
        [System.Obsolete("Compatibility Mode path for URP 14-16 / Unity 6 Compatibility Mode.")]
#endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_manager == null || !_manager.IsSupportedAndReady || _manager.ActiveSpriteCount <= 0)
            {
                return;
            }

            Camera cam = renderingData.cameraData.camera;
            if (cam == null || renderingData.cameraData.isPreviewCamera || !_manager.ShouldRenderForCamera(cam))
            {
                return;
            }

            int bucketCount = _manager.ActiveBucketCount;
            if (bucketCount <= 0)
            {
                return;
            }

            ComputeShader cs = _manager.SimulationCompute;
            if (cs == null)
            {
                return;
            }

            GeometryUtility.CalculateFrustumPlanes(cam, s_ScratchPlanes);
            for (int i = 0; i < 6; i++)
            {
                Plane p = s_ScratchPlanes[i];
                Vector3 n = p.normal;
                _compatFrustumPlanes[i] = new Vector4(n.x, n.y, n.z, p.distance);
            }

            Transform camTr = cam.transform;
            Vector3 camPos = camTr.position;
            Vector3 camRight = camTr.right;
            Vector3 camUp = camTr.up;
            float maxCullDistSq = _manager.MaxCullDistance > 0f ? (_manager.MaxCullDistance * _manager.MaxCullDistance) : 0f;
            Vector4 ySortRange = _manager.GetYSortRangeVector();

            _manager.CollectSortedBucketDispatches(_scratchSortDispatches);

            CommandBuffer cmd = CommandBufferPool.Get("SpriteBridge_GpuPipeline");
            try
            {
                using (new ProfilingScope(cmd, s_CullProfilingSampler))
                {
                    int resetKernel = _manager.ResetKernel;
                    int cullKernel = _manager.CullAndAnimateKernel;
                    int activeSprites = _manager.ActiveSpriteCount;

                    cmd.SetComputeIntParam(cs, s_BucketCountId, bucketCount);
                    cmd.SetComputeBufferParam(cs, resetKernel, s_IndirectArgsId, _manager.IndirectArgsBuffer);
                    cmd.DispatchCompute(cs, resetKernel, (bucketCount + 63) / 64, 1, 1);

                    cmd.SetComputeIntParam(cs, s_ActiveSpriteCountId, activeSprites);
                    cmd.SetComputeIntParam(cs, s_MaxInstancesPerBucketId, _manager.MaxInstancesPerBucket);
                    cmd.SetComputeIntParam(cs, s_AnimClipCountId, _manager.AnimClipCount);
                    cmd.SetComputeFloatParam(cs, s_GlobalTimeId, Application.isPlaying ? Time.time : (float)Time.realtimeSinceStartup);
                    cmd.SetComputeIntParam(cs, s_EnableFrustumCullingId, _manager.EnableFrustumCulling ? 1 : 0);
                    cmd.SetComputeVectorArrayParam(cs, s_FrustumPlanesId, _compatFrustumPlanes);
                    cmd.SetComputeVectorParam(cs, s_CameraWorldPosId, new Vector4(camPos.x, camPos.y, camPos.z, maxCullDistSq));
                    cmd.SetComputeVectorParam(cs, s_CameraRightWSId, new Vector4(camRight.x, camRight.y, camRight.z, 0f));
                    cmd.SetComputeVectorParam(cs, s_CameraUpWSId, new Vector4(camUp.x, camUp.y, camUp.z, 0f));
                    cmd.SetComputeVectorParam(cs, s_YSortRangeId, ySortRange);

                    cmd.SetComputeBufferParam(cs, cullKernel, s_SourceSpritesId, _manager.SourceSpritesBuffer);
                    cmd.SetComputeBufferParam(cs, cullKernel, s_AnimClipsId, _manager.AnimClipsBuffer);
                    cmd.SetComputeBufferParam(cs, cullKernel, s_AnimFramesId, _manager.AnimFramesBuffer);
                    cmd.SetComputeBufferParam(cs, cullKernel, s_VisibleSpritesId, _manager.VisibleSpritesBuffer);
                    cmd.SetComputeBufferParam(cs, cullKernel, s_IndirectArgsId, _manager.IndirectArgsBuffer);

                    cmd.DispatchCompute(cs, cullKernel, (activeSprites + 63) / 64, 1, 1);
                }

                if (_scratchSortDispatches.Count > 0 && _manager.SortEntriesBuffer != null && _manager.SortEntriesBuffer.IsValid())
                {
                    using (new ProfilingScope(cmd, s_SortProfilingSampler))
                    {
                        int buildKernel = _manager.BuildSortEntriesKernel;
                        int localKernel = _manager.BitonicLocalKernel;
                        int globalKernel = _manager.BitonicGlobalKernel;
                        int mergeKernel = _manager.BitonicLocalMergeKernel;

                        for (int i = 0; i < _scratchSortDispatches.Count; i++)
                        {
                            SortedBucketDispatch sd = _scratchSortDispatches[i];
                            cmd.SetComputeIntParam(cs, s_SortBucketIndexId, sd.BucketIndex);
                            cmd.SetComputeIntParam(cs, s_SortBaseOffsetId, sd.BaseOffset);
                            cmd.SetComputeIntParam(cs, s_SortCountPow2Id, sd.SortCountPow2);

                            cmd.SetComputeBufferParam(cs, buildKernel, s_VisibleSpritesROId, _manager.VisibleSpritesBuffer);
                            cmd.SetComputeBufferParam(cs, buildKernel, s_IndirectArgsROId, _manager.IndirectArgsBuffer);
                            cmd.SetComputeBufferParam(cs, buildKernel, s_SortEntriesId, _manager.SortEntriesBuffer);
                            cmd.DispatchCompute(cs, buildKernel, (sd.SortCountPow2 + 63) / 64, 1, 1);

                            int groups256 = sd.SortCountPow2 / 256;
                            cmd.SetComputeBufferParam(cs, localKernel, s_SortEntriesId, _manager.SortEntriesBuffer);
                            cmd.DispatchCompute(cs, localKernel, groups256, 1, 1);

                            for (int k = 512; k <= sd.SortCountPow2; k <<= 1)
                            {
                                cmd.SetComputeIntParam(cs, s_SortKId, k);
                                for (int j = k >> 1; j >= 256; j >>= 1)
                                {
                                    cmd.SetComputeIntParam(cs, s_SortJId, j);
                                    cmd.SetComputeBufferParam(cs, globalKernel, s_SortEntriesId, _manager.SortEntriesBuffer);
                                    cmd.DispatchCompute(cs, globalKernel, groups256, 1, 1);
                                }
                                cmd.SetComputeBufferParam(cs, mergeKernel, s_SortEntriesId, _manager.SortEntriesBuffer);
                                cmd.DispatchCompute(cs, mergeKernel, groups256, 1, 1);
                            }
                        }
                    }
                }

                context.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                CommandBufferPool.Release(cmd);
            }
        }
#endif

#if UNITY_6000_0_OR_NEWER
        private sealed class CullPassData
        {
            public ComputeShader ComputeShader;
            public int ResetKernel;
            public int CullKernel;
            public BufferHandle SourceSpritesHandle;
            public BufferHandle AnimClipsHandle;
            public BufferHandle AnimFramesHandle;
            public BufferHandle VisibleSpritesHandle;
            public BufferHandle IndirectArgsHandle;
            public int ActiveSpriteCount;
            public int BucketCount;
            public int MaxInstancesPerBucket;
            public int AnimClipCount;
            public float GlobalTime;
            public int EnableFrustumCulling;
            public readonly Vector4[] FrustumPlanes = new Vector4[6];
            public Vector4 CameraWorldPos;
            public Vector4 CameraRightWS;
            public Vector4 CameraUpWS;
            public Vector4 YSortRange;
        }

        private sealed class SortPassData
        {
            public ComputeShader ComputeShader;
            public int BuildKernel;
            public int LocalKernel;
            public int GlobalKernel;
            public int LocalMergeKernel;
            public BufferHandle VisibleSpritesHandle;
            public BufferHandle IndirectArgsHandle;
            public BufferHandle SortEntriesHandle;
            public readonly List<SortedBucketDispatch> Dispatches = new List<SortedBucketDispatch>(16);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_manager == null || !_manager.IsSupportedAndReady || _manager.ActiveSpriteCount <= 0)
            {
                return;
            }

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData == null || cameraData.camera == null || cameraData.isPreviewCamera ||
                !_manager.ShouldRenderForCamera(cameraData.camera))
            {
                return;
            }

            int bucketCount = _manager.ActiveBucketCount;
            if (bucketCount <= 0 || _manager.SimulationCompute == null)
            {
                return;
            }

            BufferHandle visibleHandle = renderGraph.ImportBuffer(_manager.VisibleSpritesBuffer);
            BufferHandle argsHandle = renderGraph.ImportBuffer(_manager.IndirectArgsBuffer);

            using (IComputeRenderGraphBuilder builder = renderGraph.AddComputePass<CullPassData>(
                       "SpriteBridge_GpuCullAndAnimate",
                       out CullPassData passData,
                       s_CullProfilingSampler))
            {
                Camera cam = cameraData.camera;
                GeometryUtility.CalculateFrustumPlanes(cam, s_ScratchPlanes);
                for (int i = 0; i < 6; i++)
                {
                    Plane p = s_ScratchPlanes[i];
                    Vector3 n = p.normal;
                    passData.FrustumPlanes[i] = new Vector4(n.x, n.y, n.z, p.distance);
                }

                Transform camTr = cam.transform;
                Vector3 camPos = camTr.position;
                Vector3 camRight = camTr.right;
                Vector3 camUp = camTr.up;
                float maxCullDistSq = _manager.MaxCullDistance > 0f ? (_manager.MaxCullDistance * _manager.MaxCullDistance) : 0f;

                passData.ComputeShader = _manager.SimulationCompute;
                passData.ResetKernel = _manager.ResetKernel;
                passData.CullKernel = _manager.CullAndAnimateKernel;
                passData.SourceSpritesHandle = renderGraph.ImportBuffer(_manager.SourceSpritesBuffer);
                passData.AnimClipsHandle = renderGraph.ImportBuffer(_manager.AnimClipsBuffer);
                passData.AnimFramesHandle = renderGraph.ImportBuffer(_manager.AnimFramesBuffer);
                passData.VisibleSpritesHandle = visibleHandle;
                passData.IndirectArgsHandle = argsHandle;
                passData.ActiveSpriteCount = _manager.ActiveSpriteCount;
                passData.BucketCount = bucketCount;
                passData.MaxInstancesPerBucket = _manager.MaxInstancesPerBucket;
                passData.AnimClipCount = _manager.AnimClipCount;
                passData.GlobalTime = Application.isPlaying ? Time.time : (float)Time.realtimeSinceStartup;
                passData.EnableFrustumCulling = _manager.EnableFrustumCulling ? 1 : 0;
                passData.CameraWorldPos = new Vector4(camPos.x, camPos.y, camPos.z, maxCullDistSq);
                passData.CameraRightWS = new Vector4(camRight.x, camRight.y, camRight.z, 0f);
                passData.CameraUpWS = new Vector4(camUp.x, camUp.y, camUp.z, 0f);
                passData.YSortRange = _manager.GetYSortRangeVector();

                builder.UseBuffer(passData.SourceSpritesHandle, AccessFlags.Read);
                builder.UseBuffer(passData.AnimClipsHandle, AccessFlags.Read);
                builder.UseBuffer(passData.AnimFramesHandle, AccessFlags.Read);
                builder.UseBuffer(passData.VisibleSpritesHandle, AccessFlags.Write);
                builder.UseBuffer(passData.IndirectArgsHandle, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (CullPassData data, ComputeGraphContext ctx) =>
                {
                    ComputeCommandBuffer cmd = ctx.cmd;
                    ComputeShader cs = data.ComputeShader;

                    cmd.SetComputeIntParam(cs, s_BucketCountId, data.BucketCount);
                    cmd.SetComputeBufferParam(cs, data.ResetKernel, s_IndirectArgsId, data.IndirectArgsHandle);
                    cmd.DispatchCompute(cs, data.ResetKernel, (data.BucketCount + 63) / 64, 1, 1);

                    cmd.SetComputeIntParam(cs, s_ActiveSpriteCountId, data.ActiveSpriteCount);
                    cmd.SetComputeIntParam(cs, s_MaxInstancesPerBucketId, data.MaxInstancesPerBucket);
                    cmd.SetComputeIntParam(cs, s_AnimClipCountId, data.AnimClipCount);
                    cmd.SetComputeFloatParam(cs, s_GlobalTimeId, data.GlobalTime);
                    cmd.SetComputeIntParam(cs, s_EnableFrustumCullingId, data.EnableFrustumCulling);
                    cmd.SetComputeVectorArrayParam(cs, s_FrustumPlanesId, data.FrustumPlanes);
                    cmd.SetComputeVectorParam(cs, s_CameraWorldPosId, data.CameraWorldPos);
                    cmd.SetComputeVectorParam(cs, s_CameraRightWSId, data.CameraRightWS);
                    cmd.SetComputeVectorParam(cs, s_CameraUpWSId, data.CameraUpWS);
                    cmd.SetComputeVectorParam(cs, s_YSortRangeId, data.YSortRange);

                    cmd.SetComputeBufferParam(cs, data.CullKernel, s_SourceSpritesId, data.SourceSpritesHandle);
                    cmd.SetComputeBufferParam(cs, data.CullKernel, s_AnimClipsId, data.AnimClipsHandle);
                    cmd.SetComputeBufferParam(cs, data.CullKernel, s_AnimFramesId, data.AnimFramesHandle);
                    cmd.SetComputeBufferParam(cs, data.CullKernel, s_VisibleSpritesId, data.VisibleSpritesHandle);
                    cmd.SetComputeBufferParam(cs, data.CullKernel, s_IndirectArgsId, data.IndirectArgsHandle);

                    cmd.DispatchCompute(cs, data.CullKernel, (data.ActiveSpriteCount + 63) / 64, 1, 1);
                });
            }

            // Conditional Pass 2: GPU Bitonic Y-Sort (only added when at least one active bucket requests GpuYSort / GpuOrderAndYSort)
            _manager.CollectSortedBucketDispatches(_scratchSortDispatches);
            if (_scratchSortDispatches.Count > 0 && _manager.SortEntriesBuffer != null && _manager.SortEntriesBuffer.IsValid())
            {
                using (IComputeRenderGraphBuilder builder = renderGraph.AddComputePass<SortPassData>(
                           "SpriteBridge_GpuBitonicSort",
                           out SortPassData sortData,
                           s_SortProfilingSampler))
                {
                    sortData.ComputeShader = _manager.SimulationCompute;
                    sortData.BuildKernel = _manager.BuildSortEntriesKernel;
                    sortData.LocalKernel = _manager.BitonicLocalKernel;
                    sortData.GlobalKernel = _manager.BitonicGlobalKernel;
                    sortData.LocalMergeKernel = _manager.BitonicLocalMergeKernel;
                    sortData.VisibleSpritesHandle = visibleHandle;
                    sortData.IndirectArgsHandle = argsHandle;
                    sortData.SortEntriesHandle = renderGraph.ImportBuffer(_manager.SortEntriesBuffer);
                    sortData.Dispatches.Clear();
                    for (int i = 0; i < _scratchSortDispatches.Count; i++)
                    {
                        sortData.Dispatches.Add(_scratchSortDispatches[i]);
                    }

                    builder.UseBuffer(sortData.VisibleSpritesHandle, AccessFlags.Read);
                    builder.UseBuffer(sortData.IndirectArgsHandle, AccessFlags.Read);
                    builder.UseBuffer(sortData.SortEntriesHandle, AccessFlags.ReadWrite);
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc(static (SortPassData data, ComputeGraphContext ctx) =>
                    {
                        ComputeCommandBuffer cmd = ctx.cmd;
                        ComputeShader cs = data.ComputeShader;

                        for (int i = 0; i < data.Dispatches.Count; i++)
                        {
                            SortedBucketDispatch sd = data.Dispatches[i];
                            cmd.SetComputeIntParam(cs, s_SortBucketIndexId, sd.BucketIndex);
                            cmd.SetComputeIntParam(cs, s_SortBaseOffsetId, sd.BaseOffset);
                            cmd.SetComputeIntParam(cs, s_SortCountPow2Id, sd.SortCountPow2);

                            cmd.SetComputeBufferParam(cs, data.BuildKernel, s_VisibleSpritesROId, data.VisibleSpritesHandle);
                            cmd.SetComputeBufferParam(cs, data.BuildKernel, s_IndirectArgsROId, data.IndirectArgsHandle);
                            cmd.SetComputeBufferParam(cs, data.BuildKernel, s_SortEntriesId, data.SortEntriesHandle);
                            cmd.DispatchCompute(cs, data.BuildKernel, (sd.SortCountPow2 + 63) / 64, 1, 1);

                            int groups256 = sd.SortCountPow2 / 256;
                            cmd.SetComputeBufferParam(cs, data.LocalKernel, s_SortEntriesId, data.SortEntriesHandle);
                            cmd.DispatchCompute(cs, data.LocalKernel, groups256, 1, 1);

                            for (int k = 512; k <= sd.SortCountPow2; k <<= 1)
                            {
                                cmd.SetComputeIntParam(cs, s_SortKId, k);
                                for (int j = k >> 1; j >= 256; j >>= 1)
                                {
                                    cmd.SetComputeIntParam(cs, s_SortJId, j);
                                    cmd.SetComputeBufferParam(cs, data.GlobalKernel, s_SortEntriesId, data.SortEntriesHandle);
                                    cmd.DispatchCompute(cs, data.GlobalKernel, groups256, 1, 1);
                                }
                                cmd.SetComputeBufferParam(cs, data.LocalMergeKernel, s_SortEntriesId, data.SortEntriesHandle);
                                cmd.DispatchCompute(cs, data.LocalMergeKernel, groups256, 1, 1);
                            }
                        }
                    });
                }
            }
        }
#endif
    }
}
