// ============================================================================
// Loy_URP GTAO (Ground Truth Ambient Occlusion)
// ----------------------------------------------------------------------------
// 相对本工程 HBAO 的进化（参考 Jimenez et al. GDC2016：
//   "Practical Real-Time Strategies for Accurate Indirect Occlusion"）
//   1. 解析积分：遮挡用"投影面积"定义（ground truth），
//      而不是 HBAO 的线性启发式映射 horizon*2/π
//   2. 少采样：解析积分下每方向 1~2 个 tap 就够，不需要 HBAO 的 8~12 步 raymarch
//   3. 多弹射近似（Multi-bounce）：被遮挡区域不是全黑，
//      遮挡几何会按反照率把光反射回来（带颜色渗漏）
// ============================================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

// GTAO 多弹射需要反照率（GBuffer0 = albedo）
TEXTURE2D_X_HALF(_GBuffer0);
SAMPLER(sampler_GBuffer0);

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
    // 与 HBAO 相同：URP 17 用内置 UNITY_MATRIX_I_VP 重建世界位置再转视图空间
    float3 worldPos = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
    return mul(UNITY_MATRIX_V, float4(worldPos, 1.0)).xyz;
}

// 白噪声（与本工程 HBAO 一致）
// 之前尝试过 Interleaved Gradient Noise(IGN)：在 TAA 收敛的 Game 视图它是更优选择，
// 但在无时域收敛的视图（Scene 视图 / 刚切完设置时 TAA history 重置的十几帧），
// IGN 会露出固有的斜向条纹特征，比白噪声的随机颗粒更扎眼。
// 白噪声是随机颗粒，经双边模糊后混成均匀灰，视觉上更"安静"。
// 想用 IGN（配合 TAA 更干净）：把下面 randomAngle 换成
//   InterleavedGradientNoise(uv * GetAOTexSize()) * TWO_PI
float Hash12(float2 p)
{
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
float _MultiBounce;   // 多弹射强度 [0,1]，0=关闭。完整 GTAO 是逐通道彩色，这里是标量版

#define BlurRadius 2   // 和 HBAO 一致：TAA 已做时域平滑，空间模糊不用过重，保留 AO 细节

// AO 渲染目标尺寸，由 C# 端按实际创建的 RT 传入（不依赖 _ScaledScreenParams）
float4 _AOTexSize;  // x=宽, y=高, z=1/宽, w=1/高

float2 GetAOTexSize()
{
    return _AOTexSize.xy;
}

float2 AdjustUvForDepth(float2 uv)
{
    // 半分辨率 AO 时 depth 是全分辨率：AO 像素中心落在全分辨率 texel 边界上，
    // point 采样在边界处会因浮点误差在相邻 texel 间抖动 → 竖/横线。
    // 偏半个 texel 稳定对齐到 texel 中心（与 URP SSAO 的 ADJUSTED_DEPTH_UV 一致）。
    return uv + (_CameraDepthTexture_TexelSize.xy * 0.5) * (1.0 - (_AOTexRes - 0.5) * 2.0);
}

// ============================================================================
// GTAO 核心
// ============================================================================
// 几何准备与 HBAO 完全相同（深度重建、法线读取、方向旋转、透视缩放），
// 与 HBAO 的关键区别在"地平角→遮挡"的积分和每方向的采样数。
// ============================================================================
float4 GTAORaymarch(float2 uv)
{
    float rawDepth = SampleSceneDepth(AdjustUvForDepth(uv));
    // sky 检测：reversed-Z 下天空深度接近远平面（0），用远平面判断更可靠
    if (LinearEyeDepth(rawDepth, _ZBufferParams) >= _ProjectionParams.z * 0.99)
        return 1;

    float3 ViewPos = ReconstructViewPos(uv, rawDepth);

    // _GBuffer2 是世界空间法线，转成视图空间用于背向剔除
    // 法线编码由 "Accurate G-Buffer Normals" 决定：关闭→SNorm 直接读；开启→八面体解码
    float3 Normal;
#if defined(_GBUFFER_NORMALS_OCT)
    float3 packed = SAMPLE_TEXTURE2D(_GBuffer2, sampler_GBuffer2, uv).rgb;
    float2 oct = Unpack888ToFloat2(packed) * 2.0 - 1.0;
    Normal = UnpackNormalOctQuadEncode(oct);
#else
    Normal = SAMPLE_TEXTURE2D(_GBuffer2, sampler_GBuffer2, uv).rgb;
#endif
    float3 NormalVS = normalize(mul((float3x3)UNITY_MATRIX_V, Normal));

    // 均匀随机旋转方向，避免用 cos 值当角度造成方向偏差（白噪声，与 HBAO 一致）
    float randomAngle = Hash12(uv) * TWO_PI;

    // 透视投影缩放：把 view 空间横向距离换算为 screen uv 偏移
    float viewDist = max(-ViewPos.z, 1e-4);
    float2 uvPerViewUnit = float2(UNITY_MATRIX_P[0][0], UNITY_MATRIX_P[1][1]) * rcp(viewDist) * 0.5;

    float occlusionAccum = 0;
    float weights = 0;

    [loop]
    for (int d = 0; d < _NumDirs; ++d)
    {
        float dirAngle = (d / (float)_NumDirs) * TWO_PI + randomAngle;
        float2 dir = float2(cos(dirAngle), sin(dirAngle));

        float maxSlope = -1e9;

        // GTAO 每方向只需很少的采样（参考实现每方向 2 个 tap）：
        // 解析积分让"地平角→遮挡"是精确的，多余采样只用于更精确地找地平角，
        // 边际收益递减。这里保留 _NumSteps 步进循环方便和 HBAO 对比，默认 2~3 步。
        float baseStep = _Radius / pow(_StepScale, (float)(_NumSteps - 1));

        UNITY_LOOP
        for (int s = 1; s <= _NumSteps; ++s)
        {
            float t = pow(_StepScale, s - 1);
            float sampleDistance = baseStep * t;

            float2 sampleUV = uv + dir * (sampleDistance * uvPerViewUnit);

            if (sampleUV.x < 0 || sampleUV.x > 1 || sampleUV.y < 0 || sampleUV.y > 1) break;

            float3 sampleView = ReconstructViewPos(sampleUV, SampleSceneDepth(AdjustUvForDepth(sampleUV)));

            // 高度/横向距离 → 相对表面切平面的斜率（与 HBAO 相同的几何）：
            // 平坦表面上所有采样点都在切平面内（height≈0）→ 无 AO
            // 只有高于切平面的遮挡物（墙/凸起）才贡献
            float3 delta = sampleView - ViewPos;
            float height = dot(delta, NormalVS) - _Bias;     // 高于切平面的距离（减 bias 抑制自遮蔽）
            float3 tangentDelta = delta - NormalVS * height; // 切平面内的横向分量
            float dist = length(tangentDelta) + 1e-6;
            float slope = height / dist;

            if (slope > maxSlope) maxSlope = slope;          // 得到最大斜率（即该方向的地平角）
        }

        if (maxSlope > -1e8)
        {
            float horizon = atan(maxSlope);

            // ==================================================================
            // GTAO 与 HBAO 的核心区别：解析积分（ground truth）
            // ==================================================================
            // 投影面积定义下的真实 AO：
            //   AO = (1/π)·∫∫ max(0, ω·N)·V(ω) dω
            // 固定方位角 φ 的方向切片里，可见天顶角范围是 [θ_h, π/2]
            // （θ 从表面切平面起算，θ_h 就是上面求出的地平角），
            // 立体角元 dω = cosθ·dθ·dφ，权重 ω·N = sinθ，因此切片贡献：
            //   (1/π)·∫_{θ_h}^{π/2} sinθ·cosθ dθ = cos²θ_h / (2π)
            // 全开（θ_h=0）时为 1/(2π)，归一化后该方向 AO_dir = cos²θ_h
            // 于是每方向"遮挡量" occl = 1 − cos²θ_h = sin²θ_h
            //
            // 对比 HBAO 的线性映射 horizon·(2/π)：
            //   * 小地平角（远处贴地平线的遮挡物）时 sin²θ 趋近 0 → 几乎不产生 AO，
            //     这正是 GTAO 消 halo、消"远景整体变暗"的原因
            //   * 45° 时两者都等于 0.5，90° 都等于 1（完全封顶）——大角部分一致
            // ==================================================================
            float sinH = sin(horizon);
            float occl = saturate(sinH * sinH);

            occlusionAccum += occl;
            weights += 1.0;
        }
    }

    float ao = 1.0;
    if (weights > 0)
        ao = 1.0 - _AOIntensity * saturate(occlusionAccum / weights);
    ao = saturate(ao);

    // ==========================================================================
    // GTAO 多弹射近似（Multi-bounce）
    // ==========================================================================
    // 被遮挡的方向并不是全黑：遮挡几何会以反照率 a 把光反射回接收点。
    // 光在遮挡几何与接收点之间的往返按几何级数衰减：
    //   有效光 = AO·[1 + a(1−AO) + a²(1−AO)² + ...] = AO / (1 − a·(1−AO))
    //   * 白表面（a≈1）几乎不被暗化（遮挡被反弹光抵消）
    //   * 黑表面（a≈0）退化为原始 AO
    // 完整 GTAO 是逐通道的：AO 乘反照率颜色 → 产生色彩渗漏（红地毯暗部偏红）。
    // 本工程 ApplyToGI 用 R8 标量管线，这里用反照率亮度做标量近似。
    // ==========================================================================
    if (_MultiBounce > 0.0)
    {
        float3 albedo = SAMPLE_TEXTURE2D(_GBuffer0, sampler_GBuffer0, uv).rgb;
        float albedoLum = dot(albedo, float3(0.2126, 0.7152, 0.0722));
        ao = ao / max(1e-4, 1.0 - _MultiBounce * (1.0 - ao) * albedoLum);
        ao = saturate(ao);
    }

    return float4(ao, ao, ao, 1);
}

// ============================================================================
// 双边模糊（与 HBAO 相同，考虑几何深度而非纯高斯）
// ============================================================================
TEXTURE2D(_GTAOBlurSource);
SAMPLER(sampler_GTAOBlurSource);

// 最终 AO 结果（R 通道 = ao，1=无遮蔽，0=全遮蔽），供 ApplyToGI 采样
TEXTURE2D_X(_GTAOResultTex);
SAMPLER(sampler_GTAOResultTex);

float4 GTAO_BlurV(float2 uv)
{
    float centerDepth = SampleSceneDepth(AdjustUvForDepth(uv));

    float sum = 0;
    float wsum = 0;
    int radius = (int)ceil(BlurRadius);

    for (int o = -radius; o <= radius; o++)
    {
        float2 sampleUV = uv + float2(0, o * rcp(GetAOTexSize().y));
        float ao = SAMPLE_TEXTURE2D_X(_GTAOBlurSource, sampler_GTAOBlurSource, sampleUV);
        float sampleDepth = SampleSceneDepth(AdjustUvForDepth(sampleUV));
        // 相对深度差（除以中心深度）比绝对 eye-depth 稳：
        // 绝对差会把远处真实边缘（深度差小）当噪声糊掉，把近处噪声（深度差大）当边缘挡住
        float diff = abs(sampleDepth - centerDepth) / max(abs(centerDepth), 1e-4);
        float w = exp(-diff * 10) * exp(-abs(o) / BlurRadius);
        sum += ao * w;
        wsum += w;
    }
    return float4(sum / wsum, 0, 0, 1);
}

float4 GTAO_BlurH(float2 uv)
{
    float centerDepth = SampleSceneDepth(AdjustUvForDepth(uv));

    float sum = 0;
    float wsum = 0;
    int radius = (int)ceil(BlurRadius);

    for (int o = -radius; o <= radius; o++)
    {
        float2 sampleUV = uv + float2(o * rcp(GetAOTexSize().x), 0);
        float ao = SAMPLE_TEXTURE2D_X(_GTAOBlurSource, sampler_GTAOBlurSource, sampleUV);
        float sampleDepth = SampleSceneDepth(AdjustUvForDepth(sampleUV));
        // 相对深度差（除以中心深度）比绝对 eye-depth 稳：
        // 绝对差会把远处真实边缘（深度差小）当噪声糊掉，把近处噪声（深度差大）当边缘挡住
        float diff = abs(sampleDepth - centerDepth) / max(abs(centerDepth), 1e-4);
        float w = exp(-diff * 10) * exp(-abs(o) / BlurRadius);
        sum += ao * w;
        wsum += w;
    }
    return float4(sum / wsum, 0, 0, 1);
}
