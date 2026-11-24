using UnityEngine;

namespace SpriteBridge
{
    /// <summary>
    /// Drop-in component that hijacks a Unity SpriteRenderer (and optional 2D Animator) and batches it onto the GPU
    /// via SpriteBridgeManager. Keeps 100% of Unity's SpriteRenderer Inspector workflow.
    /// Automatically falls back to native SpriteRenderer for unsupported modes (Sliced/Tiled, SpriteMask, or unsupported hardware).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SpriteRenderer))]
    [AddComponentMenu("SpriteBridge/Sprite Bridge Renderer")]
    public sealed class SpriteBridgeRenderer : MonoBehaviour
    {
        [Header("Motion & Sync")]
        [Tooltip("Static: 0.000 ms/frame CPU after upload (call MarkDirty() if moved). DynamicTransform: auto-synced every frame via Burst/Job TransformAccessArray.")]
        [SerializeField] private SpriteMotionMode _motionMode = SpriteMotionMode.Static;

        [Tooltip("When enabled on DynamicTransform sprites, automatically detects runtime changes to SpriteRenderer.flipX/Y, color, sprite, and sortingOrder without requiring manual MarkDirty() calls.")]
        [SerializeField] private bool _autoSyncRendererProperties = true;

        [Header("Sorting & Orientation")]
        [Tooltip("BucketedOrder: zero GPU sort passes, deterministic SortingLayer+Order draw buckets. GpuYSort / GpuOrderAndYSort: GPU Bitonic Sort back-to-front by world -Y.")]
        [SerializeField] private SpriteSortMode _sortMode = SpriteSortMode.BucketedOrder;

        [Tooltip("Optional camera-facing billboard evaluated on the GPU (ideal for 2.5D / HD-2D games).")]
        [SerializeField] private SpriteBillboardMode _billboardMode = SpriteBillboardMode.None;

        [Header("GPU 2D Animation (Optional)")]
        [Tooltip("Baked GPU Sprite Animation Library. When assigned, playback runs 100% on GPU with zero Animator CPU cost.")]
        [SerializeField] private SpriteBridgeAnimationLibrary _animationLibrary;

        [Tooltip("Clip name inside AnimationLibrary (leave empty to use DefaultClipIndex).")]
        [SerializeField] private string _defaultClipName;

        [Tooltip("Fallback local clip index inside AnimationLibrary when DefaultClipName is empty (-1 = static sprite).")]
        [SerializeField] private int _defaultClipIndex = -1;

        [SerializeField] private float _animSpeed = 1f;

        [Tooltip("Randomizes initial animation phase so crowds of identical sprites don't animate in lockstep.")]
        [SerializeField] private bool _randomizeStartPhase = true;

        [Tooltip("Automatically sets attached Animator.enabled = false during Play Mode GPU rendering (restored in NativeSpriteRenderer debug mode & OnDisable).")]
        [SerializeField] private bool _disableNativeAnimator = true;

        private SpriteRenderer _spriteRenderer;
        private Animator _nativeAnimator;
        private int _registeredSlotIndex = -1;
        private int _registeredBucketIndex = -1;
        private int _activeLocalClipIndex = -1;
        private float _animTimeOffset;
        private bool _hasInitializedPhase;
        private bool _isAutoFallback;
        private string _fallbackReason;
        private bool _isTightPackedWarning;

        // Runtime & Editor change-detection snapshot
        private Sprite _lastSprite;
        private Color _lastColor;
        private bool _lastFlipX;
        private bool _lastFlipY;
        private bool _lastRendererEnabled = true;
        private int _lastSortingLayerId;
        private int _lastSortingOrder;
        private int _lastLayer = -1;
        private bool _pendingValidateDirty;
#if UNITY_EDITOR
        private Material _lastMaterial;
        private Vector3 _lastPos;
        private Quaternion _lastRot;
        private Vector3 _lastScale;
        private SpriteDrawMode _lastDrawMode;
        private SpriteMaskInteraction _lastMaskInteraction;
#endif

        public SpriteRenderer NativeSpriteRenderer
        {
            get
            {
                if (_spriteRenderer == null)
                {
                    _spriteRenderer = GetComponent<SpriteRenderer>();
                }
                return _spriteRenderer;
            }
        }

        public SpriteMotionMode MotionMode
        {
            get => _motionMode;
            set
            {
                if (_motionMode != value)
                {
                    _motionMode = value;
                    MarkDirty();
                }
            }
        }

        public bool AutoSyncRendererProperties
        {
            get => _autoSyncRendererProperties;
            set => _autoSyncRendererProperties = value;
        }

        public SpriteSortMode SortMode
        {
            get => _sortMode;
            set
            {
                if (_sortMode != value)
                {
                    _sortMode = value;
                    MarkDirty();
                }
            }
        }

        public SpriteBillboardMode BillboardMode
        {
            get => _billboardMode;
            set
            {
                if (_billboardMode != value)
                {
                    _billboardMode = value;
                    MarkDirty();
                }
            }
        }

        public SpriteBridgeAnimationLibrary AnimationLibrary
        {
            get => _animationLibrary;
            set
            {
                if (_animationLibrary != value)
                {
                    _animationLibrary = value;
                    ResolveInitialClipIndex();
                    MarkDirty();
                }
            }
        }

        public int RegisteredSlotIndex => _registeredSlotIndex;
        public int RegisteredBucketIndex => _registeredBucketIndex;
        public int ActiveLocalClipIndex => _activeLocalClipIndex;
        public bool IsGpuActive => _registeredSlotIndex >= 0 && !_isAutoFallback;
        public bool IsAutoFallback => _isAutoFallback;
        public string FallbackReason => _fallbackReason;
        public bool IsTightPackedWarning => _isTightPackedWarning;

        /// <summary>
        /// Plays a baked GPU 2D animation clip by local clip index (0.000 ms Animator CPU).
        /// </summary>
        public void PlayClip(int localClipIndex, float speed = 1f, float startTimeOffset = 0f)
        {
            _activeLocalClipIndex = localClipIndex;
            _animSpeed = Mathf.Max(0.0001f, speed);
            float now = Application.isPlaying ? Time.time : (float)Time.realtimeSinceStartup;
            _animTimeOffset = startTimeOffset - now;
            _hasInitializedPhase = true;
            MarkDirty();
        }

        /// <summary>
        /// Plays a baked GPU 2D animation clip by clip name inside AnimationLibrary.
        /// </summary>
        public bool PlayClip(string clipName, float speed = 1f, float startTimeOffset = 0f)
        {
            if (_animationLibrary == null)
            {
                return false;
            }

            int idx = _animationLibrary.FindClipIndex(clipName);
            if (idx < 0)
            {
                return false;
            }

            PlayClip(idx, speed, startTimeOffset);
            return true;
        }

        /// <summary>
        /// Pauses GPU sprite animation at the specified clip time (seconds).
        /// </summary>
        public void PauseAnimation(float exactClipTimeSeconds)
        {
            _animSpeed = 0f;
            _animTimeOffset = Mathf.Max(0f, exactClipTimeSeconds);
            _hasInitializedPhase = true;
            MarkDirty();
        }

        /// <summary>
        /// Stops GPU animation and reverts to the static SpriteRenderer.sprite.
        /// </summary>
        public void StopAnimation()
        {
            _activeLocalClipIndex = -1;
            MarkDirty();
        }

        private void Awake()
        {
            _spriteRenderer = GetComponent<SpriteRenderer>();
            _nativeAnimator = GetComponent<Animator>();
            ResolveInitialClipIndex();
        }

        private void OnEnable()
        {
            if (!gameObject.scene.IsValid())
            {
                return;
            }
            _spriteRenderer = GetComponent<SpriteRenderer>();
            _nativeAnimator = GetComponent<Animator>();
            ResolveInitialClipIndex();
            RegisterOrFallback();
        }

        private void OnDisable()
        {
            UnregisterAndRestoreNative();
        }

        private void OnDestroy()
        {
            UnregisterAndRestoreNative();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            ResolveInitialClipIndex();
            if (!gameObject.scene.IsValid())
            {
                return;
            }
            // Defer actual manager/renderer sync out of OnValidate to avoid Unity SendMessage/CheckConsistency warnings
            _pendingValidateDirty = true;
        }

        private void LateUpdate()
        {
            if (!gameObject.scene.IsValid())
            {
                return;
            }

            if (Application.isPlaying)
            {
                if (_pendingValidateDirty)
                {
                    _pendingValidateDirty = false;
                    MarkDirty();
                }
                return;
            }

            SpriteRenderer sr = NativeSpriteRenderer;
            if (sr == null)
            {
                return;
            }

            Transform tr = transform;
            bool changed =
                _pendingValidateDirty ||
                sr.enabled != _lastRendererEnabled ||
                sr.sprite != _lastSprite ||
                sr.color != _lastColor ||
                sr.flipX != _lastFlipX ||
                sr.flipY != _lastFlipY ||
                sr.sortingLayerID != _lastSortingLayerId ||
                sr.sortingOrder != _lastSortingOrder ||
                sr.sharedMaterial != _lastMaterial ||
                sr.drawMode != _lastDrawMode ||
                sr.maskInteraction != _lastMaskInteraction ||
                tr.position != _lastPos ||
                tr.rotation != _lastRot ||
                tr.lossyScale != _lastScale;

            if (changed)
            {
                _pendingValidateDirty = false;
                MarkDirty();
            }
        }
#endif

        /// <summary>
        /// Call after modifying SpriteRenderer properties (sprite, color, flipX/Y, sortingOrder,
        /// material) or moving a Static sprite at runtime to synchronize the GPU buffer.
        /// </summary>
        public void MarkDirty()
        {
            if (!isActiveAndEnabled)
            {
                return;
            }

            if (CheckAutoFallback(out string reason))
            {
                if (_registeredSlotIndex >= 0)
                {
                    UnregisterAndRestoreNative();
                }
                EnterFallback(reason);
                return;
            }

            _isAutoFallback = false;
            _fallbackReason = null;

            SpriteBridgeManager mgr = SpriteBridgeManager.GetOrCreate();
            if (mgr == null || !mgr.IsSupportedAndReady)
            {
                EnterFallback("SpriteBridgeManager unavailable or hardware unsupported.");
                return;
            }

            if (_registeredSlotIndex < 0)
            {
                if (mgr.RegisterRenderer(this, out int slot, out int bucket))
                {
                    _registeredSlotIndex = slot;
                    _registeredBucketIndex = bucket;
                    ApplyNativeRendererVisibility(SpriteBridgeManager.IsNativeSceneViewDebugActive);
                }
                else
                {
                    string failReason = mgr.ActiveBucketCount >= SpriteBridgeManager.MaxBuckets
                        ? $"Max draw bucket limit ({SpriteBridgeManager.MaxBuckets}) reached. Pack sprites into a SpriteAtlas or set SortMode to GpuYSort / GpuOrderAndYSort."
                        : "Sprite/AnimationLibrary has no valid Texture.";
                    EnterFallback(failReason);
                }
            }
            else
            {
                mgr.UpdateRenderer(this);
                ApplyNativeRendererVisibility(SpriteBridgeManager.IsNativeSceneViewDebugActive);
            }

            CacheEditorSnapshot();
        }

        internal void SetRegisteredSlotAndBucket(int slotIndex, int bucketIndex)
        {
            _registeredSlotIndex = slotIndex;
            _registeredBucketIndex = bucketIndex;
        }

        internal void OnManagerReleased()
        {
            _registeredSlotIndex = -1;
            _registeredBucketIndex = -1;
            if (_spriteRenderer != null)
            {
                _spriteRenderer.forceRenderingOff = false;
            }
            if (_nativeAnimator != null && _disableNativeAnimator)
            {
                _nativeAnimator.enabled = true;
            }
        }

        internal void ApplyNativeRendererVisibility(bool nativeSceneViewDebugMode)
        {
            SpriteRenderer sr = NativeSpriteRenderer;
            if (sr == null)
            {
                return;
            }

            if (_isAutoFallback || _registeredSlotIndex < 0)
            {
                sr.forceRenderingOff = false;
                if (_nativeAnimator != null && _disableNativeAnimator)
                {
                    _nativeAnimator.enabled = true;
                }
                return;
            }

            // Use non-serialized Renderer.forceRenderingOff instead of Renderer.enabled so:
            // 1. Scene (.unity) and Prefab (.prefab) files are NEVER dirtied with m_Enabled: 0 in Edit Mode.
            // 2. User gameplay scripts can still toggle sr.enabled = false/true to hide/blink sprites!
            if (nativeSceneViewDebugMode)
            {
                sr.forceRenderingOff = false;
                if (_nativeAnimator != null && _disableNativeAnimator)
                {
                    _nativeAnimator.enabled = true;
                }
            }
            else
            {
                sr.forceRenderingOff = true;
                if (Application.isPlaying && _nativeAnimator != null && _disableNativeAnimator && _activeLocalClipIndex >= 0)
                {
                    _nativeAnimator.enabled = false;
                }
            }
        }

        internal bool TryBuildInstanceData(
            SpriteBridgeManager manager,
            out Texture2D outTexture,
            out Material outSourceMaterial,
            out SpriteBucketKey outBucketKey,
            out GpuSpriteInstance outInstance,
            out float outFlipSignX,
            out float outFlipSignY)
        {
            outTexture = null;
            outSourceMaterial = null;
            outBucketKey = default;
            outInstance = default;
            outFlipSignX = 1f;
            outFlipSignY = 1f;

            SpriteRenderer sr = NativeSpriteRenderer;
            if (sr == null)
            {
                return false;
            }

            Vector4 uvRect = new Vector4(0f, 0f, 1f, 1f);
            Vector4 pivotSize = new Vector4(-0.5f, -0.5f, 1f, 1f);
            uint packingRotBits = 0u;
            bool isTightPacked = false;

            uint globalAnimClip = GpuSpriteInstance.StaticAnimClipIndex;
            bool animLoop = false;

            if (_animationLibrary != null && _activeLocalClipIndex >= 0 && _activeLocalClipIndex < _animationLibrary.ClipCount)
            {
                globalAnimClip = manager.ResolveGlobalClipIndex(_animationLibrary, _activeLocalClipIndex);
                outTexture = _animationLibrary.AtlasTexture;
                _animationLibrary.TryGetFirstFrameGeometry(_activeLocalClipIndex, out uvRect, out pivotSize);
                if (_animationLibrary.TryGetClip(_activeLocalClipIndex, out SpriteBridgeAnimationLibrary.BakedClipMetadata meta))
                {
                    animLoop = meta.Loop;
                }
            }

            if (outTexture == null)
            {
                if (sr.sprite == null || !SpriteGeometryUtility.TryExtractSpriteGeometry(
                        sr.sprite,
                        out outTexture,
                        out uvRect,
                        out pivotSize,
                        out packingRotBits,
                        out isTightPacked))
                {
                    return false;
                }
            }

            _isTightPackedWarning = isTightPacked;
            outSourceMaterial = sr.sharedMaterial;

            int sortingLayerVal = SortingLayer.GetLayerValueFromID(sr.sortingLayerID);
            // Collapse SortingOrder into 1 bucket for None, GpuYSort, and GpuOrderAndYSort
            int orderBucket = (_sortMode == SpriteSortMode.BucketedOrder) ? sr.sortingOrder : 0;
            int matId = outSourceMaterial != null ? outSourceMaterial.GetInstanceID() : 0;
            int texId = outTexture != null ? outTexture.GetInstanceID() : 0;
            int goLayer = gameObject.layer;

            outBucketKey = new SpriteBucketKey(matId, texId, sortingLayerVal, orderBucket, _sortMode, goLayer);

            Transform tr = transform;
            Vector3 pos = tr.position;
            Quaternion rot = tr.rotation;
            Vector3 lossy = tr.lossyScale;

            outFlipSignX = sr.flipX ? -1f : 1f;
            outFlipSignY = sr.flipY ? -1f : 1f;

            Vector3 effectiveScale = new Vector3(
                lossy.x * outFlipSignX,
                lossy.y * outFlipSignY,
                lossy.z);

            // Respect user's SpriteRenderer.enabled state (allows gameplay scripts to hide/blink sprites via sr.enabled = false)
            GpuSpriteFlags flags = sr.enabled ? GpuSpriteFlags.Active : GpuSpriteFlags.None;
            if (_billboardMode == SpriteBillboardMode.FullBillboard)
            {
                flags |= GpuSpriteFlags.BillboardFull;
            }
            else if (_billboardMode == SpriteBillboardMode.YAxisBillboard)
            {
                flags |= GpuSpriteFlags.BillboardYAxis;
            }

            if (animLoop) flags |= GpuSpriteFlags.AnimLoop;
            if ((packingRotBits & 1u) != 0u) flags |= GpuSpriteFlags.PackingRotBit0;
            if ((packingRotBits & 2u) != 0u) flags |= GpuSpriteFlags.PackingRotBit1;
            if (_sortMode == SpriteSortMode.GpuOrderAndYSort) flags |= GpuSpriteFlags.CombineOrderWithYSort;

            SpriteGeometryUtility.PackColorHalf4(sr.color, out uint packedRG, out uint packedBA);

            if (!_hasInitializedPhase)
            {
                _hasInitializedPhase = true;
                float now = Application.isPlaying ? Time.time : (float)Time.realtimeSinceStartup;
                if (_randomizeStartPhase && _animationLibrary != null &&
                    _animationLibrary.TryGetClip(_activeLocalClipIndex, out SpriteBridgeAnimationLibrary.BakedClipMetadata clipMeta))
                {
                    uint h = (uint)GetInstanceID() * 2654435761u;
                    float fracPhase = (h & 0xFFFFu) / 65535f;
                    _animTimeOffset = fracPhase * clipMeta.Duration - now;
                }
                else
                {
                    _animTimeOffset = -now;
                }
            }

            outInstance = new GpuSpriteInstance
            {
                Position = pos,
                SortKey = sr.sortingOrder,
                Rotation = new Vector4(rot.x, rot.y, rot.z, rot.w),
                Scale = effectiveScale,
                PackedMeta = SpriteGeometryUtility.PackMeta(globalAnimClip, flags, 0u),
                UvRect = uvRect,
                PivotSize = pivotSize,
                PackedColorRG = packedRG,
                PackedColorBA = packedBA,
                AnimTimeOffset = _animTimeOffset,
                AnimSpeed = _animSpeed
            };

            return true;
        }

        private void ResolveInitialClipIndex()
        {
            if (_animationLibrary == null || _animationLibrary.ClipCount == 0)
            {
                _activeLocalClipIndex = -1;
                return;
            }

            if (!string.IsNullOrEmpty(_defaultClipName))
            {
                int found = _animationLibrary.FindClipIndex(_defaultClipName);
                if (found >= 0)
                {
                    _activeLocalClipIndex = found;
                    return;
                }
            }

            if (_defaultClipIndex >= 0 && _defaultClipIndex < _animationLibrary.ClipCount)
            {
                _activeLocalClipIndex = _defaultClipIndex;
            }
            else if (_activeLocalClipIndex >= _animationLibrary.ClipCount)
            {
                _activeLocalClipIndex = -1;
            }
        }

        private void RegisterOrFallback()
        {
            if (CheckAutoFallback(out string reason))
            {
                EnterFallback(reason);
                return;
            }

            SpriteBridgeManager mgr = SpriteBridgeManager.GetOrCreate();
            if (mgr == null || !mgr.IsSupportedAndReady)
            {
                EnterFallback("Compute Shaders / Vertex Compute Buffers unsupported or SpriteBridgeResources missing.");
                return;
            }

            if (mgr.RegisterRenderer(this, out int slot, out int bucket))
            {
                _isAutoFallback = false;
                _fallbackReason = null;
                _registeredSlotIndex = slot;
                _registeredBucketIndex = bucket;
                ApplyNativeRendererVisibility(SpriteBridgeManager.IsNativeSceneViewDebugActive);
            }
            else
            {
                string failReason = mgr.ActiveBucketCount >= SpriteBridgeManager.MaxBuckets
                    ? $"Max draw bucket limit ({SpriteBridgeManager.MaxBuckets}) reached. Pack sprites into a SpriteAtlas or set SortMode to GpuYSort / GpuOrderAndYSort."
                    : "SpriteRenderer has no valid Sprite/Texture assigned.";
                EnterFallback(failReason);
            }

            CacheEditorSnapshot();
        }

        internal void PollRuntimePropertiesIfNeeded()
        {
            SpriteRenderer sr = _spriteRenderer;
            if (sr == null || _registeredSlotIndex < 0)
            {
                return;
            }

            bool usesGpuAnim = _animationLibrary != null && _activeLocalClipIndex >= 0;
            bool changed =
                _pendingValidateDirty ||
                sr.enabled != _lastRendererEnabled ||
                sr.flipX != _lastFlipX ||
                sr.flipY != _lastFlipY ||
                sr.color != _lastColor ||
                (!usesGpuAnim && sr.sprite != _lastSprite) ||
                sr.sortingOrder != _lastSortingOrder ||
                sr.sortingLayerID != _lastSortingLayerId ||
                gameObject.layer != _lastLayer;

            if (changed)
            {
                _pendingValidateDirty = false;
                MarkDirty();
            }
        }

        private void EnterFallback(string reason)
        {
            _isAutoFallback = true;
            _fallbackReason = reason;
            _registeredSlotIndex = -1;
            _registeredBucketIndex = -1;
            if (NativeSpriteRenderer != null)
            {
                NativeSpriteRenderer.forceRenderingOff = false;
            }
            if (_nativeAnimator != null && _disableNativeAnimator)
            {
                _nativeAnimator.enabled = true;
            }
            CacheEditorSnapshot();
        }

        private void UnregisterAndRestoreNative()
        {
            SpriteBridgeManager mgr = SpriteBridgeManager.CurrentInstance;
            if (mgr != null && _registeredSlotIndex >= 0)
            {
                mgr.UnregisterRenderer(this);
            }

            _registeredSlotIndex = -1;
            _registeredBucketIndex = -1;

            if (NativeSpriteRenderer != null)
            {
                NativeSpriteRenderer.forceRenderingOff = false;
            }

            if (_nativeAnimator != null && _disableNativeAnimator)
            {
                _nativeAnimator.enabled = true;
            }
        }

        private bool CheckAutoFallback(out string reason)
        {
            if (!SpriteBridgeManager.IsHardwareSupported)
            {
                reason = "Hardware does not support Compute Shaders + Vertex Compute Buffers (GLES 3.1+ required).";
                return true;
            }

            SpriteRenderer sr = NativeSpriteRenderer;
            if (sr == null)
            {
                reason = "Missing SpriteRenderer component.";
                return true;
            }

            if (sr.drawMode != SpriteDrawMode.Simple)
            {
                reason = $"DrawMode.{sr.drawMode} (Sliced/Tiled) auto-falls back to native SpriteRenderer.";
                return true;
            }

            if (sr.maskInteraction != SpriteMaskInteraction.None)
            {
                reason = $"SpriteMaskInteraction.{sr.maskInteraction} auto-falls back to native SpriteRenderer.";
                return true;
            }

            bool hasValidAnimTex = _animationLibrary != null && _activeLocalClipIndex >= 0 && _animationLibrary.AtlasTexture != null;
            if (sr.sprite == null && !hasValidAnimTex)
            {
                reason = "No Sprite or AnimationLibrary assigned.";
                return true;
            }

            reason = null;
            return false;
        }

        private void CacheEditorSnapshot()
        {
            SpriteRenderer sr = NativeSpriteRenderer;
            if (sr == null)
            {
                return;
            }
            _lastRendererEnabled = sr.enabled;
            _lastSprite = sr.sprite;
            _lastColor = sr.color;
            _lastFlipX = sr.flipX;
            _lastFlipY = sr.flipY;
            _lastSortingLayerId = sr.sortingLayerID;
            _lastSortingOrder = sr.sortingOrder;
            _lastLayer = gameObject.layer;
#if UNITY_EDITOR
            Transform tr = transform;
            _lastMaterial = sr.sharedMaterial;
            _lastDrawMode = sr.drawMode;
            _lastMaskInteraction = sr.maskInteraction;
            _lastPos = tr.position;
            _lastRot = tr.rotation;
            _lastScale = tr.lossyScale;
#endif
        }
    }
}
