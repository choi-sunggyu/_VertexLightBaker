// 그림자 지도(깊이)를 찍을 때 물체를 그리는 셰이더. BakedShadowMap 이 시작·해금·철거 때만 사용 (매 프레임 아님).
// 그림자 여드름(표면이 자기 자신에 가려지는 얼룩)을 막으려고 물체를 빛에서 멀어지는 쪽·표면 안쪽으로 조금 밀어서 그린다.
Shader "Hidden/VLB/ShadowCaster"
{
    SubShader
    {
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Off      // 한 겹짜리 벽·판도 그림자를 드리우도록 양면
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 _VLB_ShadowBias;      // x = 빛 방향으로 밀기, y = 법선 반대로 밀기 (월드 단위)
            float4 _VLB_ShadowLightDir;  // 표면에서 빛 쪽 (월드)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            float4 vert(Attributes v) : SV_POSITION
            {
                float3 p = TransformObjectToWorld(v.positionOS.xyz);
                float3 n = TransformObjectToWorldNormal(v.normalOS);
                p -= _VLB_ShadowLightDir.xyz * _VLB_ShadowBias.x;
                p -= n * _VLB_ShadowBias.y;
                return TransformWorldToHClip(p);
            }

            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
