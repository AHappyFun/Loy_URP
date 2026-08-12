#ifndef LOY_GLITCH_INCLUDED
#define LOY_GLITCH_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

struct Attributes
{
    uint vertexID : SV_VertexID;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    UNITY_VERTEX_OUTPUT_STEREO
};

Varyings Vert(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
    output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
    return output;
}

TEXTURE2D_X(_InputTexture);

float4 _InputSize;
uint _Seed;
float _BlockStrength;
uint _BlockStride;
uint _BlockSeed1;
uint _BlockSeed2;
float2 _Drift;
float2 _Jitter;
float2 _Jump;
float _Shake;

float FRandom(uint seed)
{
    return GenerateHashedRandomFloat(seed);
}

float4 Frag(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    float2 uv = input.uv;
    uint2 resolution = max((uint2)_InputSize.xy, 1u);

    #if defined(GLITCH_BLOCK)
    const uint blockSize = 32;
    uint columns = max(1u, resolution.x / blockSize);
    uint2 blockXY = (uint2)(uv * resolution) / blockSize;
    uint block = blockXY.y * columns + blockXY.x;
    uint segment = block / max(1u, _BlockStride);

    float r1 = FRandom(block + _BlockSeed1);
    float r3 = FRandom(block / 3 + _BlockSeed2);
    uint selectedSeed = (r1 + r3) < 1 ? _BlockSeed1 : _BlockSeed2;
    float blockRandom = FRandom(segment + selectedSeed);

    block += (uint)(blockRandom * 20000) * (blockRandom < _BlockStrength);

    uint2 screenPosition = uint2(block % columns, block / columns) * blockSize;
    screenPosition += (uint2)(uv * resolution) % blockSize;
    uv = frac((screenPosition + 0.5) / resolution);
    #endif

    #if defined(GLITCH_BASIC)
    float tx = uv.x;
    float ty = uv.y;

    ty = lerp(ty, frac(ty + _Jump.x), _Jump.y);
    uint sy = min((uint)(ty * resolution.y), resolution.y - 1);

    float jitter = Hash(sy + _Seed) * 2 - 1;
    tx += jitter * (_Jitter.x < abs(jitter)) * _Jitter.y;
    tx = frac(tx + (Hash(_Seed) - 0.5) * _Shake);

    float drift = sin(ty * 2 + _Drift.x) * _Drift.y;
    uint sx1 = min((uint)(frac(tx) * resolution.x), resolution.x - 1);
    uint sx2 = min((uint)(frac(tx + drift) * resolution.x), resolution.x - 1);
    float4 c1 = LOAD_TEXTURE2D_X(_InputTexture, uint2(sx1, sy));
    float4 c2 = LOAD_TEXTURE2D_X(_InputTexture, uint2(sx2, sy));
    float4 color = float4(c1.r, c2.g, c1.b, c1.a);
    #else
    uint2 pixel = min((uint2)(uv * resolution), resolution - 1);
    float4 color = LOAD_TEXTURE2D_X(_InputTexture, pixel);
    #endif

    #if defined(GLITCH_BLOCK)
    if (frac(blockRandom * 1234) < _BlockStrength * 0.1)
    {
        float3 hsv = RgbToHsv(color.rgb);
        hsv = hsv * float3(-1, 1, 0) + float3(0.5, 0, 0.9);
        color.rgb = HsvToRgb(hsv);
    }
    #endif

    return color;
}

#endif
