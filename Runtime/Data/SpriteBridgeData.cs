using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SpriteBridge
{
    /// <summary>
    /// Editor-only Play Mode Scene View debugging mode.
    /// Forced to Off in all Player Builds via IPreprocessBuildWithReport and #if !UNITY_EDITOR.
    /// </summary>
    public enum SpriteSceneViewDebugMode
    {
        /// <summary>
        /// Zero Scene View overhead during Play Mode (Game View renders GPU sprites at max FPS).
        /// </summary>
        Off = 0,

        /// <summary>
        /// Renders GPU sprites in both Game View and Scene View during Play Mode,
        /// with individual Scene View click-picking via SpriteBridgeSceneViewPicker.
        /// </summary>
        GpuSprites = 1,

        /// <summary>
        /// Scene View renders 100% pure native Unity SpriteRenderers (using Renderer.forceRenderingOff
        /// toggled per camera) while Game View renders GPU SpriteBridge — ideal for 1:1 visual parity checks.
        /// </summary>
        NativeSpriteRenderer = 2
    }

    /// <summary>
    /// Controls how a bridged SpriteRenderer synchronizes its Transform to the GPU buffer.
    /// </summary>
    public enum SpriteMotionMode
    {
        /// <summary>
        /// Uploaded once on registration or when MarkDirty() is called (0.000 ms/frame CPU).
        /// Ideal for static props, scenery, tiles, and non-moving backgrounds.
        /// </summary>
        Static = 0,

        /// <summary>
        /// Registered in TransformAccessArray and synchronized every frame via multi-threaded
        /// IJobParallelForTransform (automatically Burst-compiled when com.unity.burst is installed).
        /// Ideal for moving enemies, NPCs, bullets, and animated GameObjects.
        /// </summary>
        DynamicTransform = 1
    }

    /// <summary>
    /// Bucket-level draw sorting strategy.
    /// </summary>
    public enum SpriteSortMode
    {
        /// <summary>
        /// No per-instance sorting (fastest). Ideal for Cutout/AlphaClip (ZWrite On) or Additive sprites.
        /// </summary>
        None = 0,

        /// <summary>
        /// Groups sprites by (Material, Texture, SortingLayer, SortingOrder) into deterministic
        /// draw buckets with zero GPU sort compute passes.
        /// </summary>
        BucketedOrder = 1,

        /// <summary>
        /// GPU Bitonic Sort by dynamic world-space -Y (Phase 2).
        /// </summary>
        GpuYSort = 2,

        /// <summary>
        /// GPU Bitonic Sort combining SortingOrder + dynamic world-space -Y (Phase 2).
        /// </summary>
        GpuOrderAndYSort = 3
    }

    /// <summary>
    /// Optional billboard orientation evaluated directly inside the Compute Shader.
    /// </summary>
    public enum SpriteBillboardMode
    {
        None = 0,
        FullBillboard = 1,
        YAxisBillboard = 2
    }

    /// <summary>
    /// Bit flags packed into GpuSpriteInstance.PackedMeta (bits 16..23).
    /// </summary>
    [Flags]
    public enum GpuSpriteFlags : uint
    {
        None = 0u,
        Active = 1u << 0,
        BillboardFull = 1u << 1,
        BillboardYAxis = 1u << 2,
        AnimLoop = 1u << 3,
        FlipNormal = 1u << 4,
        PackingRotBit0 = 1u << 5,
        PackingRotBit1 = 1u << 6,
        CombineOrderWithYSort = 1u << 7
    }

    /// <summary>
    /// Per-sprite GPU input record.
    /// Strictly 96 bytes (6 x float4) matching HLSL GpuSpriteInstance layout.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuSpriteInstance
    {
        public Vector3 Position;        // 12 B
        public float SortKey;           //  4 B (16)

        public Vector4 Rotation;        // 16 B (32) Quaternion (x, y, z, w)

        public Vector3 Scale;           // 12 B (lossyScale with flipX/flipY signs baked in)
        public uint PackedMeta;         //  4 B (48) [0..15: animClipIndex (0xFFFF=static), 16..23: flags, 24..31: bucketIndex]

        public Vector4 UvRect;          // 16 B (64) (uvMin.x, uvMin.y, uvSize.x, uvSize.y)

        public Vector4 PivotSize;       // 16 B (80) (localMin.x, localMin.y, localSize.x, localSize.y)

        public uint PackedColorRG;      //  4 B (Linear half2 r, g)
        public uint PackedColorBA;      //  4 B (Linear half2 b, a)
        public float AnimTimeOffset;    //  4 B
        public float AnimSpeed;         //  4 B (96)

        public const int StrideBytes = 96;
        public const uint StaticAnimClipIndex = 0xFFFFu;
    }

    /// <summary>
    /// Per-visible-sprite GPU output written by Compute Shader and consumed by Vertex Shader.
    /// Strictly 64 bytes (4 x float4) matching HLSL GpuVisibleSprite layout.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuVisibleSprite
    {
        public Vector3 OriginWS;        // 12 B
        public uint PackedColorRG;      //  4 B (16)

        public Vector3 RightWS;         // 12 B
        public uint PackedColorBA;      //  4 B (32)

        public Vector3 UpWS;            // 12 B
        public uint PackedNormalFlags;  //  4 B (48)

        public Vector4 UvRect;          // 16 B (64)

        public const int StrideBytes = 64;
    }

    /// <summary>
    /// GPU 2D animation clip descriptor (16 bytes, ready for Phase 2 Animation Baker).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuSpriteAnimClip
    {
        public uint FrameStartIndex;
        public uint FrameCount;
        public float Duration;
        public uint Flags;

        public const int StrideBytes = 16;
    }

    /// <summary>
    /// GPU 2D animation frame descriptor (32 bytes, supports per-frame trimmed pivot/size).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuSpriteAnimFrame
    {
        public Vector4 UvRect;
        public Vector4 PivotSize;

        public const int StrideBytes = 32;
    }

    /// <summary>
    /// GPU Bitonic Sort entry (8 bytes) used by GpuYSort and GpuOrderAndYSort buckets.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuSpriteSortEntry
    {
        public uint SortKey;
        public uint VisibleIndex;

        public const int StrideBytes = 8;
    }

    /// <summary>
    /// Auxiliary per-dynamic-transform metadata consumed by IJobParallelForTransform (16 bytes).
    /// Maps a TransformAccessArray element index to its dense GpuSpriteInstance slot + flip signs.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SpriteSyncSlotMeta
    {
        public int InstanceSlotIndex;   // Target index in NativeArray<GpuSpriteInstance>
        public float FlipSignX;         // +1.0f or -1.0f
        public float FlipSignY;         // +1.0f or -1.0f
        public float BaseSortKey;       // Base layer/order sort key (preserved during transform updates)
    }

    /// <summary>
    /// Zero-GC dictionary key grouping sprites that can share a single indirect draw bucket.
    /// Sprites from the same SpriteAtlas share the same Texture2D and collapse into 1 bucket = 1 DrawCall.
    /// </summary>
    public readonly struct SpriteBucketKey : IEquatable<SpriteBucketKey>
    {
        public readonly int MaterialInstanceId;
        public readonly int TextureInstanceId;
        public readonly int SortingLayerValue;
        public readonly int SortingOrderBucket;
        public readonly int SortModeInt;
        public readonly int Layer;

        public SpriteBucketKey(
            int materialInstanceId,
            int textureInstanceId,
            int sortingLayerValue,
            int sortingOrderBucket,
            SpriteSortMode sortMode,
            int layer = 0)
        {
            MaterialInstanceId = materialInstanceId;
            TextureInstanceId = textureInstanceId;
            SortingLayerValue = sortingLayerValue;
            SortingOrderBucket = sortingOrderBucket;
            SortModeInt = (int)sortMode;
            Layer = (layer >= 0 && layer < 32) ? layer : 0;
        }

        public bool Equals(SpriteBucketKey other)
        {
            return MaterialInstanceId == other.MaterialInstanceId &&
                   TextureInstanceId == other.TextureInstanceId &&
                   SortingLayerValue == other.SortingLayerValue &&
                   SortingOrderBucket == other.SortingOrderBucket &&
                   SortModeInt == other.SortModeInt &&
                   Layer == other.Layer;
        }

        public override bool Equals(object obj)
        {
            return obj is SpriteBucketKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = MaterialInstanceId;
                hash = (hash * 397) ^ TextureInstanceId;
                hash = (hash * 397) ^ SortingLayerValue;
                hash = (hash * 397) ^ SortingOrderBucket;
                hash = (hash * 397) ^ SortModeInt;
                hash = (hash * 397) ^ Layer;
                return hash;
            }
        }
    }

    /// <summary>
    /// Pure utility methods for exact Unity Sprite geometry, UV, packing rotation, and Linear/HDR color encoding.
    /// </summary>
    public static class SpriteGeometryUtility
    {
        /// <summary>
        /// Packs an sRGB/HDR Unity Color into two uints storing Linear half4 (matches Unity's native
        /// SpriteRenderer vertex color space in both Linear and Gamma projects without double-gamma error).
        /// </summary>
        public static void PackColorHalf4(Color color, out uint packedRG, out uint packedBA)
        {
            Color gpuColor = (QualitySettings.activeColorSpace == ColorSpace.Linear) ? color.linear : color;
            uint r = Mathf.FloatToHalf(gpuColor.r);
            uint g = Mathf.FloatToHalf(gpuColor.g);
            uint b = Mathf.FloatToHalf(gpuColor.b);
            uint a = Mathf.FloatToHalf(gpuColor.a);
            packedRG = (r & 0xFFFFu) | ((g & 0xFFFFu) << 16);
            packedBA = (b & 0xFFFFu) | ((a & 0xFFFFu) << 16);
        }

        /// <summary>
        /// Packs animClipIndex (16b), flags (8b), and bucketIndex (8b) into GpuSpriteInstance.PackedMeta.
        /// </summary>
        public static uint PackMeta(uint animClipIndex, GpuSpriteFlags flags, uint bucketIndex)
        {
            return (animClipIndex & 0xFFFFu) |
                   (((uint)flags & 0xFFu) << 16) |
                   ((bucketIndex & 0xFFu) << 24);
        }

        /// <summary>
        /// Extracts exact local quad bounds relative to pivot (localMin.xy, localSize.xy) and
        /// normalized texture UV rect (uvMin.xy, uvSize.xy) for both untrimmed, trimmed, and
        /// SpriteAtlas-packed sprites without throwing on SpritePackingMode.Tight.
        /// </summary>
        public static bool TryExtractSpriteGeometry(
            Sprite sprite,
            out Texture2D outTexture,
            out Vector4 outUvRect,
            out Vector4 outPivotSize,
            out uint outPackingRotBits,
            out bool outIsTightPacked)
        {
            outTexture = null;
            outUvRect = new Vector4(0f, 0f, 1f, 1f);
            outPivotSize = new Vector4(-0.5f, -0.5f, 1f, 1f);
            outPackingRotBits = 0u;
            outIsTightPacked = false;

            if (sprite == null)
            {
                return false;
            }

            outTexture = sprite.texture;
            if (outTexture == null)
            {
                return false;
            }

            // Safely handle SpritePackingMode.Tight (DataUtility.GetOuterUV / sprite.textureRect throw on Tight!)
            if (sprite.packed && sprite.packingMode == SpritePackingMode.Tight)
            {
                outIsTightPacked = true;
                Bounds tightBounds = sprite.bounds;
                outPivotSize = new Vector4(tightBounds.min.x, tightBounds.min.y, tightBounds.size.x, tightBounds.size.y);

                Vector2[] uvs = sprite.uv;
                if (uvs != null && uvs.Length > 0)
                {
                    Vector2 uMin = uvs[0];
                    Vector2 uMax = uvs[0];
                    for (int i = 1; i < uvs.Length; i++)
                    {
                        uMin = Vector2.Min(uMin, uvs[i]);
                        uMax = Vector2.Max(uMax, uvs[i]);
                    }
                    outUvRect = new Vector4(uMin.x, uMin.y, uMax.x - uMin.x, uMax.y - uMin.y);
                }
                return true;
            }

            // Standard / FullRect / Rectangle-packed SpriteAtlas:
            // - sprite.bounds gives exact trimmed local bounds relative to pivot in world units (0 GC)
            // - DataUtility.GetOuterUV(sprite) gives exact normalized UV rect inside sprite.texture including SpriteAtlas packing (0 GC)
            Bounds bounds = sprite.bounds;
            outPivotSize = new Vector4(bounds.min.x, bounds.min.y, bounds.size.x, bounds.size.y);

            Vector4 outerUv = UnityEngine.Sprites.DataUtility.GetOuterUV(sprite);
            outUvRect = new Vector4(
                outerUv.x,
                outerUv.y,
                outerUv.z - outerUv.x,
                outerUv.w - outerUv.y);

            if (sprite.packed)
            {
                switch (sprite.packingRotation)
                {
                    case SpritePackingRotation.FlipHorizontal:
                        outPackingRotBits = 1u;
                        break;
                    case SpritePackingRotation.FlipVertical:
                        outPackingRotBits = 2u;
                        break;
                    case SpritePackingRotation.Rotate180:
                        outPackingRotBits = 3u;
                        break;
                    default:
                        outPackingRotBits = 0u;
                        break;
                }
            }

            return true;
        }
    }
}
