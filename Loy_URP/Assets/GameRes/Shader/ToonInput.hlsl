#ifndef LOY_TOON_INPUT_INCLUDED
#define LOY_TOON_INPUT_INCLUDED

//CBuffer里不加变体，不然SRP Batcher无法工作
CBUFFER_START(UnityPerMaterial)
    half _Cutoff;

    float4 _BaseMap_ST;
    half4 _BaseColor;

    half _Metallic;
    half _Smoothness;
    half _Occlusion;

    // 写入独立 Toon CustomData GBuffer，不再占用 PBR metallic/smoothness 槽位。
    half _ToonDiffuseStep;
    half _ToonDiffuseSoftness;
    half _ToonSpecThreshold;
    half _ToonSpecSoftness;
    half _ToonGIStrength;

    half4 _EmissionColor;
    half _EmissionScale;

CBUFFER_END

TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);
TEXTURE2D(_MaskMap);
SAMPLER(sampler_MaskMap);
TEXTURE2D(_EmissionMap);
SAMPLER(sampler_EmissionMap);
TEXTURE2D(_BumpMap);
SAMPLER(sampler_BumpMap);

#endif
