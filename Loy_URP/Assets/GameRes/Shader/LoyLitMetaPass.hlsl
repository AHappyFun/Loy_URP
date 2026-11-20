#ifndef LOY_LIT_META_PASS_INCLUDED
#define LOY_LIT_META_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/UniversalMetaPass.hlsl"

half4 LoyFragmentMeta(Varyings input) : SV_Target
{
    half4 baseColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
#if defined(_ALPHATEST_ON)
    clip(baseColor.a - _Cutoff);
#endif
    half4 mask = SAMPLE_TEXTURE2D(LOY_META_MASK_MAP, LOY_META_MASK_SAMPLER, input.uv);
    half metallic = saturate(mask.r * _Metallic);
    half smoothness = saturate(mask.a * _Smoothness);
    half alpha = baseColor.a;
    BRDFData brdfData;
    InitializeBRDFData(baseColor.rgb, metallic, half3(0, 0, 0), smoothness, alpha, brdfData);

    MetaInput metaInput = (MetaInput)0;
    metaInput.Albedo = brdfData.diffuse + brdfData.specular * brdfData.roughness * 0.5h;
    metaInput.Emission = SAMPLE_TEXTURE2D(_EmissionMap, LOY_META_EMISSION_SAMPLER, input.uv).rgb
        * _EmissionColor.rgb * _EmissionScale;
    return UniversalFragmentMeta(input, metaInput);
}

#endif
