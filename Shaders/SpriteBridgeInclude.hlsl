#ifndef SPRITEBRIDGE_INCLUDE_HLSL
#define SPRITEBRIDGE_INCLUDE_HLSL

// -----------------------------------------------------------------------------
// SpriteBridge Shared GPU Data Structures & Vertex Reconstruction Helpers
// Strictly 16-byte aligned (GpuVisibleSprite = 64 bytes = 4 x float4).
// -----------------------------------------------------------------------------

struct GpuVisibleSprite
{
    float3 originWS;
    uint   packedColorRG;

    float3 rightWS;
    uint   packedColorBA;

    float3 upWS;
    uint   packedNormalFlags;

    float4 uvRect;
};

StructuredBuffer<GpuVisibleSprite> _SpriteBridgeVisibleSprites;

uint _SpriteBridgeBucketBaseOffset;

// Optional Phase 2 GPU Sort indirection buffer (enabled via _SPRITEBRIDGE_SORTED)
#if defined(_SPRITEBRIDGE_SORTED)
struct GpuSpriteSortEntry
{
    uint sortKey;
    uint visibleIndex;
};
StructuredBuffer<GpuSpriteSortEntry> _SpriteBridgeSortEntries;
#endif

inline half4 SpriteBridgeUnpackHalf4(uint packedRG, uint packedBA)
{
    half r = (half)f16tof32(packedRG & 0xFFFFu);
    half g = (half)f16tof32((packedRG >> 16) & 0xFFFFu);
    half b = (half)f16tof32(packedBA & 0xFFFFu);
    half a = (half)f16tof32((packedBA >> 16) & 0xFFFFu);
    return half4(r, g, b, a);
}

inline float2 SpriteBridgeApplyPackingRotation(float2 quadUV, uint packingRotBits)
{
    // 0: None, 1: FlipHorizontal, 2: FlipVertical, 3: Rotate180
    float2 uv = quadUV;
    if (packingRotBits == 1u)
    {
        uv.x = 1.0 - uv.x;
    }
    else if (packingRotBits == 2u)
    {
        uv.y = 1.0 - uv.y;
    }
    else if (packingRotBits == 3u)
    {
        uv = 1.0 - uv;
    }
    return uv;
}

inline GpuVisibleSprite SpriteBridgeLoadVisibleSprite(uint instanceID)
{
    uint readIndex = _SpriteBridgeBucketBaseOffset + instanceID;
#if defined(_SPRITEBRIDGE_SORTED)
    readIndex = _SpriteBridgeSortEntries[readIndex].visibleIndex;
#endif
    return _SpriteBridgeVisibleSprites[readIndex];
}

inline void SpriteBridgeReconstructVertex(
    uint instanceID,
    float2 quadUV,
    out float3 outPositionWS,
    out float2 outSpriteUV,
    out half4 outSpriteColor,
    out float3 outNormalWS,
    out float3 outTangentWS)
{
    GpuVisibleSprite s = SpriteBridgeLoadVisibleSprite(instanceID);

    outPositionWS = s.originWS + s.rightWS * quadUV.x + s.upWS * quadUV.y;

    uint packingRot = (s.packedNormalFlags >> 1) & 0x3u;
    float2 mappedUV = SpriteBridgeApplyPackingRotation(quadUV, packingRot);
    outSpriteUV = s.uvRect.xy + mappedUV * s.uvRect.zw;

    outSpriteColor = SpriteBridgeUnpackHalf4(s.packedColorRG, s.packedColorBA);

    float normalSign = ((s.packedNormalFlags & 1u) != 0u) ? -1.0 : 1.0;
    float3 rDir = normalize(s.rightWS + float3(1e-7, 0.0, 0.0));
    float3 uDir = normalize(s.upWS + float3(0.0, 1e-7, 0.0));
    outTangentWS = rDir;
    // Unity sprite front face points toward -Z in local space (cross(right, up) = +Z -> -cross = -Z)
    outNormalWS = normalize(-cross(rDir, uDir)) * normalSign;
}

#endif // SPRITEBRIDGE_INCLUDE_HLSL
