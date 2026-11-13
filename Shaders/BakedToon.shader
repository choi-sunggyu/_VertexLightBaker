// 툰 스타일 배경(정적) 오브젝트용. 실시간 빛·그림자 계산 없음.
// 직사광 양 = (N·L × 0.5 + 0.5, Toony Colors Pro 2 와 같음) × 그림자
//   N·L    : VertexLightBaker 가 TEXCOORD3.a 에 구워 둔 값
//   그림자 : BakedShadowMap 이 저장해 둔 그림자 지도를 픽셀마다 1번 읽음 (VLBShadow.hlsl)
// 이 값을 툰 셰이더처럼 명암을 단계로 끊어 표시한다.
//
//   단계 = smoothstep(경계 - 부드러움/2, 경계 + 부드러움/2, 직사광 양)   ← 픽셀당 산술 몇 개
//   최종색 = 텍스처색 × ( lerp(그림자 색, 밝은 색, 단계) × 빛 색 + 환경광 )
//
// 색·텍스처·밝은 색·그림자 색·경계 값은 한 번에 적용 시 원본 툰 재질에서 자동으로 옮겨진다.

Shader "Custom/BakedToon"
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
        [HDR] _BakedLightColor("Baked Light Color", Color) = (1,1,1,1)
        _AmbientStrength("Ambient Strength", Range(0, 2)) = 1
        [Toggle(_USE_VERTEX_COLOR)] _UseVertexColor("Use Vertex Color", Float) = 0
        [Toggle(_DEBUG_SHADOW)] _DebugShadow("Debug: Show Baked Light (black = shadow)", Float) = 0
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
            half4  _BakedLightColor;
            half   _AmbientStrength;
        CBUFFER_END

        #include "VLBShadow.hlsl"
        ENDHLSL

        Pass
        {
            Name "BakedToon"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature_local _USE_VERTEX_COLOR
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _DEBUG_SHADOW
            #pragma multi_compile_fog

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float4 bakedLight : TEXCOORD3;   // a = N·L (그림자 전, -1~1)
                half4  color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                half3  ambient     : TEXCOORD1;
                half   wrapped     : TEXCOORD2;   // 그림자 전 툰 직사광 양
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
                OUT.wrapped = WrappedLight(IN.bakedLight.a); // 빛 반대편은 0
                OUT.shadowCoord = VLBShadowCoord(positionWS);
                // 환경광은 정점에서 1번만 (굽기 도구·움직이는 물체 셰이더와 같은 값)
                OUT.ambient = SampleSH(TransformObjectToWorldNormal(IN.normalOS)) * _AmbientStrength;
                OUT.color = IN.color;
                OUT.fogFactor = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // 그림자 모양은 픽셀마다 지도에서 읽음 (정점 간격과 무관하게 또렷함)
                half lit = IN.wrapped * VLBShadow(IN.shadowCoord);

                #if defined(_DEBUG_SHADOW)
                    return half4(lit.xxx, 1); // 흰색 = 빛 받음, 검정 = 그림자·뒷면
                #endif

                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;
                #if defined(_ALPHATEST_ON)
                    clip(albedo.a - _Cutoff); // 알파 컷 (잎·철망 등). 투명 재질은 변환하지 않음
                #endif
                #if defined(_USE_VERTEX_COLOR)
                    albedo *= IN.color;
                #endif

                // 직사광 양을 툰 경계로 끊음 → 그림자 경계도 원본 툰처럼 그림자 색으로 또렷하게 바뀜
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
