Shader "SpriteBridge/URP 2D/Sprite-Lit"
{
    Properties
    {
        _MainTex ("Diffuse", 2D) = "white" {}
        _MaskTex ("Mask", 2D) = "white" {}
        _NormalMap ("Normal Map", 2D) = "bump" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [MaterialToggle] _ZWrite ("ZWrite", Float) = 0
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha Clipping", Float) = 0
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        Cull Off
        ZWrite [_ZWrite]

        // =====================================================================
        // Pass 1: URP 2D Renderer (Renderer2D) Combined 2D Shape Lights
        // Compatible across URP 14 (Unity 2022.3 LTS) through URP 17 (Unity 6)
        // =====================================================================
        Pass
        {
            Name "Universal2D"
            Tags { "LightMode" = "Universal2D" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex SpriteBridgeLit2DVert
            #pragma fragment SpriteBridgeLit2DFrag

            #pragma multi_compile USE_SHAPE_LIGHT_TYPE_0 __
            #pragma multi_compile USE_SHAPE_LIGHT_TYPE_1 __
            #pragma multi_compile USE_SHAPE_LIGHT_TYPE_2 __
            #pragma multi_compile USE_SHAPE_LIGHT_TYPE_3 __
            #pragma multi_compile_local_fragment _ _ALPHATEST_ON
            #pragma multi_compile _ _SPRITEBRIDGE_SORTED

            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Core2D.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/CombinedShapeLightShared.hlsl"
            #include "SpriteBridgeInclude.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_MaskTex);
            SAMPLER(sampler_MaskTex);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Cutoff;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                uint   instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                half2  lightingUV : TEXCOORD1;
            };

            Varyings SpriteBridgeLit2DVert(Attributes input)
            {
                Varyings o;

                float3 posWS;
                float2 spriteUV;
                half4 spriteColor;
                float3 normalWS;
                float3 tangentWS;
                SpriteBridgeReconstructVertex(
                    input.instanceID,
                    input.uv,
                    posWS,
                    spriteUV,
                    spriteColor,
                    normalWS,
                    tangentWS);

                o.positionCS = TransformWorldToHClip(posWS);
                o.uv = spriteUV;
                o.lightingUV = half2(ComputeScreenPos(o.positionCS / o.positionCS.w).xy);
                o.color = spriteColor * _Color;
                return o;
            }

            half4 SpriteBridgeLit2DFrag(Varyings input) : SV_Target
            {
                const half4 main = input.color * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
            #if defined(_ALPHATEST_ON)
                clip(main.a - _Cutoff);
            #endif
                const half4 mask = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, input.uv);
                const half3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv));

                SurfaceData2D surfaceData;
                InputData2D inputData;

                InitializeSurfaceData(main.rgb, main.a, mask, normalTS, surfaceData);
                InitializeInputData(input.uv, input.lightingUV, inputData);
                return CombinedShapeLightShared(surfaceData, inputData);
            }
            ENDHLSL
        }

        // =====================================================================
        // Pass 2: URP 2D Normal Map Rendering Pass
        // =====================================================================
        Pass
        {
            Name "NormalsRendering"
            Tags { "LightMode" = "NormalsRendering" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex SpriteBridgeNormals2DVert
            #pragma fragment SpriteBridgeNormals2DFrag
            #pragma multi_compile_local_fragment _ _ALPHATEST_ON
            #pragma multi_compile _ _SPRITEBRIDGE_SORTED

            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Core2D.hlsl"
            #include "SpriteBridgeInclude.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Cutoff;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                uint   instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                half4  color       : COLOR;
                float2 uv          : TEXCOORD0;
                half3  normalWS    : TEXCOORD1;
                half3  tangentWS   : TEXCOORD2;
                half3  bitangentWS : TEXCOORD3;
            };

            Varyings SpriteBridgeNormals2DVert(Attributes input)
            {
                Varyings o;

                float3 posWS;
                float2 spriteUV;
                half4 spriteColor;
                float3 normalWS;
                float3 tangentWS;
                SpriteBridgeReconstructVertex(
                    input.instanceID,
                    input.uv,
                    posWS,
                    spriteUV,
                    spriteColor,
                    normalWS,
                    tangentWS);

                o.positionCS = TransformWorldToHClip(posWS);
                o.uv = spriteUV;
                o.color = spriteColor * _Color;
                o.normalWS = (half3)normalWS;
                o.tangentWS = (half3)tangentWS;
                o.bitangentWS = (half3)cross(normalWS, tangentWS);
                return o;
            }

            half4 SpriteBridgeNormals2DFrag(Varyings input) : SV_Target
            {
                const half4 mainTex = input.color * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
            #if defined(_ALPHATEST_ON)
                clip(mainTex.a - _Cutoff);
            #endif
                const half3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv));
                // Compute world normal directly from GPU-reconstructed (flip-aware) TBN instead of unity_SpriteProps
                const half3 worldNormal = TransformTangentToWorld(normalTS, half3x3(input.tangentWS, input.bitangentWS, input.normalWS));
                return half4(0.5 * (worldNormal + 1.0), mainTex.a);
            }
            ENDHLSL
        }

        // =====================================================================
        // Pass 3: URP 3D (UniversalRenderer) Unlit Fallback (matches Sprite-Lit-Default)
        // =====================================================================
        Pass
        {
            Name "UniversalForwardFallback"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex SpriteBridgeFwdFallbackVert
            #pragma fragment SpriteBridgeFwdFallbackFrag
            #pragma multi_compile_local_fragment _ _ALPHATEST_ON
            #pragma multi_compile _ _SPRITEBRIDGE_SORTED

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SpriteBridgeInclude.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Cutoff;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                uint   instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
            };

            Varyings SpriteBridgeFwdFallbackVert(Attributes input)
            {
                Varyings o;

                float3 posWS;
                float2 spriteUV;
                half4 spriteColor;
                float3 normalWS;
                float3 tangentWS;
                SpriteBridgeReconstructVertex(
                    input.instanceID,
                    input.uv,
                    posWS,
                    spriteUV,
                    spriteColor,
                    normalWS,
                    tangentWS);

                o.positionCS = TransformWorldToHClip(posWS);
                o.uv = spriteUV;
                o.color = spriteColor * _Color;
                return o;
            }

            half4 SpriteBridgeFwdFallbackFrag(Varyings input) : SV_Target
            {
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * input.color;
            #if defined(_ALPHATEST_ON)
                clip(col.a - _Cutoff);
            #endif
                return col;
            }
            ENDHLSL
        }
    }

    // =========================================================================
    // SubShader 2: Built-in Render Pipeline (BiRP) Compatibility Fallback
    // =========================================================================
    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        Cull Off
        ZWrite [_ZWrite]

        Pass
        {
            Name "SpriteBridgeBuiltIn2DFallback"

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex SpriteBridgeBiRP2DVert
            #pragma fragment SpriteBridgeBiRP2DFrag
            #pragma multi_compile_local_fragment _ _ALPHATEST_ON
            #pragma multi_compile _ _SPRITEBRIDGE_SORTED

            #include "UnityCG.cginc"
            #include "SpriteBridgeInclude.hlsl"

            sampler2D _MainTex;
            half4 _Color;
            half _Cutoff;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                uint   instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
            };

            Varyings SpriteBridgeBiRP2DVert(Attributes input)
            {
                Varyings o;
                float3 posWS;
                float2 spriteUV;
                half4 spriteColor;
                float3 normalWS;
                float3 tangentWS;
                SpriteBridgeReconstructVertex(
                    input.instanceID,
                    input.uv,
                    posWS,
                    spriteUV,
                    spriteColor,
                    normalWS,
                    tangentWS);

                o.positionCS = mul(UNITY_MATRIX_VP, float4(posWS, 1.0));
                o.uv = spriteUV;
                o.color = spriteColor * _Color;
                return o;
            }

            half4 SpriteBridgeBiRP2DFrag(Varyings input) : SV_Target
            {
                half4 col = tex2D(_MainTex, input.uv) * input.color;
            #if defined(_ALPHATEST_ON)
                clip(col.a - _Cutoff);
            #endif
                return col;
            }
            ENDCG
        }
    }
}
