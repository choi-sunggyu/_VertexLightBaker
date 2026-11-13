// 셰이더 그래프용 (VLB Baked Light 서브그래프). 무늬를 계산으로 그리는 셰이더 그래프(텍스처 없는 상자 등)는
// 도구가 셰이더를 바꾸면 무늬가 사라지므로, 그래프는 그대로 두고 이 노드로 빛만 구운 조명과 같은 공식으로 계산함.
// 빛 = 주변광(씬 환경광) + 빛색 × N·L × 그림자.  빛색·방향은 BakedShadowMap 이 전역값으로 넣어 줌 (방향광 없이 동작)
// 사용: 그래프를 Unlit 으로 바꾸고 Base Color 앞에 이 노드를 끼움 → 도구는 방향광이 필요 없는 셰이더로 보고 그대로 둠
// shortcut: 주변광은 항상 씬 환경광(SH). 도구의 "주변광 색 값" 옵션은 반영 안 됨, 필요하면 전역 주변광 값을 추가할 것
#ifndef VLB_BAKED_LIGHT_INCLUDED
#define VLB_BAKED_LIGHT_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/AmbientProbe.hlsl"
#include "Packages/com.vlb.vertex-light-baker/Shaders/VLBShadow.hlsl"

float4 _VLB_LightColor; // 선형 색 × 세기. 저장 전(BakedShadowMap 없음)이면 0 → 주변광만
float4 _VLB_LightDir;   // 표면에서 빛 쪽 (월드)

void VLBBakedLight_float(float3 Albedo, float3 PositionWS, float3 NormalWS, out float3 Out)
{
#if defined(SHADERGRAPH_PREVIEW)
    Out = Albedo; // 그래프 미리보기에는 전역값이 없음
#else
    float3 n = normalize(NormalWS);
    float ndl = max(0, dot(n, _VLB_LightDir.xyz));
    float shadow = VLBShadow(VLBShadowCoord(PositionWS));
    Out = Albedo * (EvaluateAmbientProbeSRGB(n) + _VLB_LightColor.rgb * ndl * shadow);
#endif
}

void VLBBakedLight_half(half3 Albedo, half3 PositionWS, half3 NormalWS, out half3 Out)
{
    float3 o;
    VLBBakedLight_float(Albedo, PositionWS, NormalWS, o);
    Out = (half3)o;
}

#endif
