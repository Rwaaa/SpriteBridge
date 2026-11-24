Shader "SpriteBridge/URP/Sprite-Unlit"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5 // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10 // OneMinusSrcAlpha
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

        Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha
        Cull Off
        ZWrite [_ZWrite]

        // LightMode = SRPDefaultUnlit renders automatically in BOTH URP 3D (UniversalRenderer)
        // AND URP 2D (Renderer2D)!
        Pass
        {
            Name "SpriteBridgeUnlit"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex SpriteBridgeUnlitVert
            #pragma fragment SpriteBridgeUnlitFrag
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

            Varyings SpriteBridgeUnlitVert(Attributes input)
            {
                Varyings output;

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

                output.positionCS = TransformWorldToHClip(posWS);
                output.uv = spriteUV;
                output.color = spriteColor * _Color;
                return output;
            }

            half4 SpriteBridgeUnlitFrag(Varyings input) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                half4 col = tex * input.color;
            #if defined(_ALPHATEST_ON)
                clip(col.a - _Cutoff);
            #endif
                return col;
            }
            ENDHLSL
        }
    }

    // =========================================================================
    // SubShader 2: Built-in Render Pipeline (BiRP) Fallback
    // =========================================================================
    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha
        Cull Off
        ZWrite [_ZWrite]

        Pass
        {
            Name "SpriteBridgeBuiltInUnlit"

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex SpriteBridgeBiRPVert
            #pragma fragment SpriteBridgeBiRPFrag
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

            Varyings SpriteBridgeBiRPVert(Attributes input)
            {
                Varyings output;
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

                output.positionCS = mul(UNITY_MATRIX_VP, float4(posWS, 1.0));
                output.uv = spriteUV;
                output.color = spriteColor * _Color;
                return output;
            }

            half4 SpriteBridgeBiRPFrag(Varyings input) : SV_Target
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
