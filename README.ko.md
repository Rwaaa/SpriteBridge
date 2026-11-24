# SpriteBridge (`com.spritebridge.core`)

**[English](README.md) | [한국어 (Korean)](README.ko.md)**

> **Unity URP (14–17 2D/3D) 및 Built-in RP를 위한 Zero-CPU GPU `SpriteRenderer`, 2D 프레임 애니메이션 및 GPU Bitonic `Y-Sort` 브릿지**

Unity 6에 도입된 **GPU Resident Drawer (GRD)**는 `BatchRendererGroup`을 통해 3D `MeshRenderer`만 가속하며, **`SpriteRenderer`는 지원 대상에서 완전히 제외**되어 있습니다. 또한 URP 기본 2D 셰이더(`Sprite-Lit-Default`, `Sprite-Unlit-Default`)는 SRP Batcher 호환을 위해 `CBUFFER_START(UnityPerMaterial)` 구조를 사용하므로 기존 `MaterialPropertyBlock` 방식의 GPU 인스턴싱이 불가능합니다.

뱀파이어 서바이버류(탄막/호드), 탑다운 액션 RPG, 2D 타워디펜스, 콜로니 시뮬레이션, 2.5D(HD-2D) 게임에서 수천~수만 개의 이동·애니메이션·동적 `Y-Sort` 스프라이트가 등장하면 두 가지 치명적인 병목이 발생합니다.
1. **동적 Y-Sort로 인한 배칭 붕괴**: 매 프레임 `sortingOrder = -(int)(y * 100)`을 갱신하면 스프라이트 깊이 순서가 교차할 때마다 동적 배칭이 깨져 **스프라이트 1개당 1 DrawCall**(`20,000개 = 약 19,800 DrawCalls`)로 폭증합니다.
2. **`Animator` 및 `Transform` 메인 스레드 병목**: 20,000개의 `Animator` 상태 머신과 C++ `Transform` 갱신이 메인 스레드에서 매 프레임 **20 ms 이상**을 소모합니다.

**`SpriteBridge`**는 기존 `SpriteRenderer` 인스펙터와 프리팹 워크플로우를 100% 보존하면서, 컴포넌트 부착 한 번으로 **URP 3D(`UniversalRenderer`)**, **URP 2D(`Renderer2D` + 2D Lights)**, **Built-in Render Pipeline(BiRP)** 모두에서 GPU Compute 컬링, GPU 2D 애니메이션, GPU Bitonic 동적 `Y-Sort`를 수행하는 독립 패키지입니다.

---

## 1. 어떤 성능 효과를 얻을 수 있는가? (20,000 유닛 실측 벤치마크)

### 1-1. 데스크톱 실측 벤치마크 (Apple M4 Pro)
- **하드웨어**: Apple M4 Pro (macOS Metal)
- **엔진 환경**: Unity 6000.x (URP 17.3 RenderGraph, Linear Color Space, `vSyncCount = 0`)
- **워크로드**: **20,000개 실시간 이동 + 8프레임 스프라이트 애니메이션 + 동적 Y-Sort 유닛** (`SpriteAtlas` 1장, 각 Phase 45프레임 & 3.0초 Warmup 후 90프레임 평균)

| 측정 지표 | Phase 1: 순정 `SpriteRenderer` (CPU 애니메이션 + CPU `sortingOrder` Y-Sort) | Phase 2: `SpriteBridge` Tier 1 (드롭인 `SpriteBridgeRenderer` + Burst `TransformAccessArray`) | Phase 3: `SpriteBridge` Tier 2 (Zero-GameObject Batch API + Burst 직결) | 개선 효과 (Native $\to$ Tier 1 / Tier 2) |
| :--- | :---: | :---: | :---: | :---: |
| **평균 FPS** | `44.61 FPS` | **`95.63 FPS`** | **`942.42 FPS`** | **`2.14배` / `21.13배` 향상** |
| **평균 Frame Time** | `22.415 ms` | **`10.457 ms`** | **`1.061 ms`** | **`-53.3%` / `-95.3%` 단축** |
| **P95 / 최대 Frame Time** | `27.385` / `151.665 ms` | **`11.337` / `11.988 ms`** | **`1.139` / `1.414 ms`** | **최대 스파이크 `-92.1%` / `-99.1%`** |
| **>20ms 프레임 스파이크** | `22 / 90 frames` (`24.4%`) | **`0 / 90 frames` (`0%`)** | **`0 / 90 frames` (`0%`)** | **프레임 스파이크 100% 제거** |
| **PlayerLoop CPU** | `19.431 ms` | **`8.638 ms`** | **`0.592 ms`** | **`-55.5%` / `-97.0%` 절감** |
| **GPU Cull+Anim / Y-Sort** | `0.000 ms` | **`0.0254` / `0.0409 ms`** | **`< 0.03` / `< 0.04 ms`** | **2만 개 GPU 정렬+컬링 총 `~0.066 ms`** |
| **Draw Calls** | `19,798.2` | **`7.0`** | **`7.0`** | **`-99.96%` 감소 (`19,798 -> 7`)** |
| **SetPass Calls** | `18.0` | **`7.0`** | **`7.0`** | **`-61.1%` 감소** |

### 1-2. 모바일 실기기 벤치마크 (Samsung Galaxy Z Fold 2 — Android Vulkan)
- **하드웨어**: Samsung Galaxy Z Fold 2 (`SM-F916N`, Qualcomm Snapdragon 865+, `Adreno 650` GPU, Android 13, `2658×960` @ 60Hz 하드웨어 VSync)
- **엔진 환경**: Unity 6000.x (IL2CPP ARM64, Vulkan, URP 17.3 RenderGraph)
- **워크로드**: 동일한 **20,000개 실시간 이동 + 8프레임 스프라이트 애니메이션 + 동적 Y-Sort 유닛** (90프레임 평균)

| 측정 지표 | Phase 1: 순정 `SpriteRenderer` (CPU 애니메이션 + CPU `sortingOrder` Y-Sort) | Phase 2: `SpriteBridge` Tier 1 (드롭인 `SpriteBridgeRenderer` + GPU Anim/YSort) | Phase 3: `SpriteBridge` Tier 2 (Zero-GameObject Batch API + GPU Anim/YSort) | 모바일 실기기 개선 효과 (Native $\to$ Tier 1 / Tier 2) |
| :--- | :---: | :---: | :---: | :---: |
| **평균 FPS (60Hz 디스플레이)** | `25.0 FPS` | **`59.9 FPS` (60Hz 풀프레임)** | **`60.6 FPS` (60Hz 풀프레임)** | **`2.40배` (`25 FPS -> 60 FPS 고정`)** |
| **평균 Frame Time** | `40.04 ms` | **`16.68 ms`** | **`16.50 ms`** | **`-58.8%` 단축** |
| **P95 / 최대 Frame Time** | `50.07` / `50.10 ms` | **`16.70` / `16.72 ms`** | **`33.35` / `33.40 ms`** | **Tier 1 `>20ms` 스파이크 `0 / 90f` (`0%`)** |
| **순수 CPU 연산 시간 (`PlayerLoop - VSyncWait`)** | `40.11 ms` *(VSync 여유 0.00ms)* | **`11.49 ms`** *(VSync 여유 5.18ms)* | **`8.64 ms`** *(VSync 여유 7.92ms)* | **`3.49배` / `4.64배` CPU 가속 (`-71.4%` / `-78.5%`)** |
| **VSync 해제 시 이론상 CPU 한계 FPS** | `24.9 FPS` | **`87.0 FPS`** | **`115.7 FPS`** | **모바일 CPU에서도 `115+ FPS` 여유 확보** |
| **모바일 GPU Compute (`Cull+Anim` / `Y-Sort`)** | `0.000 ms` | **`0.057` / `0.093 ms`** *(총 `0.150 ms`)* | **`0.121` / `0.265 ms`** *(총 `0.386 ms`)* | **Adreno 650에서 2만 개 GPU Y-Sort `0.093 ms`** |

### Tier 1 vs Tier 2 선택 기준
- **Tier 1 (`SpriteBridgeRenderer` 드롭인)**: 기존 `GameObject`, 프리팹, `transform.position` 이동 스크립트를 한 줄도 바꾸지 않고 컴포넌트만 부착합니다. DrawCall이 `19,798 -> 7`로 압축되고 GPU Bitonic Sort(`0.041 ms`)가 동적 Y-Sort를 처리하여 `>20ms` 스파이크가 0건이 됩니다. *(참고: Tier 1에서 남은 `PlayerLoop 8.64 ms`는 렌더링 비용이 아니라 C#에서 20,000개 `GameObject`의 `transform.position`을 호출할 때 발생하는 Unity C++ `Transform` 더티 마킹 비용입니다.)*
- **Tier 2 (Zero-GameObject Batch API)**: 탄막, 투사체, 대규모 서바이버 몬스터 군집처럼 개별 `GameObject` 래퍼가 불필요한 객체들을 `SpriteBridgeHandle` + `AcquireWritableInstanceBuffer()`로 구동하면 C++ `Transform` 오버헤드까지 완전히 제거되어 **`PlayerLoop 0.592 ms` (`942.42 FPS`)**에 도달합니다.

---

## 2. 지원 환경 및 파이프라인 호환성

| 구분 | 상세 지원 스펙 |
| :--- | :--- |
| **Unity 버전** | **Unity 2022.3 LTS** 이상 (**Unity 6 / 6000.x** RenderGraph 및 Compatibility Mode 완벽 지원) |
| **렌더 파이프라인** | • **URP 14.0 – 17.x 3D (`UniversalRenderer`)** (Forward, Forward+, 2.5D 조명 및 실시간 3D 그림자 투사)<br>• **URP 14.0 – 17.x 2D (`Renderer2D`)** (2D Lights, Normal Map, ShadowCaster2D)<br>• **Built-in Render Pipeline (BiRP)** (`Camera.onPreCull` + 내장 Built-in SubShader 자동 폴백) |
| **그래픽스 API** | DirectX 11/12, Metal (macOS/iOS), Vulkan, OpenGL ES 3.1+ (모든 Compute 커널 UAV $\le 2$ 및 GLES3 균일 배리어 준수) |
| **외부 패키지 의존성** | **필수 외부 의존성 0개.** 프로젝트에 `com.unity.burst` (`>= 1.6.0`)가 설치되어 있으면 `SpriteBridge.Burst.asmdef`(`versionDefines`)가 자동으로 활성화되어 Burst SIMD 병렬 `IJobParallelForTransform`으로 업그레이드됩니다. |

---

## 3. 설치 방법

### 방법 A: Unity Package Manager (`manifest.json` / Git URL)
유니티 에디터 상단 메뉴 **Window → Package Manager → + → Add package from git URL...**에 아래 주소를 입력합니다:
```text
https://github.com/Rwaaa/SpriteBridge.git
```
또는 프로젝트의 `Packages/manifest.json`에 아래 항목을 직접 추가합니다:
```json
{
  "dependencies": {
    "com.spritebridge.core": "https://github.com/Rwaaa/SpriteBridge.git"
  }
}
```

### 방법 B: `Assets/SpriteBridge` 폴더 직접 복사
프로젝트의 `Assets/` 하위에 `Assets/SpriteBridge` 폴더를 복사하면 에디터 로드 시 `SpriteBridgeResourcesAutoSetup`이 자동으로 `SpriteBridgeResources.asset`을 생성·연결합니다.

---

## 4. 사용 방법 및 코드 예시

### 4-1. 1-Click 씬 일괄 적용 및 1-Click 원상복구
- **씬 일괄 변환**: 상단 메뉴 **`Tools → SpriteBridge → Convert All Scene SpriteRenderers to SpriteBridge`** 클릭
  - 씬 내 모든 유효한 `SpriteRenderer`에 `SpriteBridgeRenderer`를 부착합니다 (`isStatic` 여부에 따라 `Static` / `DynamicTransform` 자동 지정).
  - `Sliced`/`Tiled` 9-슬라이스 스프라이트 및 `SpriteMask` 사용 오브젝트는 자동으로 스킵하여 100% 순정 상태를 보존합니다.
- **씬 일괄 원상복구**: **`Tools → SpriteBridge → Revert All Scene SpriteBridgeRenderers to Native`** 클릭 (Full Undo 지원, 모든 컴포넌트를 제거하고 순정 `SpriteRenderer`로 즉시 복원).

---

### 4-2. Tier 1: 드롭인 `SpriteBridgeRenderer` 컴포넌트 설정
기존 `SpriteRenderer`가 있는 오브젝트에 **`SpriteBridgeRenderer`** (`Add Component → SpriteBridge → Sprite Bridge Renderer`)를 추가합니다.

- **`Motion Mode`**:
  - `Static`: 등록 시 1회만 GPU에 업로드(`0.000 ms/frame` CPU). 배경 프롭, 나무, 바위, 건물 등 정적 오브젝트에 사용합니다 (런타임에 위치를 옮겼다면 `bridge.MarkDirty()` 호출).
  - `DynamicTransform`: `TransformAccessArray`에 등록되어 매 프레임 Burst `IJobParallelForTransform` 워커 스레드 병렬 잡으로 월드 위치·회전·스케일을 동기화합니다.
- **`Auto Sync Renderer Properties`**:
  - 기본 활성화(`true`). 기존 게임 스크립트를 수정하지 않고 `spriteRenderer.flipX = true`, `spriteRenderer.color = Color.red`(피격 점멸), `spriteRenderer.sprite = newSprite`, `spriteRenderer.enabled = false`(무적 깜빡임/숨김), `spriteRenderer.sortingOrder`를 런타임에 변경해도 자동 감지하여 GPU 버퍼에 즉시 반영합니다.
- **`Sort Mode`**:
  - `None`: 정렬 패스 0회. `(Material, AtlasTexture, Layer)`당 단일 DrawCall로 통합합니다 (`AlphaClip + ZWrite` 컷아웃이나 Additive 이펙트에 최적).
  - `BucketedOrder` *(기본값)*: GPU 정렬 패스 없이 `(SortingLayer, SortingOrder)`별 드로우 버킷과 결정론적 `renderQueue` 오프셋으로 순서를 보장합니다.
  - `GpuYSort`: 동일 아틀라스/레이어의 모든 스프라이트를 **단 1회의 DrawCall**로 묶고, 매 프레임 `-worldPos.y`(28비트 정밀도) 기준으로 GPU Bitonic Sort를 수행해 반투명 캐릭터들의 전후 관계를 정렬합니다.
  - `GpuOrderAndYSort`: 상위 10비트에 `SortingOrder`, 하위 18비트에 `-worldPos.y`를 결합하여 단일 DrawCall 내에서 레이어 순서와 Y-Sort를 동시에 처리합니다.
- **`Billboard Mode`**:
  - `None` / `FullBillboard` / `YAxisBillboard` (Compute Shader에서 카메라 기저 벡터로 직접 계산 — 3D 카메라가 회전하는 2.5D / HD-2D 게임에 최적).

---

### 4-3. GPU 2D 애니메이션 베이커 (`0.000 ms` Animator CPU)

#### 에디터에서 Unity `AnimationClip` 원클릭 베이크
1. Project 창에서 `SpriteRenderer.m_Sprite`를 키프레임 애니메이션하는 `AnimationClip` 에셋들을 선택합니다.
2. 상단 메뉴 **`Tools → SpriteBridge → Bake Selected AnimationClips to Library`**를 클릭해 `.asset` 파일로 저장합니다.
3. 생성된 `SpriteBridgeAnimationLibrary`를 `SpriteBridgeRenderer.AnimationLibrary` 슬롯에 할당합니다.
4. Play Mode 진입 시 자동으로 `Animator.enabled = false`(`0.000 ms` CPU)로 전환되며, 프레임마다 Trimmed 여백과 피벗이 달라도 흔들림 없이 순정과 1:1 동일하게 GPU에서 재생됩니다.

#### C# 스크립트에서 애니메이션 제어
```csharp
using SpriteBridge;
using UnityEngine;

public class EnemyAnimSample : MonoBehaviour
{
    [SerializeField] private SpriteBridgeRenderer _bridge;

    public void OnMove()
    {
        // 클립 이름(또는 인덱스)으로 즉시 전환
        _bridge.PlayClip("Walk", speed: 1.0f);
    }

    public void OnHitFreeze()
    {
        // 특정 시간(초) 프레임에 일시정지
        _bridge.PauseAnimation(0.15f);
    }
}
```

---

### 4-4. Tier 2: Zero-GameObject 고밀도 배치 API (20,000~100,000+ 탄막/호드)
수만 개의 탄막이나 몬스터 군집을 `GameObject` 생성 없이 구동하는 예시입니다.

```csharp
using SpriteBridge;
using Unity.Collections;
using UnityEngine;

public class HordeBatchController : MonoBehaviour
{
    [SerializeField] private Texture2D _atlasTexture;
    [SerializeField] private SpriteBridgeAnimationLibrary _animLib;
    [SerializeField] private int _unitCount = 20000;

    private int _bucketId;
    private SpriteBridgeHandle[] _handles;

    private void Start()
    {
        SpriteBridgeManager mgr = SpriteBridgeManager.GetOrCreate();

        // 1. 드로우 버킷 등록 (20,000개 전체가 1 DrawCall로 배칭됨)
        _bucketId = mgr.RegisterBatchBucket(
            sourceMaterial: null,
            atlasTexture: _atlasTexture,
            sortMode: SpriteSortMode.GpuYSort);

        // 2. 세대 번호 핸들(SpriteBridgeHandle) 기반 스폰
        _handles = new SpriteBridgeHandle[_unitCount];
        for (int i = 0; i < _unitCount; i++)
        {
            Vector3 spawnPos = new Vector3(Random.Range(-25f, 25f), Random.Range(-25f, 25f), 0f);
            _handles[i] = mgr.SpawnAnimatedSprite(
                _bucketId,
                _animLib,
                localClipIndex: 0,
                position: spawnPos,
                rotation: Quaternion.identity,
                scale: Vector3.one * 0.4f,
                color: Color.white,
                animSpeed: 1.0f,
                startTimeOffset: Random.value);
        }
    }

    private void Update()
    {
        SpriteBridgeManager mgr = SpriteBridgeManager.CurrentInstance;
        if (mgr == null) return;

        // 3. 백킹 NativeArray 직접 획득 ([BurstCompile] IJobParallelFor에 그대로 전달 가능!)
        NativeArray<GpuSpriteInstance> instances = mgr.AcquireWritableInstanceBuffer(out int activeCount);
        float dt = Time.deltaTime;

        for (int i = 0; i < _handles.Length; i++)
        {
            if (mgr.TryGetSlotIndex(_handles[i], out int slot) && slot < activeCount)
            {
                GpuSpriteInstance inst = instances[slot];
                inst.Position += new Vector3(0f, -1.5f * dt, 0f);
                instances[slot] = inst;
            }
        }
    }

    public void KillUnit(int index)
    {
        // O(1) Swap-and-Pop 제거 (다른 모든 핸들의 유효성은 100% 유지됨)
        SpriteBridgeManager.CurrentInstance?.DespawnSprite(_handles[index]);
    }
}
```

---

### 4-5. 내장 셰이더 3종 및 커스텀 셰이더 / Shader Graph 연동

1. **`SpriteBridge/URP/Sprite-Unlit`**: **URP 3D**, **URP 2D**, **Built-in RP** 모두에서 동작하는 기본 Unlit 셰이더 (AlphaBlend / AlphaClip Cutout / Additive / ZWrite 지원).
2. **`SpriteBridge/URP 2D/Sprite-Lit`**: **URP 2D `Renderer2D`** 전용 조명 셰이더(`Universal2D` + `NormalsRendering` 패스)로 2D Lights, Normal Map, 2D 그림자를 완벽 지원하며 URP 3D 및 Built-in RP 폴백 패스를 포함합니다.
3. **`SpriteBridge/URP 3D/Sprite-Lit-2.5D`**: **HD-2D / 2.5D 게임**용 3D 조명 셰이더(URP 3D 및 Built-in RP 지원). 3D Main/Additional Lights, Normal Map, SH 환경광과 함께 **알파 클립 실루엣 기반 `ShadowCaster` 패스**를 내장하여 2D 스프라이트가 3D 바닥에 실시간 그림자를 드리웁니다.
4. **커스텀 셰이더 & Shader Graph**:
   - **`Tools → SpriteBridge → Custom Shader Auto-Patcher...`** 창에서 커스텀 `.shader`를 원클릭으로 패치하거나,
   - Shader Graph에서 **Custom Function** 노드(`File`: `Assets/SpriteBridge/Shaders/SpriteBridgeShaderGraphNode.hlsl`, `Name`: `SpriteBridgeVertex`)를 연결해 아티스트 제작 셰이더(디졸브, 외곽선, 히트 플래시 등)를 그대로 사용할 수 있습니다.

---

## 5. 3-Tier Play Mode 씬뷰 디버그 모드

상단 메뉴 **`Tools → SpriteBridge → Play Mode Scene View`** 또는 인스펙터 하단 드롭다운에서 실시간 전환할 수 있습니다.

| 디버그 모드 | 동작 방식 | 주요 용도 |
| :--- | :--- | :--- |
| **`Off` *(기본값)*** | Play Mode 중 씬뷰 렌더링을 생략하여 최고 성능을 유지합니다. **`IPreprocessBuildWithReport` 및 `#if !UNITY_EDITOR` 2중 가드로 모든 플레이어 빌드에서 강제 `Off` 고정**됩니다. | 실전 게임플레이 및 최대 FPS 벤치마크 |
| **`GpuSprites`** | Play Mode 씬뷰에서도 GPU 스프라이트를 렌더링하며, **`SpriteBridgeSceneViewPicker`**를 통해 씬뷰에서 개별 GPU 스프라이트를 마우스로 클릭해 `GameObject`를 선택하고 사각형 와이어프레임과 슬롯/버킷 정보를 확인할 수 있습니다. | 레벨 배치 디버깅 및 개별 유닛 클릭 피킹 |
| **`NativeSpriteRenderer`** | 카메라별 `Renderer.forceRenderingOff` 격리를 통해 **씬뷰는 100% 순정 Unity `SpriteRenderer` + `Animator`**, **게임뷰는 GPU `SpriteBridge`**로 동시에 그려 비교합니다. | 피벗, FlipX/Y, 색상, 소팅, 애니메이션 1:1 패리티 검증 |

---

## 6. 지원 기능 vs 자동 폴백 매트릭스 및 주의사항

### 안전한 자동 폴백 (시각적 깨짐 원천 차단)
`SpriteBridgeRenderer`는 미지원 기능을 억지로 그려 화면을 깨뜨리지 않고, 자동으로 순정 `SpriteRenderer`로 폴백한 뒤 인스펙터에 사유를 표시합니다.

| 기능 항목 | 지원 상태 | 상세 처리 방식 |
| :--- | :---: | :--- |
| `SpriteDrawMode.Simple` (단일 텍스처, 스프라이트 시트, `SpriteAtlas`) | **GPU 가속** | `DataUtility.GetOuterUV` & `sprite.bounds` 기반 서브픽셀 1:1 일치 |
| `FlipX` / `FlipY` (비중앙 커스텀 피벗 및 음수 Transform 스케일 포함) | **GPU 가속** | 피벗 기준 정확한 미러링 및 노말 부호(`flipX^flipY`) 자동 보정 |
| Linear / Gamma 색공간 및 HDR Bloom Tint | **GPU 가속** | 무손실 Linear `half4`(`uint2`) 패킹 (이중 감마 왜곡 없음) |
| 멀티 카메라 `Camera.cullingMask` & `gameObject.layer` | **GPU 가속** | 레이어별 버킷 격리 (UI 카메라 / 미니맵 카메라 중복 렌더링 방지) |
| 비직렬화 `Renderer.forceRenderingOff` 억제 | **기본 내장** | Edit Mode에서 `SpriteRenderer.enabled`를 건드리지 않아 `.unity`/`.prefab` Git 오염 0건 |
| `SpriteDrawMode.Sliced` / `Tiled` (9-슬라이스) | **자동 폴백** | 순정 `SpriteRenderer` 자동 유지 + 인스펙터 사유 표시 |
| `SpriteMaskInteraction != None` (스텐실 마스크) | **자동 폴백** | 순정 `SpriteRenderer` 자동 유지 + 인스펙터 사유 표시 |
| 구형 하드웨어 (`maxComputeBufferInputsVertex <= 0`) | **자동 폴백** | 순정 `SpriteRenderer` 자동 유지 |

### 사용 시 주의사항 (Best Practices)
1. **`SpriteAtlas` 사용 권장**:
   - `SpriteBridge`는 `(Material, Texture2D, Layer, SortingBucket)` 단위로 드로우콜을 묶습니다. 서로 다른 스프라이트 500종도 **1장의 `SpriteAtlas`**로 패킹하면 동일한 `Texture2D`를 공유하므로 **단 1회의 Indirect DrawCall**로 배칭됩니다.
   - `SpriteAtlas` 설정에서 인접 스프라이트 픽셀 침범을 막기 위해 **`Tight Packing`을 해제(Rectangle 패킹 사용)**하는 것을 권장합니다.
2. **`Static` vs `DynamicTransform` 구분**:
   - 움직이지 않는 배경/프롭은 `MotionMode = Static`으로 설정하면 매 프레임 CPU 동기화 비용이 **`0.000 ms`**가 됩니다.
   - 수천 개 이상의 `DynamicTransform` 오브젝트를 사용할 때는 Package Manager에서 **`Burst`(`com.unity.burst`)** 패키지를 설치하면 별도 설정 없이 자동으로 SIMD 멀티스레드 잡으로 가속됩니다.
3. **최대 버킷 한도 (`MaxBuckets = 256`)**:
   - `BucketedOrder` 모드에서 아틀라스로 묶이지 않은 수백 개의 개별 텍스처와 수백 개의 서로 다른 `SortingOrder`를 조합해 256개 버킷을 초과하면 초과분은 안전하게 순정 `SpriteRenderer`로 자동 폴백됩니다. 이때 `SpriteAtlas`로 묶거나 `SortMode`를 **`GpuYSort` / `GpuOrderAndYSort`**로 변경하면 모든 `SortingOrder`가 1개 버킷으로 통합됩니다.

---

## 라이선스
MIT License ([LICENSE.md](LICENSE.md) 참조).
