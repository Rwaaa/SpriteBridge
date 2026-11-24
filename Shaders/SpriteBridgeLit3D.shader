Shader "SpriteBridge/URP 3D/Sprite-Lit-2.5D"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _NormalMap ("Normal Map (Optional)", 2D) = "bump" {}
        _AmbientStrength ("Minimum Ambient Floor", Range(0.0, 1.0)) = 0.25
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5 // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10 // OneMinusSrcAlpha
        [MaterialToggle] _ZWrite ("ZWrite", Float) = 1
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha Clipping (Shadow & Cutout)", Float) = 1
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.35
    }

    SubShader
    {
        Tags
        {
            "Queue" = "AlphaTest"
            "RenderType" = "TransparentCutout"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        // =====================================================================
        // Pass 1: URP 3D UniversalForward (HD-2D / 2.5D Lighting + Shadows)
        // =====================================================================
        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha
            Cull Off
            ZWrite [_ZWrite]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex SpriteBridgeLit3DVert
            #pragma fragment SpriteBridgeLit3DFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_local_fragment _ _ALPHATEST_ON
            #pragma multi_compile _ _SPRITEBRIDGE_SORTED

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "SpriteBridgeInclude.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _AmbientStrength;
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
                float2 uv          : TEXCOORD0;
                half4  color       : COLOR;
                float3 positionWS  : TEXCOORD1;
                half3  normalWS    : TEXCOORD2;
                half3  tangentWS   : TEXCOORD3;
                half3  bitangentWS : TEXCOORD4;
            };

            Varyings SpriteBridgeLit3DVert(Attributes input)
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
                o.positionWS = posWS;
                o.normalWS = (half3)normalWS;
                o.tangentWS = (half3)tangentWS;
                o.bitangentWS = (half3)cross(normalWS, tangentWS);
                return o;
            }

            half4 SpriteBridgeLit3DFrag(Varyings input) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * input.color;
            #if defined(_ALPHATEST_ON)
                clip(albedo.a - _Cutoff);
            #endif

                half3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv));
                half3 N = normalize(TransformTangentToWorld(normalTS, half3x3(input.tangentWS, input.bitangentWS, input.normalWS)));

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                // Two-sided soft wrap diffuse for 2.5D paper-cutout aesthetic
                half ndotl = saturate(abs(dot(N, (half3)mainLight.direction)) * 0.75h + 0.25h);
                half3 directLighting = mainLight.color * (ndotl * mainLight.shadowAttenuation);

                half3 shAmbient = max(SampleSH(N), half3(_AmbientStrength, _AmbientStrength, _AmbientStrength));
                half3 totalLight = shAmbient + directLighting;

            #if defined(_ADDITIONAL_LIGHTS)
                uint addLightCount = GetAdditionalLightsCount();
                for (uint li = 0u; li < addLightCount; ++li)
                {
                    Light addLight = GetAdditionalLight(li, input.positionWS);
                    half addNdotL = saturate(abs(dot(N, (half3)addLight.direction)) * 0.75h + 0.25h);
                    totalLight += addLight.color * (addNdotL * addLight.distanceAttenuation * addLight.shadowAttenuation);
                }
            #endif

                return half4(albedo.rgb * totalLight, albedo.a);
            }
            ENDHLSL
        }

        // =====================================================================
        // Pass 2: Real-Time 3D ShadowCaster (Alpha-Clipped Sprite Silhouette)
        // =====================================================================
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex SpriteBridgeShadowVert
            #pragma fragment SpriteBridgeShadowFrag
            #pragma multi_compile _ _SPRITEBRIDGE_SORTED

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "SpriteBridgeInclude.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _AmbientStrength;
                half _Cutoff;
            CBUFFER_END

            float3 _LightDirection;

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
                half   alpha      : TEXCOORD1;
            };

            Varyings SpriteBridgeShadowVert(Attributes input)
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

                float4 posCS = TransformWorldToHClip(ApplyShadowBias(posWS, normalWS, _LightDirection));
            #if UNITY_REVERSED_Z
                posCS.z = min(posCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                posCS.z = max(posCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif

                o.positionCS = posCS;
                o.uv = spriteUV;
                o.alpha = spriteColor.a * _Color.a;
                return o;
            }

            half4 SpriteBridgeShadowFrag(Varyings input) : SV_Target
            {
                half texA = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a * input.alpha;
                clip(texA - max(_Cutoff, 0.05h));
                return 0;
            }
            ENDHLSL
        }

        // =====================================================================
        // Pass 3: DepthOnly (URP Depth Prepass / SSAO / Depth of Field)
        // =====================================================================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex SpriteBridgeDepthVert
            #pragma fragment SpriteBridgeDepthFrag
            #pragma multi_compile _ _SPRITEBRIDGE_SORTED

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SpriteBridgeInclude.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _AmbientStrength;
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
                half   alpha      : TEXCOORD1;
            };

            Varyings SpriteBridgeDepthVert(Attributes input)
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
                o.alpha = spriteColor.a * _Color.a;
                return o;
            }

            half4 SpriteBridgeDepthFrag(Varyings input) : SV_Target
            {
                half texA = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a * input.alpha;
                clip(texA - max(_Cutoff, 0.05h));
                return 0;
            }
            ENDHLSL
        }
    }

    // =========================================================================
    // SubShader 2: Built-in Render Pipeline (BiRP) 3D Lit + ShadowCaster
    // =========================================================================
    SubShader
    {
        Tags
        {
            "Queue" = "AlphaTest"
            "RenderType" = "TransparentCutout"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ForwardBase"
            Tags { "LightMode" = "ForwardBase" }

            Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha
            Cull Off
            ZWrite [_ZWrite]

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex SpriteBridgeBiRPLit3DVert
            #pragma fragment SpriteBridgeBiRPLit3DFrag
            #pragma multi_compile_fwdbase
            #pragma multi_compile_local_fragment _ _ALPHATEST_ON
            #pragma multi_compile _ _SPRITEBRIDGE_SORTED

            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "SpriteBridgeInclude.hlsl"

            sampler2D _MainTex;
            sampler2D _NormalMap;
            half4 _Color;
            half _AmbientStrength;
            half _Cutoff;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                uint   instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                half4  color       : COLOR;
                half3  normalWS    : TEXCOORD1;
                half3  tangentWS   : TEXCOORD2;
                half3  bitangentWS : TEXCOORD3;
            };

            Varyings SpriteBridgeBiRPLit3DVert(Attributes input)
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
                o.normalWS = (half3)normalWS;
                o.tangentWS = (half3)tangentWS;
                o.bitangentWS = (half3)cross(normalWS, tangentWS);
                return o;
            }

            half4 SpriteBridgeBiRPLit3DFrag(Varyings input) : SV_Target
            {
                half4 albedo = tex2D(_MainTex, input.uv) * input.color;
            #if defined(_ALPHATEST_ON)
                clip(albedo.a - _Cutoff);
            #endif
                half3 normalTS = UnpackNormal(tex2D(_NormalMap, input.uv));
                half3 N = normalize(
                    normalTS.x * input.tangentWS +
                    normalTS.y * input.bitangentWS +
                    normalTS.z * input.normalWS);

                half3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
                half ndotl = saturate(abs(dot(N, lightDir)) * 0.75h + 0.25h);
                half3 ambient = max(ShadeSH9(half4(N, 1.0)), half3(_AmbientStrength, _AmbientStrength, _AmbientStrength));
                half3 totalLight = ambient + _LightColor0.rgb * ndotl;
                return half4(albedo.rgb * totalLight, albedo.a);
            }
            ENDCG
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex SpriteBridgeBiRPShadowVert
            #pragma fragment SpriteBridgeBiRPShadowFrag
            #pragma multi_compile_shadowcaster
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
                half   alpha      : TEXCOORD1;
            };

            Varyings SpriteBridgeBiRPShadowVert(Attributes input)
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

                float4 clipPos = mul(UNITY_MATRIX_VP, float4(posWS, 1.0));
                o.positionCS = UnityApplyLinearShadowBias(clipPos);
                o.uv = spriteUV;
                o.alpha = spriteColor.a * _Color.a;
                return o;
            }

            half4 SpriteBridgeBiRPShadowFrag(Varyings input) : SV_Target
            {
                half texA = tex2D(_MainTex, input.uv).a * input.alpha;
                clip(texA - max(_Cutoff, 0.05h));
                return 0;
            }
            ENDCG
        }
    }
}
