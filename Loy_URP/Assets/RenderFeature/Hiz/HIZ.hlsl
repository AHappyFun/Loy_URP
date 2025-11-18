#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

TEXTURE2D(_HiZMip0);
TEXTURE2D(_HiZMip1);
TEXTURE2D(_HiZMip2);
TEXTURE2D(_HiZMip3);
TEXTURE2D(_HiZMip4);
TEXTURE2D(_HiZMip5);
TEXTURE2D(_HiZMip6);
TEXTURE2D(_HiZMip7);
SAMPLER(sampler_HiZMip0);
SAMPLER(sampler_HiZMip1);

int _HiZMipCount;

float SampleHIZ(float2 uv, int mip)
{
    float depth = 1.0;

    [branch]
    switch (mip)
    {
        case 0: depth = SAMPLE_TEXTURE2D(_HiZMip0, sampler_HiZMip0, uv).r; break;
        case 1: depth = SAMPLE_TEXTURE2D(_HiZMip1, sampler_HiZMip1, uv).r; break;
        case 2: depth = SAMPLE_TEXTURE2D(_HiZMip2, sampler_HiZMip0, uv).r; break;
        case 3: depth = SAMPLE_TEXTURE2D(_HiZMip3, sampler_HiZMip0, uv).r; break;
        case 4: depth = SAMPLE_TEXTURE2D(_HiZMip4, sampler_HiZMip0, uv).r; break;
        case 5: depth = SAMPLE_TEXTURE2D(_HiZMip5, sampler_HiZMip0, uv).r; break;
        case 6: depth = SAMPLE_TEXTURE2D(_HiZMip6, sampler_HiZMip0, uv).r; break;
        case 7: depth = SAMPLE_TEXTURE2D(_HiZMip7, sampler_HiZMip0, uv).r; break;
        default: depth = SAMPLE_TEXTURE2D(_HiZMip0, sampler_HiZMip0, uv).r; break;
    }

    return depth;
}