#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
#include "Assets/RenderFeature/Hiz/HIZ.hlsl"

TEXTURE2D_X_HALF(_GBuffer2);
SAMPLER(sampler_GBuffer2);

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
    float4 clip = float4(uv * 2.0 - 1.0, rawDepth, 1.0);

    float4 view = mul(_InvProjMatrix, clip);
    view /= view.w;
    view.y *= -1;
    return view;
}

float Hash12(float2 p)
{
    // 高质量 hash（不需要贴图）
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// 生成一个 2D 随机方向，用于旋转 kernel
float2 RandomDir(float2 uv)
{
    float angle = Hash12(uv) * 6.28318530718; // 0~2PI
    return float2(cos(angle), sin(angle));
}



#define TWO_PI 6.28318530718
#define PI 3.1415926

float _AOIntensity;
float _Radius;
float _Bias;
int _NumDirs;
int _NumSteps;
float _StepScale;
float _AOTexRes;

#define BlurRadius 2

float2 GetAOTexSize()
{
    return _AOTexRes * _ScaledScreenParams.xy;
}

float4 HBAORaymarch(float2 uv)
{
    //如果使用半分辨率的AO，深度也需要用半分辨的。不然会出现横竖线。
    float rawDepth = SampleHIZ(uv, 1);
    if(rawDepth > 0.999f)
        return 1;

    float3 ViewPos = ReconstructViewPos(uv, rawDepth);

    float3 Normal = SAMPLE_TEXTURE2D(_GBuffer2, sampler_GBuffer2, uv);

    float2 rot = RandomDir(uv);
    float randomAngle = rot.x * TWO_PI;

    float occlusionAccum = 0;
    float weights = 0;

    [loop]
    for (int d = 0;d<_NumDirs; ++d)
    {
        float dirAngle = (d / (float)_NumDirs) * TWO_PI + randomAngle;
        float2 dir = float2(cos(dirAngle), sin(dirAngle));

        float maxSlope = -1e9;

        float baseStep = _Radius / _NumSteps;

        UNITY_LOOP
        for (int s = 1; s<=_NumSteps; ++s)
        {
            float t = pow(_StepScale, s - 1);
            float sampleDistance = baseStep * t;

            // convert a view-space lateral offset to uv offset approximately
            // approximate: view-space dx -> uv offset = dx * (proj.x / -viewPos.z) * 0.5 + 0.5 ???
            // We'll use screen-space approximation: scale by pixel size and a fudge factor
            float2 pixelStep = sampleDistance * rcp(GetAOTexSize().xy) * 1.0; // tuning factor = 1.0
            float2 sampleUV = uv + dir * pixelStep;

            if (sampleUV.x < 0 || sampleUV.x > 1 || sampleUV.y < 0 || sampleUV.y > 1) break;

            float3 sampleView = ReconstructViewPos(sampleUV, SampleHIZ(sampleUV, 1));

            float diff = -sampleView.z - (-ViewPos.z);

            float2 lateral = sampleView.xy - ViewPos.xy;
            float dist = length(lateral) + 1e-6;
            float slope = diff / dist;

            //得到最大坡度
            if (slope > maxSlope) maxSlope = slope;
        }

        //最大坡度有效
        if(maxSlope > -1e8)
        {
            float horizon = atan(maxSlope);

            //把(0,90°)转换到(0,1)，不过现在horizon是弧度的，所以除以 pi/2
            //θ = 0 → 完全无遮挡 → occl=0
            //θ = 45° → 遮掉半个半球 → occl = 0.5
            //θ = π/2 → 完全封顶 → occl=1
            float contribte = saturate(horizon * 2 / PI);

            //计算法线Dot，剔除背面三角形的影响。如果是背面三角形
            float2 dir2 = normalize(dir);
            float nl = saturate(dot(Normal.xy , dir2));

            contribte *= lerp(0.6, 1.0, nl);

            occlusionAccum += contribte;
            weights += 1.0;
        }
    }

    float ao = 1.0;
    if (weights > 0)
        ao = 1.0 - _AOIntensity * saturate(occlusionAccum / weights);
    ao = saturate(ao);

    return float4(ao, ao, ao, 1);
}


TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);

//用双边滤波模糊，考虑几何深度。而不是直接高斯模糊
float4 HBAO_BlurV(float2 uv)
{
    float centerDepth = SampleSceneDepth(uv);

    float sum = 0;
    float wsum = 0;
    int radius = (int)ceil(BlurRadius);

    for (int o = -radius; o <= radius ; o++)
    {
        float2 sampleUV = uv + float2(0, o * rcp(GetAOTexSize().y));
        float ao = SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, sampleUV);
        float sampleDepth = SampleSceneDepth(sampleUV);
        float diff = abs(sampleDepth - centerDepth);
        float w = exp(-diff * 50) * exp(-abs(o) / BlurRadius);
        sum += ao * w;
        wsum += w;
    }
    return float4(sum / wsum, 0, 0, 1);
}

float4 HBAO_BlurH(float2 uv)
{
    float centerDepth = SampleSceneDepth(uv);

    float sum = 0;
    float wsum = 0;
    int radius = (int)ceil(BlurRadius);

    for (int o = -radius; o <= radius ; o++)
    {
        float2 sampleUV = uv + float2(o * rcp(GetAOTexSize().x), 0);
        float ao = SAMPLE_TEXTURE2D_X(_MainTex,sampler_MainTex, sampleUV);
        float sampleDepth = SampleSceneDepth(sampleUV);
        float diff = abs(sampleDepth - centerDepth);
        float w = exp(-diff * 50) * exp(-abs(o) / BlurRadius);
        sum += ao * w;
        wsum += w;
    }
    return float4(sum / wsum, 0, 0, 1);
}
