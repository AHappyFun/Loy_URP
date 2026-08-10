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
    // ComputeWorldSpacePosition 内部会自己转 NDC（uv*2-1），这里直接传 [0,1] 的 uv，
    // 不能提前转成 NDC，否则 uv 被二次变换，重建出的位置完全错误。
    float3 worldPos = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
    return mul(UNITY_MATRIX_V, float4(worldPos, 1.0)).xyz;
}

float Hash12(float2 p)
{
    // 高质量 hash（不需要贴图）
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
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

// AO 渲染目标尺寸，由 C# 端按实际创建的 RT 传入（不依赖 _ScaledScreenParams，
// 避免半分辨率 pass 里尺寸不确定导致 UV 对不上）
float4 _AOTexSize;  // x=宽, y=高, z=1/宽, w=1/高

float2 GetAOTexSize()
{
    return _AOTexSize.xy;
}

// 半分辨率 AO 时 depth 是全分辨率：AO 像素中心落在全分辨率 texel 边界上，
// point 采样在边界处会因浮点误差在相邻 texel 间抖动 → 竖/横线。
// 偏半个 texel 稳定对齐到 texel 中心（与 URP SSAO 的 ADJUSTED_DEPTH_UV 一致）。
float2 AdjustUvForDepth(float2 uv)
{
    return uv + (_CameraDepthTexture_TexelSize.xy * 0.5) * (1.0 - (_AOTexRes - 0.5) * 2.0);
}

//基础Raymarch步进版本。
//理解基础几何点的各个方向水平角计算。
//优化项：半分辨率
//这是个DDA的版本
float4 HBAORaymarch(float2 uv)
{
    // 半分辨率 AO：深度用全分辨率，需用 AdjustUvForDepth 对齐，否则出现横竖线
    float rawDepth = SampleSceneDepth(AdjustUvForDepth(uv));
//return rawDepth;
    // sky 检测：reversed-Z 下天空深度接近远平面（0），用远平面判断更可靠
    if (LinearEyeDepth(rawDepth, _ZBufferParams) >= _ProjectionParams.z * 0.99)
        return 1;

    float3 ViewPos = ReconstructViewPos(uv, rawDepth);
    
    //return float4(ViewPos, 1);

    // _GBuffer2 是世界空间法线，转成视图空间用于背向剔除（避免坐标系混用）
    // 法线编码由渲染器的 "Accurate G-Buffer Normals" 决定：
    //   关闭（默认）→ SNorm 直接存 [-1,1]，原样读取即可
    //   开启        → 八面体编码到 UNorm，需先解码
    float3 Normal;
#if defined(_GBUFFER_NORMALS_OCT)
    float3 packed = SAMPLE_TEXTURE2D(_GBuffer2, sampler_GBuffer2, uv).rgb;
    float2 oct = Unpack888ToFloat2(packed) * 2.0 - 1.0;
    Normal = UnpackNormalOctQuadEncode(oct);
#else
    Normal = SAMPLE_TEXTURE2D(_GBuffer2, sampler_GBuffer2, uv).rgb;
#endif
    float3 NormalVS = normalize(mul((float3x3)UNITY_MATRIX_V, Normal));

    // 均匀随机旋转方向，避免用 cos 值当角度造成方向偏差
    float randomAngle = Hash12(uv) * TWO_PI;

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

        // 步距按 _StepScale^(s-1) 指数放大，这里把基步长归一化，
        // 让最远采样点正好落在 _Radius，而不是被放大到 _Radius * StepScale^N / N。
        float baseStep = _Radius / pow(_StepScale, (float)(_NumSteps - 1));

        UNITY_LOOP
        for (int s = 1; s<=_NumSteps; ++s)
        {
            float t = pow(_StepScale, s - 1);
            float sampleDistance = baseStep * t;

            float2 sampleUV = uv + dir * (sampleDistance * uvPerViewUnit);

            if (sampleUV.x < 0 || sampleUV.x > 1 || sampleUV.y < 0 || sampleUV.y > 1) break;

            float3 sampleView = ReconstructViewPos(sampleUV, SampleSceneDepth(AdjustUvForDepth(sampleUV)));

            // 经典 HBAO：相对表面切线平面计算高度与横向距离
            // 平坦表面上所有采样点都在切线平面内（height≈0）→ 无 AO
            // 只有高于切线平面的遮挡物（墙/凸起）才贡献
            float3 delta = sampleView - ViewPos;
            float height = dot(delta, NormalVS) - _Bias;     // 高于切线平面的距离（减 bias 抑制自遮蔽）
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

// 最终 AO 结果（R 通道 = ao，1=无遮蔽，0=全遮蔽），供 ApplyToGI 采样
TEXTURE2D_X(_HBAOResultTex);
SAMPLER(sampler_HBAOResultTex);

//用双边滤波模糊，考虑几何深度。而不是直接高斯模糊
float4 HBAO_BlurV(float2 uv)
{
    float centerDepth = SampleSceneDepth(AdjustUvForDepth(uv));

    float sum = 0;
    float wsum = 0;
    int radius = (int)ceil(BlurRadius);

    for (int o = -radius; o <= radius ; o++)
    {
        float2 sampleUV = uv + float2(0, o * rcp(GetAOTexSize().y));
        float ao = SAMPLE_TEXTURE2D_X(_HBAOBlurSource, sampler_HBAOBlurSource, sampleUV);
        float sampleDepth = SampleSceneDepth(AdjustUvForDepth(sampleUV));
        float diff = abs(sampleDepth - centerDepth);
        float w = exp(-diff * 50) * exp(-abs(o) / BlurRadius);
        sum += ao * w;
        wsum += w;
    }
    return float4(sum / wsum, 0, 0, 1);
}

float4 HBAO_BlurH(float2 uv)
{
    float centerDepth = SampleSceneDepth(AdjustUvForDepth(uv));

    float sum = 0;
    float wsum = 0;
    int radius = (int)ceil(BlurRadius);

    for (int o = -radius; o <= radius ; o++)
    {
        float2 sampleUV = uv + float2(o * rcp(GetAOTexSize().x), 0);
        float ao = SAMPLE_TEXTURE2D_X(_HBAOBlurSource, sampler_HBAOBlurSource, sampleUV);
        float sampleDepth = SampleSceneDepth(AdjustUvForDepth(sampleUV));
        float diff = abs(sampleDepth - centerDepth);
        float w = exp(-diff * 50) * exp(-abs(o) / BlurRadius);
        sum += ao * w;
        wsum += w;
    }
    return float4(sum / wsum, 0, 0, 1);
}
