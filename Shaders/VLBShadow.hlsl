// 저장된 그림자 지도 읽기 (BakedShadowMap 이 시작·해금·철거 때만 찍어 둠). 배경·캐릭터 셰이더 4개가 함께 씀
#ifndef VLB_SHADOW_INCLUDED
#define VLB_SHADOW_INCLUDED

// 그림자 지도 형식(Shadowmap) 텍스처의 자체 샘플러는 깊이 비교 + 이웃 4점 보간을 하드웨어가 해 줌 → 1번 읽기로 부드러운 경계
TEXTURE2D_SHADOW(_VLB_ShadowMap);
SAMPLER_CMP(sampler_VLB_ShadowMap);
TEXTURE2D_SHADOW(_VLB_DynShadowMap);     // 움직이는 물체만, 매 프레임. 정적 지도와 같은 좌표
SAMPLER_CMP(sampler_VLB_DynShadowMap);
float4x4 _VLB_WorldToShadow;
float _VLB_ShadowOn;

// 정점 셰이더용: 월드 위치 → 그림자 지도 좌표. 직교 투영이라 정점에서 계산해 보간해도 정확함
float3 VLBShadowCoord(float3 positionWS)
{
    return mul(_VLB_WorldToShadow, float4(positionWS, 1)).xyz;
}

// 픽셀 셰이더용: 1 = 빛 받음, 0 = 그림자. 그림자 지도가 없거나 지도 범위 밖이면 1
half VLBShadow(float3 coord)
{
    if (_VLB_ShadowOn < 0.5 || any(coord != saturate(coord)))
        return 1;
    // 어두운 쪽: 정적·움직이는 물체 그림자가 겹쳐도 한 번만 어두워지고, 색은 받는 셰이더의 그림자 공식 그대로
    return min(SAMPLE_TEXTURE2D_SHADOW(_VLB_ShadowMap, sampler_VLB_ShadowMap, coord),
               SAMPLE_TEXTURE2D_SHADOW(_VLB_DynShadowMap, sampler_VLB_DynShadowMap, coord));
}

// 움직이는 물체(캐릭터)용: 정적 지도만 읽음. 움직이는 물체는 동적 지도에 자기 몸을 찍으므로 그걸 읽으면 자기 몸에 얼룩이 생김
half VLBStaticShadow(float3 coord)
{
    if (_VLB_ShadowOn < 0.5 || any(coord != saturate(coord)))
        return 1;
    return SAMPLE_TEXTURE2D_SHADOW(_VLB_ShadowMap, sampler_VLB_ShadowMap, coord);
}

// 모델러 그림자 보정 (정점 색 R): 0.5 = 그대로, 0 쪽 = 그늘이 잘 짐, 1 쪽 = 거의 항상 밝음. N·L 에 -1 ~ +1 을 더함
half VLBShadeOffset(half maskR)
{
    return maskR * 2 - 1;
}

// 툰 명암에 쓰는 직사광 양: Toony Colors Pro 2 와 같은 반쯤 감싼 N·L (N·L × 0.5 + 0.5). 빛 반대편은 0
half WrappedLight(float ndl)
{
    return ndl > 0 ? (half)(ndl * 0.5 + 0.5) : 0;
}

#endif
