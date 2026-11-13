// 툰 스타일 "움직이는" 오브젝트용 (캐릭터·돈 등). 씬의 방향광·실시간 그림자를 쓰지 않음.
// BakedToon 과 같은 툰 공식이지만, 움직이면 법선이 바뀌어 미리 구울 수 없으므로
// 직사광 양(N·L)만 정점 셰이더에서 고정 빛 방향으로 1번 계산한다.
//
//   직사광 양 = N·L × 0.5 + 0.5 (빛 반대편은 0)           ← 정점당 dot 1번 (Toony Colors Pro 2 와 같은 값)
//              × 정적 그림자 (배경 그림자 안에 들어가면 어두워짐, 픽셀당 1번 읽기)
//              (선택) N·L 에 모델러 보정(정점 색 R) 을 더함
//   단계 = smoothstep(경계 - 부드러움/2, 경계 + 부드러움/2, 직사광 양)
//   최종색 = 텍스처색 × ( lerp(그림자 색, 밝은 색, 단계) × 빛 색 + 환경광 )
//
// 빛 방향·색과 툰 값은 한 번에 적용 때 굽기 도구가 원본 툰 재질에서 옮겨 넣는다.

Shader "Custom/DynamicToon"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
        [Toggle(_ALPHATEST_ON)] _AlphaClip("Alpha Clip", Float) = 0
        _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.5
        _HColor("Highlight Color", Color) = (1,1,1,1)
        _SColor("Shadow Color", Color) = (0.55,0.55,0.65,1)
        _RampThreshold("Ramp Threshold", Range(0, 1)) = 0.5
        _RampSmoothing("Ramp Smoothing", Range(0.001, 1)) = 0.1
        _BakedLightDir("Baked Light Direction (to light, world)", Vector) = (0.3, 0.8, -0.5, 0)
        [HDR] _BakedLightColor("Baked Light Color", Color) = (1,1,1,1)
        _AmbientStrength("Ambient Strength", Range(0, 2)) = 1
        [Toggle(_USE_VERTEX_COLOR)] _UseVertexColor("Use Vertex Color", Float) = 0
        [Toggle(_SHADE_MASK)] _ShadeMask("Shadow Mask (Vertex Color R: 0.5 = none, 0 = darker, 1 = brighter)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4  _BaseColor;
            half   _Cutoff;
            half4  _HColor;
            half4  _SColor;
            half   _RampThreshold;
            half   _RampSmoothing;
            float4 _BakedLightDir;
            half4  _BakedLightColor;
            half   _AmbientStrength;
        CBUFFER_END

        #include "VLBShadow.hlsl"
        ENDHLSL

        Pass
        {
            Name "DynamicToon"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature_local _USE_VERTEX_COLOR
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _SHADE_MASK
            #pragma multi_compile_fog

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                half3  ambient     : TEXCOORD1;
                half   lit         : TEXCOORD2;
                half   fogFactor   : TEXCOORD3;
                float3 shadowCoord : TEXCOORD4;
                half4  color       : COLOR;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(positionWS);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                half3 normalWS = TransformObjectToWorldNormal(IN.normalOS);
                // Toony Colors Pro 2 와 같은 반쯤 감싼 N·L (× 0.5 + 0.5). 빛 반대편은 0 (원본의 자기 그림자 자리)
                half ndl = dot(normalWS, (half3)_BakedLightDir.xyz);
                #if defined(_SHADE_MASK)
                    ndl += VLBShadeOffset(IN.color.r);
                #endif
                OUT.lit = ndl > 0 ? saturate(ndl * 0.5h + 0.5h) : 0;
                OUT.shadowCoord = VLBShadowCoord(positionWS);
                // 환경광은 정점에서 1번만 (굽기 도구·BakedToon 과 같은 값)
                OUT.ambient = SampleSH(normalWS) * _AmbientStrength;
                OUT.color = IN.color;
                OUT.fogFactor = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;
                #if defined(_ALPHATEST_ON)
                    clip(albedo.a - _Cutoff); // 알파 컷 (잎·철망 등). 투명 재질은 변환하지 않음
                #endif
                #if defined(_USE_VERTEX_COLOR)
                    albedo *= IN.color;
                #endif

                // 보간된 직사광 양 × 정적 그림자를 툰 경계로 끊음 (BakedToon 과 같은 공식 → 배경 그림자와 같은 색)
                half lit = IN.lit * VLBStaticShadow(IN.shadowCoord);
                half halfWidth = _RampSmoothing * 0.5h;
                half ramp = smoothstep(_RampThreshold - halfWidth, _RampThreshold + halfWidth, lit);
                half3 tone = lerp(_SColor.rgb, _HColor.rgb, ramp) * _BakedLightColor.rgb;

                half3 col = albedo.rgb * (tone + IN.ambient);
                col = MixFog(col, IN.fogFactor);
                return half4(col, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #pragma shader_feature_local _ALPHATEST_ON

            struct AttributesD { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct VaryingsD   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            VaryingsD vertDepth(AttributesD IN)
            {
                VaryingsD OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            half fragDepth(VaryingsD IN) : SV_Target
            {
                // 깊이에도 구멍을 뚫어야 깊이 텍스처를 쓰는 효과가 잎·철망 뒤를 가리지 않음
                #if defined(_ALPHATEST_ON)
                    clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).a * _BaseColor.a - _Cutoff);
                #endif
                return IN.positionCS.z;
            }
            ENDHLSL
        }
    }
}
