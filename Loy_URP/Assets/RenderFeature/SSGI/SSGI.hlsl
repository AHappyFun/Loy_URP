#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

TEXTURE2D_X_HALF(_GBuffer2);
SAMPLER(sampler_GBuffer2);
TEXTURE2D_X(_CameraOpaqueTexture);
SAMPLER(sampler_CameraOpaqueTexture);

TEXTURE2D_X(_SSGIResultTex);
TEXTURE2D(_SSGIBlurSource);
SAMPLER(sampler_SSGIBlurSource);

struct Attributes
{
    uint vertexID   : SV_VertexID;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

float3 ReconstructViewPos(float2 uv, float rawDepth)
{
    // URP 17 不设置 _InvProjMatrix 全局，改用内置 UNITY_MATRIX_I_VP
    float3 worldPos = ComputeWorldSpacePosition(uv * 2.0 - 1.0, rawDepth, UNITY_MATRIX_I_VP);
    return mul(UNITY_MATRIX_V, float4(worldPos, 1.0)).xyz;
}

float3 SampleSceneColor(float2 uv)
{
    return SAMPLE_TEXTURE2D_X(_CameraOpaqueTexture, sampler_CameraOpaqueTexture, uv).rgb;
}


int _NumDirs;
float _MaxRayDistance;
int _NumSteps;
float _StepScale;
float _DepthBias;
float _GITexRes;

float DistanceFalloff(float distance)
{
    float distanceRatio = distance / _MaxRayDistance;
    float falloff = saturate(1.0 - distanceRatio * distanceRatio);
    return falloff;
}

void BuildTBN(float3 n, out float3 t, out float3 b)
{
    float3 up = abs(n.z) < 0.999 ? float3(0,0,1) : float3(0,1,0);
    t = normalize(cross(up, n));
    b = cross(n, t);
}

float3 GetSSGIDirectionVS(int dirIndex, float3 normalVS)
{
    float3 tangentVS, bitangentVS;
    BuildTBN(normalVS, tangentVS, bitangentVS);

    float angle = (dirIndex / (float)_NumDirs) * TWO_PI;

    float3 dirLocal = normalize(float3(
        cos(angle),
        sin(angle),
        0.7
    ));

    return normalize(
        dirLocal.x * tangentVS +
        dirLocal.y * bitangentVS +
        dirLocal.z * normalVS
    );
}

float2 GetGITexSize()
{
    return _GITexRes * _ScaledScreenParams.xy;
}

float4 SSGIRaymarch(float2 uv)
{

    //如果使用半分辨率的AO，深度也需要用半分辨的。不然会出现横竖线。
    float rawDepth = SampleSceneDepth(uv);
    float3 ViewPos = ReconstructViewPos(uv, rawDepth);
    //ViewPos.z *= -1;

    float3 NormalVS = SAMPLE_TEXTURE2D(_GBuffer2, sampler_GBuffer2, uv);
    NormalVS = TransformWorldToViewNormal(NormalVS);
    NormalVS = normalize(NormalVS);

    float3 gi = 0;
    float weightSum = 0;

    [loop]
    for (int d = 0;d < _NumDirs; ++d)
    {

        float3 rayDirVS = GetSSGIDirectionVS(d, NormalVS);

        float baseStep = _MaxRayDistance / _NumSteps;

        UNITY_LOOP
        for (int s = 1; s <= _NumSteps; ++s)
        {

            float dist = s * baseStep;
            float3 samplePosVS = ViewPos + rayDirVS * dist;

            float4 clip = mul(_ProjMatrix, float4(samplePosVS.x, samplePosVS.y, samplePosVS.z, 1));
            float2 ndc = clip.xy / clip.w;
            float2 sampleUV = ndc * 0.5 + 0.5;

            if (sampleUV.x < 0 || sampleUV.x > 1 || sampleUV.y < 0 || sampleUV.y > 1) break;

            float sampleDepth = SampleSceneDepth(sampleUV);

            float3 sampleUVViewPos = ReconstructViewPos(sampleUV, sampleDepth);

            //两个负数
            if(sampleUVViewPos.z < samplePosVS.z)
            {
                float3 sampleColor = SampleSceneColor(sampleUV);

                float NDotL = saturate(dot(NormalVS, rayDirVS));

                //越远权重越小
                float distance = length(sampleUVViewPos - samplePosVS);
                float weight = NDotL * DistanceFalloff(distance);

                gi += sampleColor * weight;
                weightSum += weight;

                break;
            }

        }


    }

    return float4(gi / max(weightSum, 0.0001), 1);

}

float4 SampleSSGI(float2 uv)
{
    return SAMPLE_TEXTURE2D_X(_SSGIResultTex, sampler_LinearClamp, uv);
}

float4 SampleMainTex(float2 uv)
{
    return SAMPLE_TEXTURE2D_X(_SSGIBlurSource, sampler_SSGIBlurSource, uv);
}

//
float4 SSGI_BlurV(float2 uv)
{
    float4 sum = float4(0,0,0,0);

    float weights[5] = {0.204164, 0.304005, 0.093913, 0.023578, 0.003300};

    for (int i = -2; i <= 2; i++)
    {
        float2 offset = float2(0, i) * rcp(_GITexRes * _ScaledScreenParams.xy);
        sum += SampleMainTex(uv + offset) * weights[i+2];
    }

    return sum;
}

float4 SSGI_BlurH(float2 uv)
{
    float4 sum = float4(0,0,0,0);

    // 核大小 5，简单加权
    float weights[5] = {0.204164, 0.304005, 0.093913, 0.023578, 0.003300};

    for (int i = -2; i <= 2; i++)
    {
        float2 offset = float2(i, 0) * rcp(_GITexRes * _ScaledScreenParams.xy);
        sum += SampleMainTex(uv + offset) * weights[i+2];
    }

    return sum;
}