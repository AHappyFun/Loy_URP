#ifndef LOY_TOON_INPUT_INCLUDED
#define LOY_TOON_INPUT_INCLUDED

//CBuffer里不加变体，不然SRP Batcher无法工作
CBUFFER_START(UnityPerMaterial)
    half _Cutoff;

    float4 _BaseMap_ST;
    half4 _BaseColor;

    // 卡通参数（写入 GBuffer1.gb / GBuffer2.a，由延迟光照阶段的 ToonDeferred.hlsl 消费）
    half _ToonDiffuseStep;      // 漫反射色阶阈值 0~1
    half _ToonSpecIntensity;    // 卡通高光强度
    half _ToonSpecularSize;     // 卡通高光大小
    half _ToonGIStrength;       // 间接光(GI)强度，用于保持色阶对比

    half _Occlusion;
    half4 _EmissionColor;
    half _EmissionScale;

CBUFFER_END

TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);
TEXTURE2D(_EmissionMap);
SAMPLER(sampler_EmissionMap);
TEXTURE2D(_BumpMap);
SAMPLER(sampler_BumpMap);

#endif
