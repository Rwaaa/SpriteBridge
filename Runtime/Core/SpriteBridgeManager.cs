using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SpriteBridge
{
    /// <summary>
    /// Global singleton orchestrator for SpriteBridge.
    /// Manages:
    /// - Tier 1 Drop-In SpriteBridgeRenderer hijacking (Static + Burst/CoreModule TransformAccessArray)
    /// - Tier 2 Zero-GameObject High-Density Batch API (O(1) Generational Handles + NativeArray direct slice)
    /// - Multi-Library GPU 2D Animation Tables (0.000 ms CPU Animator replacement)
    /// - Optional Per-Bucket GPU Bitonic Y-Sort (GpuYSort / GpuOrderAndYSort, <= 2 UAVs/kernel)
    /// - Zero-config URP 14-17 (3D & 2D) compute pass injection & 3-tier Play Mode Scene View debugging.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(900)]
    [AddComponentMenu("SpriteBridge/Sprite Bridge Manager")]
    public sealed class SpriteBridgeManager : MonoBehaviour
    {
        public const int MaxBuckets = 256;
        private const int InitialInstanceCapacity = 1024;
        private const int MinInstancesPerBucket = 256;

        private static readonly int s_VisibleSpritesPropId = Shader.PropertyToID("_SpriteBridgeVisibleSprites");
        private static readonly int s_SortEntriesPropId = Shader.PropertyToID("_SpriteBridgeSortEntries");
        private static readonly int s_BucketBaseOffsetPropId = Shader.PropertyToID("_SpriteBridgeBucketBaseOffset");
        private static readonly int s_MainTexPropId = Shader.PropertyToID("_MainTex");
        private static readonly int s_ColorPropId = Shader.PropertyToID("_Color");
        private static readonly int s_MaskTexPropId = Shader.PropertyToID("_MaskTex");
        private static readonly int s_NormalMapPropId = Shader.PropertyToID("_NormalMap");
        private static readonly int s_ZWritePropId = Shader.PropertyToID("_ZWrite");
        private static readonly int s_CutoffPropId = Shader.PropertyToID("_Cutoff");
        private static readonly int s_SrcBlendPropId = Shader.PropertyToID("_SrcBlend");
        private static readonly int s_DstBlendPropId = Shader.PropertyToID("_DstBlend");

        private static readonly Bounds s_HugeBounds = new Bounds(Vector3.zero, Vector3.one * 200000f);

        private static SpriteBridgeManager s_Instance;
        private static bool s_IsApplicationQuitting;

#if UNITY_EDITOR
        private static bool s_DebugModeLoadedFromResources;
        private static SpriteSceneViewDebugMode s_PlayModeSceneViewDebugMode = SpriteSceneViewDebugMode.Off;
#endif

        [Header("Culling & LOD")]
        [SerializeField] private bool _enableFrustumCulling = true;
        [SerializeField] private float _maxCullDistance = 0f;

        [Header("GPU Y-Sort World Range")]
        [SerializeField] private float _ySortMinY = -4096f;
        [SerializeField] private float _ySortMaxY = 4096f;

        private sealed class SpriteMaterialBucket
        {
            public int BucketIndex;
            public SpriteBucketKey Key;
            public Material SourceMaterial;
            public Material RuntimeMaterial;
            public Texture2D AtlasTexture;
            public int SortingLayerValue;
            public int SortingOrderBucket;
            public SpriteSortMode SortMode;
            public int Layer;
            public int ActiveCount;
            public int BaseRenderQueue;
            public bool CastAndReceive3DShadows;
            public bool UsesGpuSort => SortMode == SpriteSortMode.GpuYSort || SortMode == SpriteSortMode.GpuOrderAndYSort;
        }

        private struct AnimLibraryRecord
        {
            public int BaseClipIndex;
            public int BakedRevision;
        }

        // Resources & Compute
        private SpriteBridgeResources _resources;
        private ComputeShader _simulationCompute;
        private int _resetKernel = -1;
        private int _cullAndAnimateKernel = -1;
        private int _buildSortEntriesKernel = -1;
        private int _bitonicLocalKernel = -1;
        private int _bitonicGlobalKernel = -1;
        private int _bitonicLocalMergeKernel = -1;
        private SpriteBridgeComputePass _computePass;
        private Mesh _unitQuadMesh;

        // GPU Buffers
        private GraphicsBuffer _sourceSpritesBuffer;
        private GraphicsBuffer _visibleSpritesBuffer;
        private GraphicsBuffer _sortEntriesBuffer;
        private GraphicsBuffer _indirectArgsBuffer;
        private GraphicsBuffer _animClipsBuffer;
        private GraphicsBuffer _animFramesBuffer;

        // CPU Dense Slot Arrays (Zero-GC, Swap-and-Pop, shared by Tier 1 & Tier 2)
        private NativeArray<GpuSpriteInstance> _cpuInstances;
        private SpriteBridgeRenderer[] _slotOwners;
        private int[] _slotDynamicIndices;
        private int[] _slotBucketIndices;
        private int[] _denseSlotToHandleId;
        private int _instanceCapacity;
        private int _activeSpriteCount;
        private int _maxInstancesPerBucket = MinInstancesPerBucket;
        private int _visibleBufferBucketCapacity;
        private bool _hasAnySortedBucket;

        // Tier 2 Generational Handle Table (O(1) Spawn / Update / Despawn)
        private int[] _handleToDenseSlot;
        private uint[] _handleGenerations;
        private int _handleCapacity;
        private int _handleCount;
        private readonly Stack<int> _freeHandleIds = new Stack<int>(256);

        // Multi-Library GPU Animation Table
        private readonly Dictionary<SpriteBridgeAnimationLibrary, AnimLibraryRecord> _animLibraryRecords =
            new Dictionary<SpriteBridgeAnimationLibrary, AnimLibraryRecord>(8);
        private readonly List<SpriteBridgeAnimationLibrary> _animLibraryOrder = new List<SpriteBridgeAnimationLibrary>(8);
        private readonly List<GpuSpriteAnimClip> _globalAnimClips = new List<GpuSpriteAnimClip>(64);
        private readonly List<GpuSpriteAnimFrame> _globalAnimFrames = new List<GpuSpriteAnimFrame>(256);
        private int _animClipCount;
        private bool _animBuffersDirty;

        // Transform Sync (Burst when available, CoreModule fallback otherwise)
        private SpriteBridgeTransformSync _transformSync;
        private JobHandle _pendingSyncJob;
        private bool _hasPendingSyncJob;
        private bool _sourceBufferDirty;

        // Buckets
        private readonly List<SpriteMaterialBucket> _buckets = new List<SpriteMaterialBucket>(16);
        private readonly List<SpriteMaterialBucket> _sortedDrawOrder = new List<SpriteMaterialBucket>(16);
        private readonly Dictionary<SpriteBucketKey, int> _bucketLookup = new Dictionary<SpriteBucketKey, int>(16);
        private bool _bucketOrderDirty;

        // Lifecycle flags
        private bool _isInitialized;
        private bool _isHardwareSupported;
        private uint[] _readbackIndirectArgsScratch;
        private Camera[] _cameraScratch = new Camera[8];

#if UNITY_EDITOR
        private bool _hidNativeRenderersForNonSceneCamera;
#endif

        // ---------------------------------------------------------------------
        // Static Lifecycle & Debug Mode Properties
        // ---------------------------------------------------------------------

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticsOnDomainReload()
        {
            s_Instance = null;
            s_IsApplicationQuitting = false;
#if UNITY_EDITOR
            s_DebugModeLoadedFromResources = false;
#endif
        }

        public static SpriteBridgeManager CurrentInstance => s_Instance;

        public static SpriteSceneViewDebugMode PlayModeSceneViewDebugMode
        {
            get
            {
#if UNITY_EDITOR
                EnsureEditorDebugModeLoaded();
                return s_PlayModeSceneViewDebugMode;
#else
                return SpriteSceneViewDebugMode.Off;
#endif
            }
            set
            {
#if UNITY_EDITOR
                EnsureEditorDebugModeLoaded();
                if (s_PlayModeSceneViewDebugMode == value)
                {
                    return;
                }

                s_PlayModeSceneViewDebugMode = value;
                SpriteBridgeResources res = SpriteBridgeResources.Load();
                if (res != null && res.PlayModeSceneViewDebugMode != value)
                {
                    res.PlayModeSceneViewDebugMode = value;
                    UnityEditor.EditorUtility.SetDirty(res);
                }

                if (s_Instance != null)
                {
                    s_Instance.ApplyDebugModeToAllRegisteredRenderers();
                }

                UnityEditor.SceneView.RepaintAll();
#endif
            }
        }

        public static bool IsNativeSceneViewDebugActive
        {
            get
            {
#if UNITY_EDITOR
                return Application.isPlaying && PlayModeSceneViewDebugMode == SpriteSceneViewDebugMode.NativeSpriteRenderer;
#else
                return false;
#endif
            }
        }

        public static bool IsHardwareSupported =>
            SystemInfo.supportsComputeShaders &&
            SystemInfo.supportsInstancing &&
            SystemInfo.maxComputeBufferInputsVertex > 0;

        public bool IsSupportedAndReady => _isInitialized && _isHardwareSupported && _simulationCompute != null;
        public int ActiveSpriteCount => _activeSpriteCount;
        public int DynamicTransformCount => _transformSync != null ? _transformSync.DynamicCount : 0;
        public bool IsUsingBurstSync => _transformSync != null && _transformSync.IsUsingBurst;
        public int ActiveBucketCount => _buckets.Count;
        public int MaxInstancesPerBucket => _maxInstancesPerBucket;
        public int AnimClipCount => _animClipCount;
        public int AnimFrameCount => _globalAnimFrames.Count;
        public bool HasSortedBuckets => _hasAnySortedBucket;
        public bool EnableFrustumCulling { get => _enableFrustumCulling; set => _enableFrustumCulling = value; }
        public float MaxCullDistance { get => _maxCullDistance; set => _maxCullDistance = Mathf.Max(0f, value); }
        public float YSortMinY { get => _ySortMinY; set => _ySortMinY = value; }
        public float YSortMaxY { get => _ySortMaxY; set => _ySortMaxY = value; }

        internal ComputeShader SimulationCompute => _simulationCompute;
        internal int ResetKernel => _resetKernel;
        internal int CullAndAnimateKernel => _cullAndAnimateKernel;
        internal int BuildSortEntriesKernel => _buildSortEntriesKernel;
        internal int BitonicLocalKernel => _bitonicLocalKernel;
        internal int BitonicGlobalKernel => _bitonicGlobalKernel;
        internal int BitonicLocalMergeKernel => _bitonicLocalMergeKernel;
        internal GraphicsBuffer SourceSpritesBuffer => _sourceSpritesBuffer;
        internal GraphicsBuffer VisibleSpritesBuffer => _visibleSpritesBuffer;
        internal GraphicsBuffer SortEntriesBuffer => _sortEntriesBuffer;
        internal GraphicsBuffer IndirectArgsBuffer => _indirectArgsBuffer;
        internal GraphicsBuffer AnimClipsBuffer => _animClipsBuffer;
        internal GraphicsBuffer AnimFramesBuffer => _animFramesBuffer;

        internal Vector4 GetYSortRangeVector()
        {
            float minY = Mathf.Min(_ySortMinY, _ySortMaxY - 0.01f);
            float maxY = Mathf.Max(_ySortMaxY, minY + 0.01f);
            return new Vector4(minY, maxY, 1.0f / (maxY - minY), 0f);
        }

        internal void CollectSortedBucketDispatches(List<SpriteBridgeComputePass.SortedBucketDispatch> output)
        {
            output.Clear();
            if (!_hasAnySortedBucket)
            {
                return;
            }

            for (int i = 0; i < _buckets.Count; i++)
            {
                SpriteMaterialBucket b = _buckets[i];
                if (!b.UsesGpuSort || b.ActiveCount <= 0)
                {
                    continue;
                }

                int pow2 = Mathf.Max(256, Mathf.NextPowerOfTwo(b.ActiveCount));
                pow2 = Mathf.Min(pow2, _maxInstancesPerBucket);
                output.Add(new SpriteBridgeComputePass.SortedBucketDispatch
                {
                    BucketIndex = b.BucketIndex,
                    BaseOffset = b.BucketIndex * _maxInstancesPerBucket,
                    SortCountPow2 = pow2
                });
            }
        }

#if UNITY_EDITOR
        private static void EnsureEditorDebugModeLoaded()
        {
            if (s_DebugModeLoadedFromResources)
            {
                return;
            }
            s_DebugModeLoadedFromResources = true;
            SpriteBridgeResources res = SpriteBridgeResources.Load();
            if (res != null)
            {
                s_PlayModeSceneViewDebugMode = res.PlayModeSceneViewDebugMode;
            }
        }
#endif

        public static SpriteBridgeManager GetOrCreate()
        {
            if (s_IsApplicationQuitting)
            {
                return null;
            }

            if (s_Instance != null)
            {
                return s_Instance;
            }

#if UNITY_2023_1_OR_NEWER
            SpriteBridgeManager existing = FindFirstObjectByType<SpriteBridgeManager>();
#else
            SpriteBridgeManager existing = FindObjectOfType<SpriteBridgeManager>();
#endif
            if (existing != null)
            {
                s_Instance = existing;
                existing.EnsureInitialized();
                return existing;
            }

            GameObject go = new GameObject("[SpriteBridgeManager]");
            go.hideFlags = Application.isPlaying ? HideFlags.None : HideFlags.DontSave;
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(go);
            }

            SpriteBridgeManager created = go.AddComponent<SpriteBridgeManager>();
            s_Instance = created;
            created.EnsureInitialized();
            return created;
        }

        // ---------------------------------------------------------------------
        // Unity Lifecycle
        // ---------------------------------------------------------------------

        private void Awake()
        {
            if (s_Instance != null && s_Instance != this)
            {
                if (Application.isPlaying)
                {
                    Destroy(gameObject);
                }
                else
                {
                    DestroyImmediate(gameObject);
                }
                return;
            }

            s_Instance = this;
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(gameObject);
            }
            EnsureInitialized();
        }

        private void OnEnable()
        {
            if (s_Instance == null)
            {
                s_Instance = this;
            }

            EnsureInitialized();

            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;

            Camera.onPreCull -= OnBuiltInCameraPreCull;
            Camera.onPreCull += OnBuiltInCameraPreCull;
            Camera.onPostRender -= OnBuiltInCameraPostRender;
            Camera.onPostRender += OnBuiltInCameraPostRender;

            RebindActiveSceneRenderers();
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            Camera.onPreCull -= OnBuiltInCameraPreCull;
            Camera.onPostRender -= OnBuiltInCameraPostRender;

#if UNITY_EDITOR
            if (_hidNativeRenderersForNonSceneCamera)
            {
                SetAllNativeRenderersForceRenderingOffForEditor(false);
                _hidNativeRenderersForNonSceneCamera = false;
            }
#endif
            ReleaseGpuResources();

            if (s_Instance == this)
            {
                s_Instance = null;
            }
        }

        private void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            Camera.onPreCull -= OnBuiltInCameraPreCull;
            Camera.onPostRender -= OnBuiltInCameraPostRender;

            ReleaseGpuResources();

            if (s_Instance == this)
            {
                s_Instance = null;
            }
        }

        private void OnApplicationQuit()
        {
            s_IsApplicationQuitting = true;
        }

        private void LateUpdate()
        {
            if (!_isInitialized || !_isHardwareSupported || _activeSpriteCount <= 0)
            {
                return;
            }

            SyncDynamicTransformsAndUpload();

            if (Application.isPlaying)
            {
                IssueIndirectDrawsForPlayModeGameCameras();
            }
        }

        // ---------------------------------------------------------------------
        // Initialization & Buffer Management
        // ---------------------------------------------------------------------

        private void EnsureInitialized()
        {
            if (_isInitialized)
            {
                return;
            }

            _isHardwareSupported = IsHardwareSupported;
            if (!_isHardwareSupported)
            {
                return;
            }

            _resources = SpriteBridgeResources.Load();
            _simulationCompute = _resources != null ? _resources.SimulationCompute : null;

#if UNITY_EDITOR
            if (_simulationCompute == null)
            {
                _simulationCompute = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/SpriteBridge/Shaders/SpriteBridgeSimulation.compute");
            }
#endif

            if (_simulationCompute == null)
            {
                return;
            }

            _resetKernel = _simulationCompute.FindKernel("CS_ResetIndirectArgs");
            _cullAndAnimateKernel = _simulationCompute.FindKernel("CS_CullAndAnimateSprites");
            _buildSortEntriesKernel = _simulationCompute.FindKernel("CS_BuildSortEntries");
            _bitonicLocalKernel = _simulationCompute.FindKernel("CS_BitonicSortLocal");
            _bitonicGlobalKernel = _simulationCompute.FindKernel("CS_BitonicSortGlobalStep");
            _bitonicLocalMergeKernel = _simulationCompute.FindKernel("CS_BitonicSortLocalMerge");

            if (_resetKernel < 0 || _cullAndAnimateKernel < 0 ||
                _buildSortEntriesKernel < 0 || _bitonicLocalKernel < 0 ||
                _bitonicGlobalKernel < 0 || _bitonicLocalMergeKernel < 0)
            {
                return;
            }

            CreateUnitQuadMesh();
            AllocateInitialBuffers(InitialInstanceCapacity);

            _computePass = new SpriteBridgeComputePass(this);
            _isInitialized = true;
        }

        private void CreateUnitQuadMesh()
        {
            if (_unitQuadMesh != null)
            {
                return;
            }

            _unitQuadMesh = new Mesh
            {
                name = "SpriteBridge_UnitQuad",
                hideFlags = HideFlags.HideAndDontSave
            };

            _unitQuadMesh.vertices = new Vector3[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 1f, 0f),
                new Vector3(0f, 1f, 0f)
            };
            _unitQuadMesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f)
            };
            _unitQuadMesh.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
            _unitQuadMesh.bounds = s_HugeBounds;
            _unitQuadMesh.UploadMeshData(true);
        }

        private void AllocateInitialBuffers(int capacity)
        {
            _instanceCapacity = Mathf.Max(InitialInstanceCapacity, capacity);
            _cpuInstances = new NativeArray<GpuSpriteInstance>(_instanceCapacity, Allocator.Persistent);
            _slotOwners = new SpriteBridgeRenderer[_instanceCapacity];
            _slotDynamicIndices = new int[_instanceCapacity];
            _slotBucketIndices = new int[_instanceCapacity];
            _denseSlotToHandleId = new int[_instanceCapacity];
            for (int i = 0; i < _instanceCapacity; i++)
            {
                _slotDynamicIndices[i] = -1;
                _slotBucketIndices[i] = -1;
                _denseSlotToHandleId[i] = -1;
            }

            _handleCapacity = 256;
            _handleToDenseSlot = new int[_handleCapacity];
            _handleGenerations = new uint[_handleCapacity];
            for (int i = 0; i < _handleCapacity; i++)
            {
                _handleToDenseSlot[i] = -1;
                _handleGenerations[i] = 1u;
            }
            _handleCount = 0;
            _freeHandleIds.Clear();

            _transformSync = new SpriteBridgeTransformSync(256);

            _sourceSpritesBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                _instanceCapacity,
                GpuSpriteInstance.StrideBytes);

            _indirectArgsBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                MaxBuckets * 5,
                sizeof(uint));

            _readbackIndirectArgsScratch = new uint[MaxBuckets * 5];

            _animClipsBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                1,
                GpuSpriteAnimClip.StrideBytes);
            _animFramesBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                1,
                GpuSpriteAnimFrame.StrideBytes);
            _animClipCount = 0;
            _animBuffersDirty = false;

            EnsureVisibleBufferCapacity(1, MinInstancesPerBucket);
        }

        private void GrowInstanceCapacity(int requiredCapacity)
        {
            CompletePendingTransformSyncJob();

            int newCapacity = _instanceCapacity;
            while (newCapacity < requiredCapacity)
            {
                newCapacity *= 2;
            }

            NativeArray<GpuSpriteInstance> newCpu = new NativeArray<GpuSpriteInstance>(newCapacity, Allocator.Persistent);
            if (_activeSpriteCount > 0 && _cpuInstances.IsCreated)
            {
                NativeArray<GpuSpriteInstance>.Copy(_cpuInstances, newCpu, _activeSpriteCount);
            }
            if (_cpuInstances.IsCreated)
            {
                _cpuInstances.Dispose();
            }
            _cpuInstances = newCpu;

            SpriteBridgeRenderer[] newOwners = new SpriteBridgeRenderer[newCapacity];
            if (_activeSpriteCount > 0)
            {
                Array.Copy(_slotOwners, newOwners, _activeSpriteCount);
            }
            _slotOwners = newOwners;

            int[] newDynIndices = new int[newCapacity];
            int[] newBucketIndices = new int[newCapacity];
            int[] newDenseToHandle = new int[newCapacity];
            for (int i = 0; i < newCapacity; i++)
            {
                newDynIndices[i] = i < _activeSpriteCount ? _slotDynamicIndices[i] : -1;
                newBucketIndices[i] = i < _activeSpriteCount ? _slotBucketIndices[i] : -1;
                newDenseToHandle[i] = i < _activeSpriteCount ? _denseSlotToHandleId[i] : -1;
            }
            _slotDynamicIndices = newDynIndices;
            _slotBucketIndices = newBucketIndices;
            _denseSlotToHandleId = newDenseToHandle;

            _sourceSpritesBuffer?.Release();
            _sourceSpritesBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                newCapacity,
                GpuSpriteInstance.StrideBytes);

            _instanceCapacity = newCapacity;
            _sourceBufferDirty = true;
        }

        private void EnsureVisibleBufferCapacity(int requiredBuckets, int requiredPerBucket)
        {
            int targetBuckets = Mathf.Max(1, Mathf.NextPowerOfTwo(Mathf.Max(requiredBuckets, _visibleBufferBucketCapacity)));
            targetBuckets = Mathf.Min(targetBuckets, MaxBuckets);

            int targetPerBucket = Mathf.Max(MinInstancesPerBucket, Mathf.NextPowerOfTwo(requiredPerBucket));
            bool visibleNeedsResize =
                _visibleSpritesBuffer == null ||
                !_visibleSpritesBuffer.IsValid() ||
                targetBuckets > _visibleBufferBucketCapacity ||
                targetPerBucket > _maxInstancesPerBucket;

            bool sortNeedsAlloc =
                _hasAnySortedBucket &&
                (_sortEntriesBuffer == null || !_sortEntriesBuffer.IsValid() || visibleNeedsResize);

            if (!visibleNeedsResize && !sortNeedsAlloc)
            {
                return;
            }

            _visibleBufferBucketCapacity = Mathf.Max(targetBuckets, _visibleBufferBucketCapacity);
            _maxInstancesPerBucket = Mathf.Max(targetPerBucket, _maxInstancesPerBucket);
            int totalVisibleSlots = _visibleBufferBucketCapacity * _maxInstancesPerBucket;

            if (visibleNeedsResize)
            {
                _visibleSpritesBuffer?.Release();
                _visibleSpritesBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    totalVisibleSlots,
                    GpuVisibleSprite.StrideBytes);
            }

            if (_hasAnySortedBucket)
            {
                _sortEntriesBuffer?.Release();
                _sortEntriesBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    totalVisibleSlots,
                    GpuSpriteSortEntry.StrideBytes);
            }

            BindBuffersToAllBucketMaterials();
        }

        private void BindBuffersToAllBucketMaterials()
        {
            if (_visibleSpritesBuffer == null || !_visibleSpritesBuffer.IsValid())
            {
                return;
            }

            for (int i = 0; i < _buckets.Count; i++)
            {
                SpriteMaterialBucket b = _buckets[i];
                if (b.RuntimeMaterial == null)
                {
                    continue;
                }

                b.RuntimeMaterial.SetBuffer(s_VisibleSpritesPropId, _visibleSpritesBuffer);
                b.RuntimeMaterial.SetInt(s_BucketBaseOffsetPropId, b.BucketIndex * _maxInstancesPerBucket);

                if (b.UsesGpuSort && _sortEntriesBuffer != null && _sortEntriesBuffer.IsValid())
                {
                    b.RuntimeMaterial.SetBuffer(s_SortEntriesPropId, _sortEntriesBuffer);
                    b.RuntimeMaterial.EnableKeyword("_SPRITEBRIDGE_SORTED");
                }
                else
                {
                    b.RuntimeMaterial.DisableKeyword("_SPRITEBRIDGE_SORTED");
                }
            }
        }

        private void ReleaseGpuResources()
        {
            CompletePendingTransformSyncJob();

            // Restore all registered SpriteBridgeRenderers back to native rendering before releasing buffers
            if (_slotOwners != null)
            {
                for (int i = 0; i < _activeSpriteCount; i++)
                {
                    SpriteBridgeRenderer owner = _slotOwners[i];
                    if (owner != null)
                    {
                        owner.OnManagerReleased();
                    }
                }
            }

            _transformSync?.Dispose();
            _transformSync = null;

            if (_cpuInstances.IsCreated)
            {
                _cpuInstances.Dispose();
            }

            _sourceSpritesBuffer?.Release();
            _sourceSpritesBuffer = null;

            _visibleSpritesBuffer?.Release();
            _visibleSpritesBuffer = null;

            _sortEntriesBuffer?.Release();
            _sortEntriesBuffer = null;

            _indirectArgsBuffer?.Release();
            _indirectArgsBuffer = null;

            _animClipsBuffer?.Release();
            _animClipsBuffer = null;

            _animFramesBuffer?.Release();
            _animFramesBuffer = null;

            for (int i = 0; i < _buckets.Count; i++)
            {
                Material runtimeMat = _buckets[i].RuntimeMaterial;
                if (runtimeMat != null)
                {
                    if (Application.isPlaying)
                    {
                        Destroy(runtimeMat);
                    }
                    else
                    {
                        DestroyImmediate(runtimeMat);
                    }
                }
            }
            _buckets.Clear();
            _sortedDrawOrder.Clear();
            _bucketLookup.Clear();

            _animLibraryRecords.Clear();
            _animLibraryOrder.Clear();
            _globalAnimClips.Clear();
            _globalAnimFrames.Clear();
            _animClipCount = 0;
            _animBuffersDirty = false;

            if (_unitQuadMesh != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(_unitQuadMesh);
                }
                else
                {
                    DestroyImmediate(_unitQuadMesh);
                }
                _unitQuadMesh = null;
            }

            _slotOwners = null;
            _slotDynamicIndices = null;
            _slotBucketIndices = null;
            _denseSlotToHandleId = null;
            _handleToDenseSlot = null;
            _handleGenerations = null;
            _freeHandleIds.Clear();
            _handleCount = 0;
            _handleCapacity = 0;
            _activeSpriteCount = 0;
            _instanceCapacity = 0;
            _visibleBufferBucketCapacity = 0;
            _maxInstancesPerBucket = MinInstancesPerBucket;
            _hasAnySortedBucket = false;
            _isInitialized = false;
        }

        // ---------------------------------------------------------------------
        // Multi-Library GPU 2D Animation Table
        // ---------------------------------------------------------------------

        /// <summary>
        /// Registers a SpriteBridgeAnimationLibrary into the global GPU animation buffer and resolves
        /// its localClipIndex to a global GPU clip index (0xFFFF if invalid).
        /// Supports multiple AnimationLibraries simultaneously and auto-rebuilds if re-baked.
        /// </summary>
        public uint ResolveGlobalClipIndex(SpriteBridgeAnimationLibrary library, int localClipIndex)
        {
            if (library == null || localClipIndex < 0 || localClipIndex >= library.ClipCount)
            {
                return GpuSpriteInstance.StaticAnimClipIndex;
            }

            EnsureInitialized();
            if (!_isInitialized)
            {
                return GpuSpriteInstance.StaticAnimClipIndex;
            }

            if (_animLibraryRecords.TryGetValue(library, out AnimLibraryRecord rec))
            {
                if (rec.BakedRevision != library.Revision)
                {
                    RebuildGlobalAnimationTables();
                    if (!_animLibraryRecords.TryGetValue(library, out rec))
                    {
                        return GpuSpriteInstance.StaticAnimClipIndex;
                    }
                }
                return (uint)(rec.BaseClipIndex + localClipIndex);
            }

            int baseClip = AppendAnimationLibrary(library);
            return (uint)(baseClip + localClipIndex);
        }

        private int AppendAnimationLibrary(SpriteBridgeAnimationLibrary library)
        {
            int baseClip = _globalAnimClips.Count;
            int baseFrame = _globalAnimFrames.Count;

            IReadOnlyList<SpriteBridgeAnimationLibrary.BakedClipMetadata> clips = library.Clips;
            IReadOnlyList<GpuSpriteAnimFrame> frames = library.Frames;

            for (int f = 0; f < frames.Count; f++)
            {
                _globalAnimFrames.Add(frames[f]);
            }

            for (int c = 0; c < clips.Count; c++)
            {
                SpriteBridgeAnimationLibrary.BakedClipMetadata meta = clips[c];
                _globalAnimClips.Add(new GpuSpriteAnimClip
                {
                    FrameStartIndex = (uint)(baseFrame + meta.FrameStartIndex),
                    FrameCount = (uint)Mathf.Max(1, meta.FrameCount),
                    Duration = Mathf.Max(0.001f, meta.Duration),
                    Flags = meta.Loop ? 1u : 0u
                });
            }

            _animLibraryOrder.Add(library);
            _animLibraryRecords[library] = new AnimLibraryRecord
            {
                BaseClipIndex = baseClip,
                BakedRevision = library.Revision
            };

            _animClipCount = _globalAnimClips.Count;
            _animBuffersDirty = true;
            return baseClip;
        }

        private void RebuildGlobalAnimationTables()
        {
            _globalAnimClips.Clear();
            _globalAnimFrames.Clear();
            _animLibraryRecords.Clear();

            for (int i = _animLibraryOrder.Count - 1; i >= 0; i--)
            {
                if (_animLibraryOrder[i] == null)
                {
                    _animLibraryOrder.RemoveAt(i);
                }
            }

            List<SpriteBridgeAnimationLibrary> snapshot = new List<SpriteBridgeAnimationLibrary>(_animLibraryOrder);
            _animLibraryOrder.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                AppendAnimationLibrary(snapshot[i]);
            }

            _animBuffersDirty = true;
        }

        private void UploadAnimationBuffersIfDirty()
        {
            if (!_animBuffersDirty || !_isInitialized)
            {
                return;
            }
            _animBuffersDirty = false;

            int clipCount = Mathf.Max(1, _globalAnimClips.Count);
            if (_animClipsBuffer == null || !_animClipsBuffer.IsValid() || _animClipsBuffer.count < clipCount)
            {
                _animClipsBuffer?.Release();
                _animClipsBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    Mathf.NextPowerOfTwo(clipCount),
                    GpuSpriteAnimClip.StrideBytes);
            }
            if (_globalAnimClips.Count > 0)
            {
                _animClipsBuffer.SetData(_globalAnimClips);
            }

            int frameCount = Mathf.Max(1, _globalAnimFrames.Count);
            if (_animFramesBuffer == null || !_animFramesBuffer.IsValid() || _animFramesBuffer.count < frameCount)
            {
                _animFramesBuffer?.Release();
                _animFramesBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    Mathf.NextPowerOfTwo(frameCount),
                    GpuSpriteAnimFrame.StrideBytes);
            }
            if (_globalAnimFrames.Count > 0)
            {
                _animFramesBuffer.SetData(_globalAnimFrames);
            }
        }

        // ---------------------------------------------------------------------
        // Tier 1: Drop-In SpriteBridgeRenderer Registration & Update
        // ---------------------------------------------------------------------

        public bool RegisterRenderer(SpriteBridgeRenderer owner, out int outSlotIndex, out int outBucketIndex)
        {
            outSlotIndex = -1;
            outBucketIndex = -1;

            EnsureInitialized();
            if (!_isInitialized || !_isHardwareSupported || owner == null)
            {
                return false;
            }

            if (!owner.TryBuildInstanceData(this, out Texture2D tex, out Material srcMat, out SpriteBucketKey bucketKey, out GpuSpriteInstance inst, out float flipX, out float flipY))
            {
                return false;
            }

            CompletePendingTransformSyncJob();

            int bucketIdx = GetOrCreateBucket(bucketKey, srcMat, tex);
            if (bucketIdx < 0)
            {
                return false;
            }

            if (_activeSpriteCount >= _instanceCapacity)
            {
                GrowInstanceCapacity(_instanceCapacity * 2);
            }

            int slot = _activeSpriteCount++;
            inst.PackedMeta = (inst.PackedMeta & 0x00FFFFFFu) | ((uint)(bucketIdx & 0xFF) << 24);

            _cpuInstances[slot] = inst;
            _slotOwners[slot] = owner;
            _slotDynamicIndices[slot] = -1;
            _slotBucketIndices[slot] = bucketIdx;
            _denseSlotToHandleId[slot] = -1;

            if (owner.MotionMode == SpriteMotionMode.DynamicTransform)
            {
                int dynIdx = _transformSync.Add(owner.transform, slot, flipX, flipY, inst.SortKey);
                _slotDynamicIndices[slot] = dynIdx;
            }

            SpriteMaterialBucket bucket = _buckets[bucketIdx];
            bucket.ActiveCount++;
            EnsureVisibleBufferCapacity(_buckets.Count, bucket.ActiveCount);

            _sourceBufferDirty = true;
            outSlotIndex = slot;
            outBucketIndex = bucketIdx;
            return true;
        }

        public void UpdateRenderer(SpriteBridgeRenderer owner)
        {
            if (!_isInitialized || owner == null || owner.RegisteredSlotIndex < 0 || owner.RegisteredSlotIndex >= _activeSpriteCount)
            {
                return;
            }

            if (!owner.TryBuildInstanceData(this, out Texture2D tex, out Material srcMat, out SpriteBucketKey bucketKey, out GpuSpriteInstance inst, out float flipX, out float flipY))
            {
                UnregisterRenderer(owner);
                return;
            }

            CompletePendingTransformSyncJob();

            int slot = owner.RegisteredSlotIndex;
            int oldBucketIdx = owner.RegisteredBucketIndex;
            int newBucketIdx = GetOrCreateBucket(bucketKey, srcMat, tex);
            if (newBucketIdx < 0)
            {
                return;
            }

            if (oldBucketIdx != newBucketIdx)
            {
                if (oldBucketIdx >= 0 && oldBucketIdx < _buckets.Count)
                {
                    _buckets[oldBucketIdx].ActiveCount = Mathf.Max(0, _buckets[oldBucketIdx].ActiveCount - 1);
                }

                SpriteMaterialBucket newBucket = _buckets[newBucketIdx];
                newBucket.ActiveCount++;
                EnsureVisibleBufferCapacity(_buckets.Count, newBucket.ActiveCount);
                owner.SetRegisteredSlotAndBucket(slot, newBucketIdx);
                _slotBucketIndices[slot] = newBucketIdx;
            }

            inst.PackedMeta = (inst.PackedMeta & 0x00FFFFFFu) | ((uint)(newBucketIdx & 0xFF) << 24);
            _cpuInstances[slot] = inst;

            int dynIdx = _slotDynamicIndices[slot];
            bool wantsDynamic = owner.MotionMode == SpriteMotionMode.DynamicTransform;

            if (wantsDynamic && dynIdx < 0)
            {
                _slotDynamicIndices[slot] = _transformSync.Add(owner.transform, slot, flipX, flipY, inst.SortKey);
            }
            else if (!wantsDynamic && dynIdx >= 0)
            {
                _transformSync.RemoveAtSwapBack(dynIdx, out int movedInstSlot);
                if (movedInstSlot >= 0 && movedInstSlot < _activeSpriteCount)
                {
                    _slotDynamicIndices[movedInstSlot] = dynIdx;
                }
                _slotDynamicIndices[slot] = -1;
            }
            else if (wantsDynamic && dynIdx >= 0)
            {
                _transformSync.UpdateMeta(dynIdx, slot, flipX, flipY, inst.SortKey);
            }

            _sourceBufferDirty = true;
        }

        public void UnregisterRenderer(SpriteBridgeRenderer owner)
        {
            if (!_isInitialized || owner == null)
            {
                return;
            }

            int slot = owner.RegisteredSlotIndex;
            if (slot < 0 || slot >= _activeSpriteCount)
            {
                owner.SetRegisteredSlotAndBucket(-1, -1);
                return;
            }

            RemoveDenseSlotSwapBack(slot);
            owner.SetRegisteredSlotAndBucket(-1, -1);
        }

        // ---------------------------------------------------------------------
        // Tier 2: Zero-GameObject High-Density Batch API (100k+ Sprites)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Registers or fetches a draw bucket for Zero-GameObject batch spawning.
        /// All sprites sharing the same Material + SpriteAtlas Texture collapse into 1 Indirect DrawCall.
        /// </summary>
        public int RegisterBatchBucket(
            Material sourceMaterial,
            Texture2D atlasTexture,
            SpriteSortMode sortMode = SpriteSortMode.None,
            int sortingLayerId = 0,
            int sortingOrder = 0,
            int layer = 0)
        {
            EnsureInitialized();
            if (!_isInitialized || !_isHardwareSupported || atlasTexture == null)
            {
                return -1;
            }

            int sortingLayerVal = SortingLayer.GetLayerValueFromID(sortingLayerId);
            int orderBucket = (sortMode == SpriteSortMode.BucketedOrder) ? sortingOrder : 0;
            int matId = sourceMaterial != null ? sourceMaterial.GetInstanceID() : 0;
            int texId = atlasTexture.GetInstanceID();

            SpriteBucketKey key = new SpriteBucketKey(matId, texId, sortingLayerVal, orderBucket, sortMode, layer);
            return GetOrCreateBucket(key, sourceMaterial, atlasTexture);
        }

        /// <summary>
        /// Spawns a Zero-GameObject static-UV GPU sprite and returns a generational SpriteBridgeHandle.
        /// </summary>
        public SpriteBridgeHandle SpawnSprite(
            int bucketId,
            Sprite sprite,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            Color color,
            int sortingOrder = 0,
            SpriteBillboardMode billboard = SpriteBillboardMode.None)
        {
            if (!IsBucketValid(bucketId) || sprite == null)
            {
                return SpriteBridgeHandle.Invalid;
            }

            if (!SpriteGeometryUtility.TryExtractSpriteGeometry(
                    sprite,
                    out _,
                    out Vector4 uvRect,
                    out Vector4 pivotSize,
                    out uint packingRotBits,
                    out _))
            {
                return SpriteBridgeHandle.Invalid;
            }

            return SpawnRawInstance(
                bucketId,
                position,
                rotation,
                scale,
                color,
                uvRect,
                pivotSize,
                GpuSpriteInstance.StaticAnimClipIndex,
                0f,
                1f,
                sortingOrder,
                billboard,
                packingRotBits,
                false);
        }

        /// <summary>
        /// Spawns a Zero-GameObject GPU-animated sprite driven by a baked SpriteBridgeAnimationLibrary.
        /// </summary>
        public SpriteBridgeHandle SpawnAnimatedSprite(
            int bucketId,
            SpriteBridgeAnimationLibrary animLibrary,
            int localClipIndex,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            Color color,
            float animSpeed = 1f,
            float startTimeOffset = 0f,
            int sortingOrder = 0,
            SpriteBillboardMode billboard = SpriteBillboardMode.None)
        {
            if (!IsBucketValid(bucketId) || animLibrary == null)
            {
                return SpriteBridgeHandle.Invalid;
            }

            uint globalClip = ResolveGlobalClipIndex(animLibrary, localClipIndex);
            if (globalClip == GpuSpriteInstance.StaticAnimClipIndex)
            {
                return SpriteBridgeHandle.Invalid;
            }

            animLibrary.TryGetFirstFrameGeometry(localClipIndex, out Vector4 uvRect, out Vector4 pivotSize);
            bool loop = animLibrary.TryGetClip(localClipIndex, out SpriteBridgeAnimationLibrary.BakedClipMetadata meta) && meta.Loop;

            return SpawnRawInstance(
                bucketId,
                position,
                rotation,
                scale,
                color,
                uvRect,
                pivotSize,
                globalClip,
                startTimeOffset,
                animSpeed,
                sortingOrder,
                billboard,
                0u,
                loop);
        }

        public bool IsHandleValid(SpriteBridgeHandle handle)
        {
            if (!_isInitialized || !handle.IsValid || handle.SlotIndex >= _handleCount)
            {
                return false;
            }

            return _handleGenerations[handle.SlotIndex] == handle.Generation &&
                   _handleToDenseSlot[handle.SlotIndex] >= 0;
        }

        public bool TryGetSlotIndex(SpriteBridgeHandle handle, out int denseSlotIndex)
        {
            if (IsHandleValid(handle))
            {
                denseSlotIndex = _handleToDenseSlot[handle.SlotIndex];
                return denseSlotIndex >= 0 && denseSlotIndex < _activeSpriteCount;
            }

            denseSlotIndex = -1;
            return false;
        }

        public bool UpdateSpriteTransform(SpriteBridgeHandle handle, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            if (!TryGetSlotIndex(handle, out int slot))
            {
                return false;
            }

            CompletePendingTransformSyncJob();
            GpuSpriteInstance inst = _cpuInstances[slot];
            inst.Position = position;
            inst.Rotation = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w);
            inst.Scale = scale;
            _cpuInstances[slot] = inst;
            _sourceBufferDirty = true;
            return true;
        }

        public bool UpdateSpriteColor(SpriteBridgeHandle handle, Color color)
        {
            if (!TryGetSlotIndex(handle, out int slot))
            {
                return false;
            }

            CompletePendingTransformSyncJob();
            GpuSpriteInstance inst = _cpuInstances[slot];
            SpriteGeometryUtility.PackColorHalf4(color, out inst.PackedColorRG, out inst.PackedColorBA);
            _cpuInstances[slot] = inst;
            _sourceBufferDirty = true;
            return true;
        }

        public bool UpdateSpriteAnimation(
            SpriteBridgeHandle handle,
            SpriteBridgeAnimationLibrary animLibrary,
            int localClipIndex,
            float speed = 1f,
            float startTimeOffset = 0f)
        {
            if (!TryGetSlotIndex(handle, out int slot))
            {
                return false;
            }

            uint globalClip = ResolveGlobalClipIndex(animLibrary, localClipIndex);
            CompletePendingTransformSyncJob();
            GpuSpriteInstance inst = _cpuInstances[slot];
            uint flagsAndBucket = inst.PackedMeta & 0xFFFF0000u;
            inst.PackedMeta = flagsAndBucket | (globalClip & 0xFFFFu);
            inst.AnimSpeed = speed;
            inst.AnimTimeOffset = startTimeOffset;
            _cpuInstances[slot] = inst;
            _sourceBufferDirty = true;
            return true;
        }

        public bool DespawnSprite(SpriteBridgeHandle handle)
        {
            if (!TryGetSlotIndex(handle, out int slot))
            {
                return false;
            }

            RemoveDenseSlotSwapBack(slot);
            return true;
        }

        /// <summary>
        /// High-throughput Burst direct-write escape hatch: completes any pending TransformSync job,
        /// marks the GPU buffer dirty, and returns the backing NativeArray&lt;GpuSpriteInstance&gt; so user
        /// IJobParallelFor Burst jobs can update 100,000+ Zero-GameObject sprites in &lt; 0.1 ms with zero copies.
        /// </summary>
        public NativeArray<GpuSpriteInstance> AcquireWritableInstanceBuffer(out int activeCount)
        {
            CompletePendingTransformSyncJob();
            _sourceBufferDirty = true;
            activeCount = _activeSpriteCount;
            return _cpuInstances;
        }

        private SpriteBridgeHandle SpawnRawInstance(
            int bucketId,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            Color color,
            Vector4 uvRect,
            Vector4 pivotSize,
            uint globalAnimClipIndex,
            float animTimeOffset,
            float animSpeed,
            int sortingOrder,
            SpriteBillboardMode billboard,
            uint packingRotBits,
            bool animLoop)
        {
            CompletePendingTransformSyncJob();

            if (_activeSpriteCount >= _instanceCapacity)
            {
                GrowInstanceCapacity(_instanceCapacity * 2);
            }

            int handleId = AllocateHandleId(out uint generation);
            int slot = _activeSpriteCount++;

            SpriteMaterialBucket bucket = _buckets[bucketId];
            GpuSpriteFlags flags = GpuSpriteFlags.Active;
            if (billboard == SpriteBillboardMode.FullBillboard) flags |= GpuSpriteFlags.BillboardFull;
            else if (billboard == SpriteBillboardMode.YAxisBillboard) flags |= GpuSpriteFlags.BillboardYAxis;
            if (animLoop) flags |= GpuSpriteFlags.AnimLoop;
            if ((packingRotBits & 1u) != 0u) flags |= GpuSpriteFlags.PackingRotBit0;
            if ((packingRotBits & 2u) != 0u) flags |= GpuSpriteFlags.PackingRotBit1;
            if (bucket.SortMode == SpriteSortMode.GpuOrderAndYSort) flags |= GpuSpriteFlags.CombineOrderWithYSort;

            SpriteGeometryUtility.PackColorHalf4(color, out uint packedRG, out uint packedBA);

            _cpuInstances[slot] = new GpuSpriteInstance
            {
                Position = position,
                SortKey = sortingOrder,
                Rotation = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w),
                Scale = scale,
                PackedMeta = SpriteGeometryUtility.PackMeta(globalAnimClipIndex, flags, (uint)bucketId),
                UvRect = uvRect,
                PivotSize = pivotSize,
                PackedColorRG = packedRG,
                PackedColorBA = packedBA,
                AnimTimeOffset = animTimeOffset,
                AnimSpeed = animSpeed
            };

            _slotOwners[slot] = null;
            _slotDynamicIndices[slot] = -1;
            _slotBucketIndices[slot] = bucketId;
            _denseSlotToHandleId[slot] = handleId;
            _handleToDenseSlot[handleId] = slot;

            bucket.ActiveCount++;
            EnsureVisibleBufferCapacity(_buckets.Count, bucket.ActiveCount);
            _sourceBufferDirty = true;

            return new SpriteBridgeHandle(handleId, generation);
        }

        private int AllocateHandleId(out uint generation)
        {
            if (_freeHandleIds.Count > 0)
            {
                int recycled = _freeHandleIds.Pop();
                generation = _handleGenerations[recycled];
                return recycled;
            }

            if (_handleCount >= _handleCapacity)
            {
                int newCap = Mathf.Max(256, _handleCapacity * 2);
                int[] newMap = new int[newCap];
                uint[] newGen = new uint[newCap];
                Array.Copy(_handleToDenseSlot, newMap, _handleCapacity);
                Array.Copy(_handleGenerations, newGen, _handleCapacity);
                for (int i = _handleCapacity; i < newCap; i++)
                {
                    newMap[i] = -1;
                    newGen[i] = 1u;
                }
                _handleToDenseSlot = newMap;
                _handleGenerations = newGen;
                _handleCapacity = newCap;
            }

            int id = _handleCount++;
            generation = _handleGenerations[id];
            return id;
        }

        private bool IsBucketValid(int bucketId)
        {
            return _isInitialized && bucketId >= 0 && bucketId < _buckets.Count;
        }

        private void RemoveDenseSlotSwapBack(int slot)
        {
            CompletePendingTransformSyncJob();

            int bucketIdx = _slotBucketIndices[slot];
            if (bucketIdx >= 0 && bucketIdx < _buckets.Count)
            {
                _buckets[bucketIdx].ActiveCount = Mathf.Max(0, _buckets[bucketIdx].ActiveCount - 1);
            }

            // Step 1: Remove from TransformSync first while lastSlot is untouched
            int removedDynIdx = _slotDynamicIndices[slot];
            if (removedDynIdx >= 0)
            {
                _transformSync.RemoveAtSwapBack(removedDynIdx, out int movedInstSlot);
                if (movedInstSlot >= 0 && movedInstSlot < _activeSpriteCount)
                {
                    _slotDynamicIndices[movedInstSlot] = removedDynIdx;
                }
                _slotDynamicIndices[slot] = -1;
            }

            // Step 2: Release Tier 2 Handle if owned by handle table
            int removedHandleId = _denseSlotToHandleId[slot];
            if (removedHandleId >= 0 && removedHandleId < _handleCapacity)
            {
                _handleToDenseSlot[removedHandleId] = -1;
                uint nextGen = _handleGenerations[removedHandleId] + 1u;
                _handleGenerations[removedHandleId] = nextGen == 0u ? 1u : nextGen;
                _freeHandleIds.Push(removedHandleId);
                _denseSlotToHandleId[slot] = -1;
            }

            // Step 3: Swap-and-Pop dense slot
            int lastSlot = _activeSpriteCount - 1;
            if (slot != lastSlot)
            {
                _cpuInstances[slot] = _cpuInstances[lastSlot];

                SpriteBridgeRenderer movedOwner = _slotOwners[lastSlot];
                _slotOwners[slot] = movedOwner;

                int movedDynIdx = _slotDynamicIndices[lastSlot];
                _slotDynamicIndices[slot] = movedDynIdx;

                int movedBucket = _slotBucketIndices[lastSlot];
                _slotBucketIndices[slot] = movedBucket;

                int movedHandleId = _denseSlotToHandleId[lastSlot];
                _denseSlotToHandleId[slot] = movedHandleId;

                if (movedOwner != null)
                {
                    movedOwner.SetRegisteredSlotAndBucket(slot, movedBucket);
                }

                if (movedDynIdx >= 0)
                {
                    _transformSync.UpdateTargetInstanceSlot(movedDynIdx, slot);
                }

                if (movedHandleId >= 0 && movedHandleId < _handleCapacity)
                {
                    _handleToDenseSlot[movedHandleId] = slot;
                }
            }

            // Step 4: Clear tail
            _slotOwners[lastSlot] = null;
            _slotDynamicIndices[lastSlot] = -1;
            _slotBucketIndices[lastSlot] = -1;
            _denseSlotToHandleId[lastSlot] = -1;
            _activeSpriteCount--;

            _sourceBufferDirty = true;
        }

        // ---------------------------------------------------------------------
        // Bucket Management & Automatic Material Bridging
        // ---------------------------------------------------------------------

        private int GetOrCreateBucket(SpriteBucketKey key, Material sourceMaterial, Texture2D atlasTexture)
        {
            if (_bucketLookup.TryGetValue(key, out int existingIdx))
            {
                return existingIdx;
            }

            if (_buckets.Count >= MaxBuckets)
            {
                Debug.LogWarning($"[SpriteBridge] Reached maximum bucket limit ({MaxBuckets}). Consider packing sprites into a SpriteAtlas or using GpuYSort.");
                return -1;
            }

            Material runtimeMat = CreateBridgedMaterial(sourceMaterial, atlasTexture);
            if (runtimeMat == null)
            {
                return -1;
            }

            int bucketIdx = _buckets.Count;
            bool supports3DShadows = runtimeMat.FindPass("ShadowCaster") >= 0;
            SpriteMaterialBucket bucket = new SpriteMaterialBucket
            {
                BucketIndex = bucketIdx,
                Key = key,
                SourceMaterial = sourceMaterial,
                RuntimeMaterial = runtimeMat,
                AtlasTexture = atlasTexture,
                SortingLayerValue = key.SortingLayerValue,
                SortingOrderBucket = key.SortingOrderBucket,
                SortMode = (SpriteSortMode)key.SortModeInt,
                Layer = key.Layer,
                ActiveCount = 0,
                BaseRenderQueue = runtimeMat.renderQueue,
                CastAndReceive3DShadows = supports3DShadows
            };

            _buckets.Add(bucket);
            _sortedDrawOrder.Add(bucket);
            _bucketLookup[key] = bucketIdx;
            _bucketOrderDirty = true;

            if (bucket.UsesGpuSort)
            {
                _hasAnySortedBucket = true;
            }

            EnsureVisibleBufferCapacity(_buckets.Count, _maxInstancesPerBucket);
            BindBuffersToAllBucketMaterials();

            return bucketIdx;
        }

        private Material CreateBridgedMaterial(Material sourceMaterial, Texture2D atlasTexture)
        {
            Shader targetShader = ResolveBridgedShader(sourceMaterial);
            if (targetShader == null)
            {
                return null;
            }

            bool sourceAlreadyCompatible = sourceMaterial != null &&
                                           sourceMaterial.shader != null &&
                                           sourceMaterial.shader == targetShader;

            Material mat = sourceAlreadyCompatible
                ? new Material(sourceMaterial)
                : new Material(targetShader);

            mat.name = $"SpriteBridge_Mat_{(atlasTexture != null ? atlasTexture.name : "Tex")}_{_buckets.Count}";
            mat.hideFlags = HideFlags.HideAndDontSave;

            if (atlasTexture != null)
            {
                mat.SetTexture(s_MainTexPropId, atlasTexture);
            }

            if (sourceMaterial != null && !sourceAlreadyCompatible)
            {
                CopySourceMaterialProperties(sourceMaterial, mat);
            }

            return mat;
        }

        private static void CopySourceMaterialProperties(Material sourceMaterial, Material targetMat)
        {
            if (sourceMaterial == null || targetMat == null)
            {
                return;
            }

            if (sourceMaterial.HasProperty(s_ColorPropId) && targetMat.HasProperty(s_ColorPropId))
            {
                targetMat.SetColor(s_ColorPropId, sourceMaterial.GetColor(s_ColorPropId));
            }
            if (sourceMaterial.HasProperty(s_MaskTexPropId) && targetMat.HasProperty(s_MaskTexPropId))
            {
                targetMat.SetTexture(s_MaskTexPropId, sourceMaterial.GetTexture(s_MaskTexPropId));
            }
            if (sourceMaterial.HasProperty(s_NormalMapPropId) && targetMat.HasProperty(s_NormalMapPropId))
            {
                targetMat.SetTexture(s_NormalMapPropId, sourceMaterial.GetTexture(s_NormalMapPropId));
            }
            if (sourceMaterial.HasProperty(s_ZWritePropId) && targetMat.HasProperty(s_ZWritePropId))
            {
                targetMat.SetFloat(s_ZWritePropId, sourceMaterial.GetFloat(s_ZWritePropId));
            }
            if (sourceMaterial.HasProperty(s_CutoffPropId) && targetMat.HasProperty(s_CutoffPropId))
            {
                targetMat.SetFloat(s_CutoffPropId, sourceMaterial.GetFloat(s_CutoffPropId));
            }
            if (sourceMaterial.HasProperty(s_SrcBlendPropId) && targetMat.HasProperty(s_SrcBlendPropId))
            {
                targetMat.SetFloat(s_SrcBlendPropId, sourceMaterial.GetFloat(s_SrcBlendPropId));
            }
            if (sourceMaterial.HasProperty(s_DstBlendPropId) && targetMat.HasProperty(s_DstBlendPropId))
            {
                targetMat.SetFloat(s_DstBlendPropId, sourceMaterial.GetFloat(s_DstBlendPropId));
            }
            if (sourceMaterial.IsKeywordEnabled("_ALPHATEST_ON"))
            {
                targetMat.EnableKeyword("_ALPHATEST_ON");
            }
            else
            {
                targetMat.DisableKeyword("_ALPHATEST_ON");
            }
        }

        /// <summary>
        /// Synchronizes live material property edits (_Color, _Cutoff, _NormalMap, _ZWrite, _ALPHATEST_ON)
        /// from each bucket's SourceMaterial to its RuntimeMaterial.
        /// </summary>
        public void SyncBucketMaterialsFromSource()
        {
            for (int i = 0; i < _buckets.Count; i++)
            {
                SpriteMaterialBucket b = _buckets[i];
                if (b.SourceMaterial != null && b.RuntimeMaterial != null)
                {
                    CopySourceMaterialProperties(b.SourceMaterial, b.RuntimeMaterial);
                    if (b.AtlasTexture != null)
                    {
                        b.RuntimeMaterial.SetTexture(s_MainTexPropId, b.AtlasTexture);
                    }
                }
            }
        }

        private void RebindActiveSceneRenderers()
        {
            if (!_isInitialized || !_isHardwareSupported)
            {
                return;
            }

#if UNITY_2023_1_OR_NEWER
            SpriteBridgeRenderer[] renderers = FindObjectsByType<SpriteBridgeRenderer>(FindObjectsSortMode.None);
#else
            SpriteBridgeRenderer[] renderers = FindObjectsOfType<SpriteBridgeRenderer>();
#endif
            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteBridgeRenderer r = renderers[i];
                if (r != null && r.isActiveAndEnabled && r.gameObject.scene.IsValid() && r.RegisteredSlotIndex < 0)
                {
                    r.MarkDirty();
                }
            }
        }

        private Shader ResolveBridgedShader(Material sourceMaterial)
        {
            if (sourceMaterial != null && sourceMaterial.shader != null)
            {
                string shaderName = sourceMaterial.shader.name;
                if (shaderName.StartsWith("SpriteBridge/", StringComparison.Ordinal))
                {
                    return sourceMaterial.shader;
                }

                if (shaderName.IndexOf("Sprite-Lit", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Shader lit2d = _resources != null && _resources.Lit2DShader != null
                        ? _resources.Lit2DShader
                        : Shader.Find("SpriteBridge/URP 2D/Sprite-Lit");
                    if (lit2d != null)
                    {
                        return lit2d;
                    }
                }
            }

            Shader unlit = _resources != null && _resources.UnlitShader != null
                ? _resources.UnlitShader
                : Shader.Find("SpriteBridge/URP/Sprite-Unlit");
            return unlit;
        }

        private void SortBucketsAndAssignRenderQueuesIfDirty()
        {
            if (!_bucketOrderDirty)
            {
                return;
            }
            _bucketOrderDirty = false;

            _sortedDrawOrder.Sort((a, b) =>
            {
                int cmp = a.SortingLayerValue.CompareTo(b.SortingLayerValue);
                if (cmp != 0) return cmp;
                cmp = a.SortingOrderBucket.CompareTo(b.SortingOrderBucket);
                if (cmp != 0) return cmp;
                return a.BucketIndex.CompareTo(b.BucketIndex);
            });

            for (int rank = 0; rank < _sortedDrawOrder.Count; rank++)
            {
                SpriteMaterialBucket b = _sortedDrawOrder[rank];
                if (b.RuntimeMaterial != null && b.BaseRenderQueue >= 2500)
                {
                    int targetQueue = Mathf.Min(3900, b.BaseRenderQueue + rank);
                    b.RuntimeMaterial.renderQueue = targetQueue;
                }
            }
        }

        // ---------------------------------------------------------------------
        // CPU -> GPU Sync & URP Pass Injection
        // ---------------------------------------------------------------------

        private void CompletePendingTransformSyncJob()
        {
            if (_hasPendingSyncJob)
            {
                _pendingSyncJob.Complete();
                _hasPendingSyncJob = false;
            }
        }

        private void SyncDynamicTransformsAndUpload()
        {
            if (!_isInitialized || _activeSpriteCount <= 0)
            {
                return;
            }

            if (Application.isPlaying && _slotOwners != null)
            {
                for (int i = 0; i < _activeSpriteCount; i++)
                {
                    SpriteBridgeRenderer owner = _slotOwners[i];
                    if (owner != null && owner.AutoSyncRendererProperties && owner.MotionMode == SpriteMotionMode.DynamicTransform)
                    {
                        owner.PollRuntimePropertiesIfNeeded();
                    }
                }
            }

            UploadAnimationBuffersIfDirty();

            bool hasDynamic = _transformSync != null && _transformSync.DynamicCount > 0;
            if (hasDynamic)
            {
                _pendingSyncJob = _transformSync.ScheduleSync(_cpuInstances);
                _hasPendingSyncJob = true;
                CompletePendingTransformSyncJob();
                _sourceBufferDirty = true;
            }

            if (_sourceBufferDirty && _sourceSpritesBuffer != null && _sourceSpritesBuffer.IsValid())
            {
                _sourceSpritesBuffer.SetData(_cpuInstances, 0, 0, _activeSpriteCount);
                _sourceBufferDirty = false;
            }
        }

        internal bool ShouldRenderForCamera(Camera camera)
        {
            if (camera == null)
            {
                return false;
            }

            if (camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.Reflection)
            {
                return false;
            }

            if (camera.cameraType == CameraType.SceneView)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    return true;
                }
                return PlayModeSceneViewDebugMode == SpriteSceneViewDebugMode.GpuSprites;
#else
                return false;
#endif
            }

            return true;
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!_isInitialized || !_isHardwareSupported || _activeSpriteCount <= 0 || camera == null)
            {
                return;
            }

#if UNITY_EDITOR
            if (IsNativeSceneViewDebugActive)
            {
                if (camera.cameraType != CameraType.SceneView)
                {
                    SetAllNativeRenderersForceRenderingOffForEditor(true);
                    _hidNativeRenderersForNonSceneCamera = true;
                }
                else
                {
                    if (_hidNativeRenderersForNonSceneCamera)
                    {
                        SetAllNativeRenderersForceRenderingOffForEditor(false);
                        _hidNativeRenderersForNonSceneCamera = false;
                    }
                    return;
                }
            }
#endif

            if (!ShouldRenderForCamera(camera))
            {
                return;
            }

            if (!Application.isPlaying)
            {
                SyncBucketMaterialsFromSource();
            }

            if (!Application.isPlaying || _sourceBufferDirty || _animBuffersDirty)
            {
                SyncDynamicTransformsAndUpload();
            }

            SortBucketsAndAssignRenderQueuesIfDirty();

            UniversalAdditionalCameraData additionalCameraData = camera.GetUniversalAdditionalCameraData();
            if (additionalCameraData != null && additionalCameraData.scriptableRenderer != null && _computePass != null)
            {
                additionalCameraData.scriptableRenderer.EnqueuePass(_computePass);
            }

            if (!Application.isPlaying || camera.cameraType == CameraType.SceneView)
            {
                IssueIndirectDrawsForCamera(camera);
            }
        }

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
#if UNITY_EDITOR
            if (_hidNativeRenderersForNonSceneCamera)
            {
                _hidNativeRenderersForNonSceneCamera = false;
                if (IsNativeSceneViewDebugActive)
                {
                    SetAllNativeRenderersForceRenderingOffForEditor(false);
                }
            }
#endif
        }

        private void OnBuiltInCameraPreCull(Camera camera)
        {
            // Only active when running on Unity's Built-in Render Pipeline (GraphicsSettings.currentRenderPipeline == null)
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                return;
            }

            if (!_isInitialized || !_isHardwareSupported || _activeSpriteCount <= 0 || camera == null)
            {
                return;
            }

#if UNITY_EDITOR
            if (IsNativeSceneViewDebugActive)
            {
                if (camera.cameraType != CameraType.SceneView)
                {
                    SetAllNativeRenderersForceRenderingOffForEditor(true);
                    _hidNativeRenderersForNonSceneCamera = true;
                }
                else
                {
                    if (_hidNativeRenderersForNonSceneCamera)
                    {
                        SetAllNativeRenderersForceRenderingOffForEditor(false);
                        _hidNativeRenderersForNonSceneCamera = false;
                    }
                    return;
                }
            }
#endif

            if (!ShouldRenderForCamera(camera))
            {
                return;
            }

            if (!Application.isPlaying)
            {
                SyncBucketMaterialsFromSource();
            }

            if (!Application.isPlaying || _sourceBufferDirty || _animBuffersDirty)
            {
                SyncDynamicTransformsAndUpload();
            }

            SortBucketsAndAssignRenderQueuesIfDirty();

            _computePass?.ExecuteImmediateForBuiltInRP(camera);

            if (!Application.isPlaying || camera.cameraType == CameraType.SceneView)
            {
                IssueIndirectDrawsForCamera(camera);
            }
        }

        private void OnBuiltInCameraPostRender(Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                return;
            }

#if UNITY_EDITOR
            if (_hidNativeRenderersForNonSceneCamera)
            {
                _hidNativeRenderersForNonSceneCamera = false;
                if (IsNativeSceneViewDebugActive)
                {
                    SetAllNativeRenderersForceRenderingOffForEditor(false);
                }
            }
#endif
        }

        private void IssueIndirectDrawsForPlayModeGameCameras()
        {
            SortBucketsAndAssignRenderQueuesIfDirty();

            int camCount = Camera.allCamerasCount;
            if (camCount <= 0)
            {
                return;
            }

            if (_cameraScratch == null || _cameraScratch.Length < camCount)
            {
                _cameraScratch = new Camera[Mathf.NextPowerOfTwo(Mathf.Max(8, camCount))];
            }

            int fetched = Camera.GetAllCameras(_cameraScratch);
            for (int c = 0; c < fetched; c++)
            {
                Camera cam = _cameraScratch[c];
                if (cam != null && cam.isActiveAndEnabled && cam.cameraType == CameraType.Game)
                {
                    IssueIndirectDrawsForCamera(cam);
                }
            }
        }

        private void IssueIndirectDrawsForCamera(Camera camera)
        {
            if (_unitQuadMesh == null || _indirectArgsBuffer == null || !_indirectArgsBuffer.IsValid() || camera == null)
            {
                return;
            }

            int cameraCullingMask = camera.cullingMask;
            if (cameraCullingMask == 0)
            {
                return;
            }

            int sortedCount = _sortedDrawOrder.Count;
            for (int i = 0; i < sortedCount; i++)
            {
                SpriteMaterialBucket bucket = _sortedDrawOrder[i];
                if (bucket.ActiveCount <= 0 || bucket.RuntimeMaterial == null)
                {
                    continue;
                }

                if ((cameraCullingMask & (1 << bucket.Layer)) == 0)
                {
                    continue;
                }

                RenderParams rp = new RenderParams(bucket.RuntimeMaterial)
                {
                    worldBounds = s_HugeBounds,
                    camera = camera,
                    shadowCastingMode = bucket.CastAndReceive3DShadows ? ShadowCastingMode.TwoSided : ShadowCastingMode.Off,
                    receiveShadows = bucket.CastAndReceive3DShadows,
                    layer = bucket.Layer
                };

                Graphics.RenderMeshIndirect(
                    rp,
                    _unitQuadMesh,
                    _indirectArgsBuffer,
                    commandCount: 1,
                    startCommand: bucket.BucketIndex);
            }
        }

        // ---------------------------------------------------------------------
        // Diagnostics & Editor Helpers
        // ---------------------------------------------------------------------

        public uint ReadbackVisibleSpriteCount()
        {
            if (!_isInitialized || _indirectArgsBuffer == null || !_indirectArgsBuffer.IsValid())
            {
                return 0u;
            }

            _indirectArgsBuffer.GetData(_readbackIndirectArgsScratch);
            uint total = 0u;
            for (int i = 0; i < _buckets.Count; i++)
            {
                total += _readbackIndirectArgsScratch[i * 5 + 1];
            }
            return total;
        }

#if UNITY_EDITOR
        public int EditorSlotCount => _activeSpriteCount;

        public bool EditorTryGetSlot(int slotIndex, out SpriteBridgeRenderer owner, out GpuSpriteInstance instance)
        {
            owner = null;
            instance = default;
            if (!_isInitialized || slotIndex < 0 || slotIndex >= _activeSpriteCount || !_cpuInstances.IsCreated)
            {
                return false;
            }

            owner = _slotOwners[slotIndex];
            instance = _cpuInstances[slotIndex];
            return owner != null;
        }

        private void ApplyDebugModeToAllRegisteredRenderers()
        {
            _hidNativeRenderersForNonSceneCamera = false;
            if (_slotOwners == null)
            {
                return;
            }

            bool nativeMode = IsNativeSceneViewDebugActive;
            for (int i = 0; i < _activeSpriteCount; i++)
            {
                SpriteBridgeRenderer owner = _slotOwners[i];
                if (owner != null)
                {
                    owner.ApplyNativeRendererVisibility(nativeMode);
                }
            }
        }

        private void SetAllNativeRenderersForceRenderingOffForEditor(bool forceOff)
        {
            if (_slotOwners == null)
            {
                return;
            }

            for (int i = 0; i < _activeSpriteCount; i++)
            {
                SpriteBridgeRenderer owner = _slotOwners[i];
                if (owner != null && owner.NativeSpriteRenderer != null)
                {
                    owner.NativeSpriteRenderer.forceRenderingOff = forceOff;
                }
            }
        }
#endif
    }
}
