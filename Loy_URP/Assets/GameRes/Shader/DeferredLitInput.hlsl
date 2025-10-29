#ifndef LOY_DEFERRED_LITINPUT_INCLUDED
#define LOY_DEFERRED_LITINPUT_INCLUDED

//CBuffer里不加变体，不然SRP Batcher无法工作
CBUFFER_START(UnityPerMaterial)
    half _Cutoff;

    float4 _BaseMap_ST;
    half4 _BaseColor;
    half _Smoothness;
    half _Metallic;
    half _Occlusion;
    half4 _EmissionColor;
    half _EmissionScale;

CBUFFER_END

TEXTURE2D(_BaseMap);
TEXTURE2D(_EmissionMap);
SAMPLER(sampler_BaseMap);
TEXTURE2D(_MetallicGlossMap);
SAMPLER(sampler_MetallicGlossMap);
TEXTURE2D(_BumpMap);
SAMPLER(sampler_BumpMap);

#endif