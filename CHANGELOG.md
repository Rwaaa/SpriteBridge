# Changelog

## [1.0.0] - 2026-11-22
### Added
- **Core Pipeline**: Drop-in `SpriteBridgeRenderer` hijacking Unity `SpriteRenderer` onto GPU Compute + `Graphics.RenderMeshIndirect` across URP 14–17 (Unity 2022.3 LTS through Unity 6 RenderGraph & Compatibility Mode).
- **Optional Burst Acceleration**: Separate `SpriteBridge.Burst.asmdef` using `versionDefines` + `defineConstraints` to automatically enable Burst-compiled `IJobParallelForTransform` when `com.unity.burst` is present while keeping zero mandatory external dependencies.
- **GPU 2D Animation Baker**: `SpriteBridgeAnimationLibrary` and `SpriteBridgeAnimationLibraryEditor` baking `AnimationClip` `m_Sprite` tracks into GPU LUT buffers (`0.000 ms` Animator CPU).
- **GPU Bitonic Dynamic Y-Sort**: `CS_BuildSortEntries`, `CS_BitonicSortLocal`, `CS_BitonicSortGlobalStep`, and `CS_BitonicSortLocalMerge` kernels (strictly $\le 2$ UAVs/kernel, GLES 3.1 uniform barrier compliant) for `GpuYSort` and `GpuOrderAndYSort`.
- **Tier 2 Zero-GameObject Batch API**: Generational `SpriteBridgeHandle` ($O(1)$ Swap-and-Pop) and `AcquireWritableInstanceBuffer()` for high-density bullet-hell and horde workloads.
- **URP 2D & 3D Shaders**: `SpriteBridgeUnlit.shader`, `SpriteBridgeLit2D.shader` (2D Lights + Normal Maps), `SpriteBridgeLit3D.shader` (2.5D 3D Lights + Alpha-Clipped `ShadowCaster` + `DepthOnly`), `SpriteBridgeShaderGraphNode.hlsl`, and `SpriteBridgeShaderPatcherWindow`.
- **3-Tier Play Mode Scene View Debug Mode**: `Off` / `GpuSprites` (with `SpriteBridgeSceneViewPicker`) / `NativeSpriteRenderer` (via `Renderer.forceRenderingOff`) with 2-layer Player Build forced-`Off` guard.
- **Built-in Render Pipeline (BiRP) Compatibility**: Automatic `Camera.onPreCull` / `Camera.onPostRender` compute & indirect draw fallback when `GraphicsSettings.currentRenderPipeline == null`, plus Built-in RP SubShaders in `SpriteBridgeUnlit.shader`, `SpriteBridgeLit2D.shader`, and `SpriteBridgeLit3D.shader` (ForwardBase + ShadowCaster).
- **Release Hardening**: Non-serialized `Renderer.forceRenderingOff` suppression (0 dirty scene/prefab diffs in Edit Mode), `DataUtility.GetOuterUV` + `sprite.bounds` `SpriteAtlas` parity, per-layer `Camera.cullingMask` bucket isolation, Zero-GC `Camera.GetAllCameras`, Manager `OnDisable/OnEnable` auto-recovery, and 1-Click Scene Convert/Revert tools.
