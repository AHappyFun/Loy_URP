#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

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
    // URP 17 不设置 _InvProjMatrix/_ProjMatrix 全局，改用内置 UNITY_MATRIX_I_VP
    // 先重建世界位置，再变换到视图空间
    float3 worldPos = ComputeWorldSpacePosition(uv * 2.0 - 1.0, rawDepth, UNITY_MATRIX_I_VP);
    return mul(UNITY_MATRIX_V, float4(worldPos, 1.0)).xyz;
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

//基础Raymarch步进版本。
//理解基础几何点的各个方向水平角计算。
//优化项：半分辨率
//这是个DDA的版本
float4 HBAORaymarch(float2 uv)
{
    //如果使用半分辨率的AO，深度也需要用半分辨的。不然会出现横竖线。
    float rawDepth = SampleSceneDepth(uv);
//return rawDepth;
    // sky 检测：reversed-Z 下天空深度接近远平面（0），用远平面判断更可靠
    if (LinearEyeDepth(rawDepth, _ZBufferParams) >= _ProjectionParams.z * 0.99)
        return 1;

    float3 ViewPos = ReconstructViewPos(uv, rawDepth);
    
    //return float4(ViewPos, 1);

    // _GBuffer2 是世界空间法线，转成视图空间用于背向剔除（避免坐标系混用）
    float3 Normal = SAMPLE_TEXTURE2D(_GBuffer2, sampler_GBuffer2, uv);
    float3 NormalVS = normalize(mul((float3x3)UNITY_MATRIX_V, Normal));

    float2 rot = RandomDir(uv);
    float randomAngle = rot.x * TWO_PI;

    // 透视投影缩放：把 view 空间横向距离换算为 screen uv 偏移（用中心像素深度）
    // UNITY_MATRIX_P 是内置投影矩阵，[0][0]=f/aspect, [1][1]=f
    float viewDist = max(-ViewPos.z, 1e-4);
    float2 uvPerViewUnit = float2(UNITY_MATRIX_P[0][0], UNITY_MATRIX_P[1][1]) * rcp(viewDist) * 0.5;

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

            float2 sampleUV = uv + dir * (sampleDistance * uvPerViewUnit);

            if (sampleUV.x < 0 || sampleUV.x > 1 || sampleUV.y < 0 || sampleUV.y > 1) break;

            float3 sampleView = ReconstructViewPos(sampleUV, SampleSceneDepth(sampleUV));

            // 经典 HBAO：相对表面切线平面计算高度与横向距离
            // 平坦表面上所有采样点都在切线平面内（height≈0）→ 无 AO
            // 只有高于切线平面的遮挡物（墙/凸起）才贡献
            float3 delta = sampleView - ViewPos;
            float height = dot(delta, NormalVS);             // 高于切线平面的距离
            float3 tangentDelta = delta - NormalVS * height; // 切线平面内的横向分量
            float dist = length(tangentDelta) + 1e-6;
            float slope = height / dist;

            //得到最大坡度（即该方向的地平角）
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

            occlusionAccum += contribte;
            weights += 1.0;
        }
    }

    float ao = 1.0;
    if (weights > 0)
        ao = 1.0 - _AOIntensity * saturate(occlusionAccum / weights);
    ao = saturate(ao);

    // ===== 临时调试输出（R8 单通道）=====
    // 输出 AO 像素的 uv.x，验证 UV 映射（全屏应为 0→1 平滑渐变）
    //return float4(uv.x, uv.x, uv.x, 1);
    // ===== 调试结束 =====

    return float4(ao, ao, ao, 1);
}

//HIZ优化版本
float4 HBAORaymarchHIZ(float2 uv)
{

}

TEXTURE2D(_HBAOBlurSource);
SAMPLER(sampler_HBAOBlurSource);

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
        float ao = SAMPLE_TEXTURE2D_X(_HBAOBlurSource, sampler_HBAOBlurSource, sampleUV);
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
        float ao = SAMPLE_TEXTURE2D_X(_HBAOBlurSource, sampler_HBAOBlurSource, sampleUV);
        float sampleDepth = SampleSceneDepth(sampleUV);
        float diff = abs(sampleDepth - centerDepth);
        float w = exp(-diff * 50) * exp(-abs(o) / BlurRadius);
        sum += ao * w;
        wsum += w;
    }
    return float4(sum / wsum, 0, 0, 1);
}
