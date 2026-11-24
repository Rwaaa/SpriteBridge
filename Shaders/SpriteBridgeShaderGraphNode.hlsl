#ifndef SPRITEBRIDGE_SHADERGRAPH_NODE_HLSL
#define SPRITEBRIDGE_SHADERGRAPH_NODE_HLSL

#include "SpriteBridgeInclude.hlsl"

// =============================================================================
// Unity Shader Graph Custom Function Node Entrypoints
// Usage in Shader Graph:
//   1. Add a 'Custom Function' node (Type = File, Source = SpriteBridgeShaderGraphNode.hlsl)
//   2. Name = SpriteBridgeVertex
//   3. Inputs:  uint InstanceID (connect 'Instance ID' node), Vector2 QuadUV (connect 'UV0' node)
//   4. Outputs: Vector3 PositionWS, Vector2 SpriteUV, Vector4 Color, Vector3 NormalWS, Vector3 TangentWS
//   5. Connect PositionWS -> Vertex Position (World Space)!
// =============================================================================

void SpriteBridgeVertex_float(
    float InstanceID,
    float2 QuadUV,
    out float3 PositionWS,
    out float2 SpriteUV,
    out float4 Color,
    out float3 NormalWS,
    out float3 TangentWS)
{
    uint instId = (uint)(InstanceID + 0.5);
    half4 colHalf;
    SpriteBridgeReconstructVertex(instId, QuadUV, PositionWS, SpriteUV, colHalf, NormalWS, TangentWS);
    Color = (float4)colHalf;
}

void SpriteBridgeVertex_half(
    half InstanceID,
    half2 QuadUV,
    out half3 PositionWS,
    out half2 SpriteUV,
    out half4 Color,
    out half3 NormalWS,
    out half3 TangentWS)
{
    uint instId = (uint)(InstanceID + 0.5);
    float3 posF, normF, tanF;
    float2 uvF;
    SpriteBridgeReconstructVertex(instId, (float2)QuadUV, posF, uvF, Color, normF, tanF);
    PositionWS = (half3)posF;
    SpriteUV = (half2)uvF;
    NormalWS = (half3)normF;
    TangentWS = (half3)tanF;
}

#endif // SPRITEBRIDGE_SHADERGRAPH_NODE_HLSL
