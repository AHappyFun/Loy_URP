#ifndef STANDARD_SHADOWPASS_INCLUDED
#define STANDARD_SHADOWPASS_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"


struct Attributes
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;
    float2 texcoord     : TEXCOORD0;

    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS   : SV_POSITION;
    float2 uv           : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// ShadowCaster 阶段由 SetupShadowCasterConstantBuffer 逐灯设置的光方向（面→光）。
// 不能用 _MainLightPosition：那是延迟/前向光照阶段才设置的全局，shadow caster 跑在它之前，
// 读到的是陈旧值，法线偏移方向错乱 → 阴影平坠(peter-panning)。
float3 _LightDirection;
float3 _LightPosition;



//------ShadowCaster--------
Varyings StandardShadowPassVertex(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);


    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
    positionCS = ApplyShadowClamping(positionCS);
    output.positionCS = positionCS;
    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);

    return output;
}

half4 StandardShadowPassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);


    half4 albedoAlpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
    half alpha = albedoAlpha.a;
#ifdef _ALPHATEST_ON
    clip(alpha - _Cutoff);
#endif

    return 0;
}


//------DepthOnly----------
// 注意：深度预通道不能加阴影偏移（ApplyShadowBias 只用于 ShadowCaster），否则预通道深度会偏移
Varyings StandardDepthOnlyVertex(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    return output;
}

half4 StandardDepthOnlyFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half4 albedoAlpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
    half alpha = albedoAlpha.a;
#ifdef _ALPHATEST_ON
    clip(alpha - _Cutoff);
#endif

    // 与 URP 标准 DepthOnly 一致：R 通道写深度（ColorMask R），供颜色纹理形式的 _CameraDepthTexture 使用
    return input.positionCS.z;
}

//------DepthNormals----------
struct DepthNormalsVaryings
{
    float4 positionCS   : SV_POSITION;
    float3 normalWS     : TEXCOORD0;
#if defined(_ALPHATEST_ON)
    float2 uv           : TEXCOORD1;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

DepthNormalsVaryings StandardDepthNormalsVertex(Attributes input)
{
    DepthNormalsVaryings output = (DepthNormalsVaryings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

#if defined(_ALPHATEST_ON)
    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
#endif
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    output.normalWS = NormalizeNormalPerVertex(TransformObjectToWorldNormal(input.normalOS));
    return output;
}

void StandardDepthNormalsFragment(
    DepthNormalsVaryings input
    , out half4 outNormalWS : SV_Target0
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

#if defined(_ALPHATEST_ON)
    half4 albedoAlpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
    clip(albedoAlpha.a - _Cutoff);
#endif

    // 与 GBuffer 通道保持一致的 Oct 编码，保证写进 GBuffer2 的法线能被延迟光照正确解码
#if defined(_GBUFFER_NORMALS_OCT)
    float3 normalWS = normalize(input.normalWS);
    float2 octNormalWS = PackNormalOctQuadEncode(normalWS);           // [-1, +1]
    float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);   // [0, 1]
    half3 packedNormalWS = PackFloat2To888(remappedOctNormalWS);      // [0, 1]
    outNormalWS = half4(packedNormalWS, 0.0);
#else
    float3 normalWS = NormalizeNormalPerPixel(input.normalWS);
    outNormalWS = half4(normalWS, 0.0);
#endif
}

#endif
