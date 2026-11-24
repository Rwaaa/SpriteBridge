# SpriteBridge (`com.spritebridge.core`)

**[English](README.md) | [한국어 (Korean)](README.ko.md)**

> **Zero-CPU GPU `SpriteRenderer`, 2D Frame Animation & GPU Bitonic `Y-Sort` Bridge for Unity (URP 14–17 2D/3D & Built-in RP)**

Unity 6 introduced the **GPU Resident Drawer (GRD)** to accelerate 3D `MeshRenderer`s via `BatchRendererGroup`, **but explicitly excludes `SpriteRenderer`**. Furthermore, standard URP 2D shaders (`Sprite-Lit-Default`, `Sprite-Unlit-Default`) use `CBUFFER_START(UnityPerMaterial)` for the SRP Batcher, preventing traditional `MaterialPropertyBlock` GPU instancing.

When 2D or 2.5D games (Vampire Survivors-like hordes, bullet-hell shooters, top-down action RPGs, colony sims, tower defense) spawn thousands of moving, animated, dynamically `Y-Sorted` sprites, two bottlenecks cripple frame rates:
1. **Dynamic Y-Sort Breaks Batching**: Updating `SpriteRenderer.sortingOrder = -(int)(y * 100)` causes interleaving draw order changes, shattering dynamic batching into **1 DrawCall per sprite** (`20,000 sprites = ~19,800 DrawCalls`).
2. **`Animator` & `Transform` CPU Overhead**: Running 20,000 `Animator` state machines and C++ `Transform` updates on the main thread consumes **20+ ms per frame**.

**`SpriteBridge`** solves both bottlenecks as a **zero-config drop-in package** supporting **URP 3D (`UniversalRenderer`)**, **URP 2D (`Renderer2D` + 2D Lights)**, and the **Built-in Render Pipeline (BiRP)**.

---

## 1. What Performance Gains Can You Expect? (Measured Benchmark)

### 1-1. Desktop Benchmark (Apple M4 Pro)
- **Hardware**: Apple M4 Pro (macOS Metal)
- **Engine**: Unity 6000.x (URP 17.3 RenderGraph, Linear Color Space, `vSyncCount = 0`)
- **Workload**: **20,000 Moving + 8-Frame Animated + Dynamically Y-Sorted 2D Units** (Single `SpriteAtlas`, 90-frame steady-state average after 45-frame / 3.0s warmup)

| Metric | Phase 1: Native `SpriteRenderer` (CPU Anim + CPU `sortingOrder` Y-Sort) | Phase 2: `SpriteBridge` Tier 1 (Drop-In `SpriteBridgeRenderer` + Burst `TransformAccessArray`) | Phase 3: `SpriteBridge` Tier 2 (Zero-GameObject Batch API + Burst Direct Write) | Improvement (Native $\to$ Tier 1 / Tier 2) |
| :--- | :---: | :---: | :---: | :---: |
| **Average FPS** | `44.61 FPS` | **`95.63 FPS`** | **`942.42 FPS`** | **`2.14x` / `21.13x` Faster** |
| **Average Frame Time** | `22.415 ms` | **`10.457 ms`** | **`1.061 ms`** | **`-53.3%` / `-95.3%` Frame Time** |
| **P95 / Max Frame Time** | `27.385` / `151.665 ms` | **`11.337` / `11.988 ms`** | **`1.139` / `1.414 ms`** | **`-92.1%` / `-99.1%` Peak Spike** |
| **>20ms Frame Spikes** | `22 / 90 frames` (`24.4%`) | **`0 / 90 frames` (`0%`)** | **`0 / 90 frames` (`0%`)** | **100% Spike Elimination** |
| **PlayerLoop CPU** | `19.431 ms` | **`8.638 ms`** | **`0.592 ms`** | **`-55.5%` / `-97.0%` CPU Cost** |
| **GPU Cull+Anim / Y-Sort** | `0.000 ms` | **`0.0254` / `0.0409 ms`** | **`< 0.03` / `< 0.04 ms`** | **`~0.066 ms` Total GPU Compute** |
| **Draw Calls** | `19,798.2` | **`7.0`** | **`7.0`** | **`-99.96%` (`19,798 -> 7`)** |
| **SetPass Calls** | `18.0` | **`7.0`** | **`7.0`** | **`-61.1%` Reduction** |

### 1-2. Mobile Device Benchmark (Samsung Galaxy Z Fold 2 — Android Vulkan)
- **Hardware**: Samsung Galaxy Z Fold 2 (`SM-F916N`, Qualcomm Snapdragon 865+, `Adreno 650` GPU, Android 13, `2658×960` @ 60Hz Hardware VSync)
- **Engine**: Unity 6000.x (IL2CPP ARM64, Vulkan, URP 17.3 RenderGraph)
- **Workload**: Same **20,000 Moving + 8-Frame Animated + Dynamically Y-Sorted 2D Units** (90-frame steady-state average)

| Metric | Phase 1: Native `SpriteRenderer` (CPU Anim + CPU `sortingOrder` Y-Sort) | Phase 2: `SpriteBridge` Tier 1 (Drop-In `SpriteBridgeRenderer` + GPU Anim/YSort) | Phase 3: `SpriteBridge` Tier 2 (Zero-GameObject Batch API + GPU Anim/YSort) | Mobile Improvement (Native $\to$ Tier 1 / Tier 2) |
| :--- | :---: | :---: | :---: | :---: |
| **Average FPS (60Hz Display)** | `25.0 FPS` | **`59.9 FPS` (60Hz Locked)** | **`60.6 FPS` (60Hz Locked)** | **`2.40x` (`25 FPS -> 60 FPS Cap`)** |
| **Average Frame Time** | `40.04 ms` | **`16.68 ms`** | **`16.50 ms`** | **`-58.8%` Frame Time** |
| **P95 / Max Frame Time** | `50.07` / `50.10 ms` | **`16.70` / `16.72 ms`** | **`33.35` / `33.40 ms`** | **Tier 1 `>20ms` Spikes `0 / 90f` (`0%`)** |
| **Pure CPU Work (`PlayerLoop - VSyncWait`)** | `40.11 ms` *(0.00ms VSync headroom)* | **`11.49 ms`** *(5.18ms VSync headroom)* | **`8.64 ms`** *(7.92ms VSync headroom)* | **`3.49x` / `4.64x` Faster CPU (`-71.4%` / `-78.5%`)** |
| **Unthrottled CPU Capacity FPS** | `24.9 FPS` | **`87.0 FPS`** | **`115.7 FPS`** | **`115+ FPS` Mobile CPU Headroom** |
| **Mobile GPU Compute (`Cull+Anim` / `Y-Sort`)** | `0.000 ms` | **`0.057` / `0.093 ms`** *(`0.150 ms` total)* | **`0.121` / `0.265 ms`** *(`0.386 ms` total)* | **20k Sprites Sorted on Adreno 650 in `0.093 ms`** |

### Why Two Tiers?
- **Tier 1 (`SpriteBridgeRenderer` Drop-In)**: Keep 100% of your existing `GameObject`s, prefabs, and `transform.position` scripts. Adding one component collapses `19,798` draw calls down to `7`, offloads 2D animation and dynamic Y-Sort (`0.041 ms` GPU Bitonic Sort) to the GPU, and eliminates all `>20ms` frame spikes. *(Note: The remaining `~8.6 ms` CPU time in Tier 1 is Unity's own C++ `Transform.position` setter cost across 20,000 `GameObject`s.)*
- **Tier 2 (Zero-GameObject Batch API)**: For projectiles, bullet-hell patterns, and massive survivor hordes that don't need individual `GameObject` components, `SpriteBridgeHandle` + `AcquireWritableInstanceBuffer()` removes `GameObject`/`Transform` overhead completely, reaching **`942.4 FPS` (`0.59 ms` CPU)** for 20,000 animated Y-sorted units.

---

## 2. Requirements & Pipeline Compatibility

| Category | Supported Specifications |
| :--- | :--- |
| **Unity Version** | **Unity 2022.3 LTS** or newer (including **Unity 6 / 6000.x** RenderGraph & Compatibility Mode) |
| **Render Pipelines** | • **URP 14.0 – 17.x 3D (`UniversalRenderer`)** (Forward, Forward+, 2.5D Lighting & 3D ShadowCaster)<br>• **URP 14.0 – 17.x 2D (`Renderer2D`)** (2D Lights, Normal Maps, ShadowCaster2D)<br>• **Built-in Render Pipeline (BiRP)** (Automatic fallback via `Camera.onPreCull` + Built-in SubShaders) |
| **Graphics APIs** | DirectX 11/12, Metal (macOS/iOS), Vulkan, OpenGL ES 3.1+ (Strictly $\le 2$ UAVs per Compute Kernel) |
| **External Dependencies** | **Zero mandatory dependencies.** If `com.unity.burst` (`>= 1.6.0`) is installed, `SpriteBridge.Burst.asmdef` automatically enables Burst SIMD `IJobParallelForTransform` acceleration via `versionDefines`. |

---

## 3. Installation

### Option A: Install via Package Manager (`manifest.json` / Git URL)
Add `com.spritebridge.core` via **Window → Package Manager → + → Add package from git URL...**:
```text
https://github.com/Rwaaa/SpriteBridge.git
```
Or add it directly to your project's `Packages/manifest.json`:
```json
{
  "dependencies": {
    "com.spritebridge.core": "https://github.com/Rwaaa/SpriteBridge.git"
  }
}
```

### Option B: Direct `Assets/SpriteBridge` Folder
Copy the `Assets/SpriteBridge` directory directly into your Unity project's `Assets/` folder. `SpriteBridgeResourcesAutoSetup` will automatically configure `SpriteBridgeResources.asset` on Editor load.

---

## 4. How to Use (Step-by-Step Guide & Code Examples)

### 4-1. 1-Click Scene Conversion & 1-Click Revert (Fastest Setup)
- **Convert Scene**: Click **`Tools → SpriteBridge → Convert All Scene SpriteRenderers to SpriteBridge`**.
  - Automatically attaches `SpriteBridgeRenderer` to all valid `SpriteRenderer`s in the scene.
  - Automatically sets `MotionMode = Static` for static objects (`gameObject.isStatic`) and `DynamicTransform` for moving objects.
  - Automatically skips `Sliced`/`Tiled` 9-slice sprites and `SpriteMask` objects so UI-like sprites remain 100% native.
- **Revert Scene**: Click **`Tools → SpriteBridge → Revert All Scene SpriteBridgeRenderers to Native`** (Full Undo support, restores all native `SpriteRenderer`s cleanly).

---

### 4-2. Tier 1: Drop-In `SpriteBridgeRenderer` Component
Attach **`SpriteBridgeRenderer`** (`Add Component → SpriteBridge → Sprite Bridge Renderer`) to any `GameObject` with a `SpriteRenderer`:

- **`Motion Mode`**:
  - `Static`: Uploaded once on registration (`0.000 ms/frame` CPU). Ideal for trees, props, rocks, buildings, and non-moving decorations. Call `bridge.MarkDirty()` if moved manually.
  - `DynamicTransform`: Registered into a dense `TransformAccessArray` and synced in parallel via Burst `IJobParallelForTransform`.
- **`Auto Sync Renderer Properties`**:
  - When enabled (`true` by default), your existing gameplay scripts can modify `spriteRenderer.flipX`, `spriteRenderer.flipY`, `spriteRenderer.color` (e.g., hit flash), `spriteRenderer.sprite`, `spriteRenderer.enabled` (e.g., invincibility blinking), or `spriteRenderer.sortingOrder` at runtime **without modifying a single line of your existing code**.
- **`Sort Mode`**:
  - `None`: Fastest; collapses all sorting orders into 1 DrawCall per `(Material, AtlasTexture, Layer)`. Ideal for Alpha-Cutout (`ZWrite On`) or Additive sprites.
  - `BucketedOrder` *(Default)*: Zero GPU sort passes; groups sprites by `(SortingLayer, SortingOrder)` and assigns deterministic `renderQueue` offsets.
  - `GpuYSort`: Collapses all sprites sharing `(Material, AtlasTexture, SortingLayer)` into **1 single DrawCall** and sorts them back-to-front every frame by `-worldPos.y` (28-bit precision) using a GPU Bitonic Sort.
  - `GpuOrderAndYSort`: Combines `SortingOrder` (top 10 bits) + `-worldPos.y` (bottom 18 bits) in 1 GPU-sorted DrawCall.
- **`Billboard Mode`**:
  - `None` / `FullBillboard` / `YAxisBillboard` (Evaluated directly inside the Compute Shader — ideal for 2.5D / HD-2D games with rotating 3D cameras).

---

### 4-3. GPU 2D Animation Baker (`0.000 ms` Animator CPU)

#### Baking From Unity `AnimationClip` Assets (Editor)
1. Select one or more 2D `AnimationClip` assets (that animate `SpriteRenderer.m_Sprite`) in the Project window.
2. Click **`Tools → SpriteBridge → Bake Selected AnimationClips to Library`** and save the `.asset`.
3. Assign the generated `SpriteBridgeAnimationLibrary` to `SpriteBridgeRenderer.AnimationLibrary`.
4. During Play Mode, `SpriteBridgeRenderer` automatically sets `Animator.enabled = false` (`0.000 ms` CPU) and plays the animation 100% on the GPU while preserving per-frame trimmed pivots and sizes.

#### Controlling GPU Animation from C#
```csharp
using SpriteBridge;
using UnityEngine;

public class MonsterAnimationController : MonoBehaviour
{
    [SerializeField] private SpriteBridgeRenderer _spriteBridge;

    public void PlayWalk()
    {
        // Play by clip name (or integer clip index) with custom speed
        _spriteBridge.PlayClip("Walk", speed: 1.2f);
    }

    public void PlayAttack()
    {
        _spriteBridge.PlayClip("Attack", speed: 1.0f, startTimeOffset: 0f);
    }

    public void FreezeAtFrameTime(float seconds)
    {
        _spriteBridge.PauseAnimation(seconds);
    }
}
```

#### Baking Procedural Sprite Arrays at Runtime
```csharp
SpriteBridgeAnimationLibrary runtimeLib = ScriptableObject.CreateInstance<SpriteBridgeAnimationLibrary>();
int walkClipIdx = runtimeLib.AddClipFromSprites("Walk", myWalkSpritesArray, framesPerSecond: 12f, loop: true, out string warning);
```

---

### 4-4. Tier 2: Zero-GameObject High-Density Batch API (20,000–100,000+ Sprites)
For bullet-hell projectiles, survivor enemy swarms, or dense foliage where creating 50,000 `GameObject`s is unnecessary:

```csharp
using SpriteBridge;
using Unity.Collections;
using UnityEngine;

public class BulletHellSpawner : MonoBehaviour
{
    [SerializeField] private Texture2D _atlasTexture;
    [SerializeField] private SpriteBridgeAnimationLibrary _animLib;
    [SerializeField] private int _count = 20000;

    private int _bucketId;
    private SpriteBridgeHandle[] _handles;

    private void Start()
    {
        SpriteBridgeManager mgr = SpriteBridgeManager.GetOrCreate();

        // 1. Register a draw bucket (all 20,000 sprites share 1 Indirect DrawCall!)
        _bucketId = mgr.RegisterBatchBucket(
            sourceMaterial: null, // Uses default SpriteBridgeUnlit shader
            atlasTexture: _atlasTexture,
            sortMode: SpriteSortMode.GpuYSort);

        // 2. Spawn 20,000 Zero-GameObject animated sprites (O(1) generational handles)
        _handles = new SpriteBridgeHandle[_count];
        for (int i = 0; i < _count; i++)
        {
            Vector3 pos = new Vector3(Random.Range(-20f, 20f), Random.Range(-20f, 20f), 0f);
            _handles[i] = mgr.SpawnAnimatedSprite(
                _bucketId,
                _animLib,
                localClipIndex: 0,
                position: pos,
                rotation: Quaternion.identity,
                scale: Vector3.one * 0.5f,
                color: Color.white,
                animSpeed: 1.0f,
                startTimeOffset: Random.value);
        }
    }

    private void Update()
    {
        SpriteBridgeManager mgr = SpriteBridgeManager.CurrentInstance;
        if (mgr == null) return;

        // 3. Direct zero-copy NativeArray write (can also be scheduled inside a [BurstCompile] IJobParallelFor!)
        NativeArray<GpuSpriteInstance> buffer = mgr.AcquireWritableInstanceBuffer(out int activeCount);
        float dt = Time.deltaTime;

        for (int i = 0; i < _handles.Length; i++)
        {
            if (mgr.TryGetSlotIndex(_handles[i], out int slot) && slot < activeCount)
            {
                GpuSpriteInstance inst = buffer[slot];
                inst.Position += new Vector3(0f, -2.5f * dt, 0f);
                buffer[slot] = inst;
            }
        }
    }

    public void DespawnBullet(int index)
    {
        // O(1) Swap-and-Pop removal; all other SpriteBridgeHandles remain 100% valid!
        SpriteBridgeManager.CurrentInstance?.DespawnSprite(_handles[index]);
    }
}
```

---

### 4-5. Built-In Shaders & Custom Shader / Shader Graph Integration

#### Included Shaders
1. **`SpriteBridge/URP/Sprite-Unlit`**: Works in **URP 3D**, **URP 2D**, and **Built-in RP**. Supports AlphaBlend, Alpha Cutout (`_AlphaClip`), Additive, and `_ZWrite`.
2. **`SpriteBridge/URP 2D/Sprite-Lit`**: Full **URP 2D `Renderer2D`** shader (`Universal2D` + `NormalsRendering` passes) supporting 2D Lights (Point/Spot/Freeform/Global), 2D Normal Maps, and 2D Shadows, with automatic URP 3D and Built-in RP fallback passes.
3. **`SpriteBridge/URP 3D/Sprite-Lit-2.5D`**: Designed for **HD-2D / 2.5D games** in URP 3D and Built-in RP. Receives 3D Main & Additional Lights, Normal Maps, Spherical Harmonics ambient, and includes an **alpha-clipped `ShadowCaster` pass** so 2D sprite silhouettes cast real-time 3D shadows onto the environment.

#### Custom HLSL Shader Patching
- **1-Click Auto-Patcher**: Open **`Tools → SpriteBridge → Custom Shader Auto-Patcher...`**, select any custom `.shader`, and click **Generate Patched `_SpriteBridge.shader` Copy**.
- **Shader Graph**: Add a **Custom Function** node (`Type = File`, `Source = Assets/SpriteBridge/Shaders/SpriteBridgeShaderGraphNode.hlsl`, `Name = SpriteBridgeVertex`):
  - **Inputs**: `InstanceID (Float)` (connect Shader Graph `Instance ID` node), `QuadUV (Vector2)` (connect `UV0`)
  - **Outputs**: `PositionWS (Vector3)`, `SpriteUV (Vector2)`, `Color (Vector4)`, `NormalWS (Vector3)`, `TangentWS (Vector3)`

---

## 5. 3-Tier Play Mode Scene View Debug Mode

Switch modes anytime via **`Tools → SpriteBridge → Play Mode Scene View`** or from any `SpriteBridgeRenderer` Inspector:

| Mode | Behavior | Use Case |
| :--- | :--- | :--- |
| **`Off` *(Default)*** | Skips Scene View GPU dispatch during Play Mode. **Automatically forced `Off` in all Player Builds** via `IPreprocessBuildWithReport` and `#if !UNITY_EDITOR`. | Maximum benchmark & gameplay FPS |
| **`GpuSprites`** | Renders GPU sprites in both Game View and Scene View during Play Mode, and enables **`SpriteBridgeSceneViewPicker`** (click any GPU sprite in Scene View to select its `GameObject` and inspect its wireframe quad & slot/bucket HUD). | Level debugging & individual sprite selection |
| **`NativeSpriteRenderer`** | Uses per-camera `Renderer.forceRenderingOff` toggling so **Scene View renders 100% pure native Unity `SpriteRenderer` + `Animator`** while **Game View renders GPU `SpriteBridge` simultaneously**. | Instant 1:1 visual parity verification (Pivot, FlipX/Y, Color, Sorting, Animation) |

---

## 6. Supported vs. Auto-Fallback Matrix & Cautions

### Automatic Safe Fallback (Zero Visual Breakage)
`SpriteBridgeRenderer` never silently corrupts unsupported sprite features. It automatically detects and leaves the following on native `SpriteRenderer` (displaying the exact reason in the Inspector):

| Feature | Status | Handling |
| :--- | :---: | :--- |
| `SpriteDrawMode.Simple` (Untrimmed, Trimmed, `SpriteAtlas` FullRect / Rectangle) | **GPU Accelerated** | 100% 1:1 parity via `DataUtility.GetOuterUV` & `sprite.bounds` |
| `FlipX` / `FlipY` (Including Off-Center Custom Pivots & Negative Scale) | **GPU Accelerated** | Exact pivot-relative mirror & normal handedness flip |
| Linear / Gamma Color Space & HDR Bloom Tint | **GPU Accelerated** | Lossless Linear `half4` (`uint2`) packing |
| Multi-Camera `Camera.cullingMask` & `gameObject.layer` | **GPU Accelerated** | Per-layer bucket isolation (UI/Minimap camera safe) |
| Non-Serialized `Renderer.forceRenderingOff` Suppression | **Built-In** | Never modifies `SpriteRenderer.enabled` in Edit Mode (0 dirty `.unity`/`.prefab` diffs in Git) |
| `SpriteDrawMode.Sliced` / `SpriteDrawMode.Tiled` (9-Slice) | **Auto-Fallback** | Automatically left on native `SpriteRenderer` |
| `SpriteMaskInteraction != None` (Stencil Masking) | **Auto-Fallback** | Automatically left on native `SpriteRenderer` |
| Hardware without Vertex Compute Buffers (`maxComputeBufferInputsVertex <= 0`) | **Auto-Fallback** | Automatically left on native `SpriteRenderer` |

### Important Cautions & Best Practices
1. **Use `SpriteAtlas` for Best Batching**:
   - `SpriteBridge` groups draw calls by `(Material, Texture2D, Layer, SortingBucket)`. If 500 different sprites are packed into **1 `SpriteAtlas`**, they share the same `Texture2D` and collapse into **1 single Indirect DrawCall**.
   - In `SpriteAtlas` Inspector settings, **uncheck `Tight Packing`** (use Rectangle packing) so adjacent atlas sprites never bleed into quad corners.
2. **When to Use `Static` vs `DynamicTransform`**:
   - Use `MotionMode = Static` for non-moving background props (`0.000 ms` CPU). If you move a `Static` sprite at runtime, call `spriteBridgeRenderer.MarkDirty()`.
   - For thousands of moving objects, install **`com.unity.burst`** in Package Manager so `SpriteBridge.Burst` automatically compiles `IJobParallelForTransform` with SIMD multi-threading.
3. **Bucket Limit (`MaxBuckets = 256`)**:
   - If you use `SortMode = BucketedOrder` with hundreds of unique `SortingOrder` values across un-atlased textures, switch `SortMode` to **`GpuYSort`** or **`GpuOrderAndYSort`**, which collapses all sorting orders on an atlas into **1 single GPU-sorted bucket**.

---

## License
MIT License. See [LICENSE.md](LICENSE.md).
