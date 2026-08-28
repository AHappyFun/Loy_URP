#ifndef LOY_TOON_GBUFFER_INCLUDED
#define LOY_TOON_GBUFFER_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Input.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutput.hlsl"
#include "LoyRenderDebug.hlsl"

struct Attributes
{
    float4 positionOS    : POSITION;
    float3 normalOS      : NORMAL;
    float4 tangentOS     : TANGENT;
    float2 texcoord      : TEXCOORD0;
    float2 lightmapUV    : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float2 uv                       : TEXCOORD0;

#if LIGHTMAP_ON
    float2 lightmapUV               : TEXCOORD1;
#else
    half3 vertexSH                  : TEXCOORD1;
#endif

    float3 positionWS               : TEXCOORD2;
    float3 normalWS                 : TEXCOORD3;
    float4 tangentWS                : TEXCOORD4;    // xyz: tangent, w: sign
    float4 viewDirWS_fogFactor      : TEXCOORD5;
    float4 positionCS               : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

//----------------
// 卡通材质表面数据
struct ToonSurfaceData
{
    half3 albedo;
    half3 normalTS;
    half3 emission;
    half  occlusion;
    half  alpha;
    half  metallic;
    half  smoothness;
    half4 toonCustomData;
    half  toonGIStrength;
};

Varyings ToonGBufferPassVert(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    output.positionWS = vertexInput.positionWS;
    output.normalWS = normalInput.normalWS;

#ifdef LIGHTMAP_ON
    output.lightmapUV.xy = input.lightmapUV.xy * unity_LightmapST.xy + unity_LightmapST.zw;
#else
    output.vertexSH.xyz = SampleSHVertex(output.normalWS.xyz);
#endif

    real sign = input.tangentOS.w * GetOddNegativeScale();
    output.tangentWS = half4(normalInput.tangentWS.xyz, sign);
    output.viewDirWS_fogFactor.xyz = GetWorldSpaceViewDir(vertexInput.positionWS);

    half fogFactor = 0;
#if !defined(_FOG_FRAGMENT)
    fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
#endif
    output.viewDirWS_fogFactor.w = fogFactor;

    output.positionCS = vertexInput.positionCS;

    return output;
}

inline void InitToonSurfaceData(float2 uv, out ToonSurfaceData outSurface)
{
    //表面
    half4 albedoAlpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
    outSurface.alpha = albedoAlpha.a * _BaseColor.a;
#ifdef _ALPHATEST_ON
    clip(outSurface.alpha - _Cutoff);
 #endif
    outSurface.albedo = albedoAlpha.rgb * _BaseColor.rgb;

    // 标准 Metallic PBR 数据。MaskMap: R=Metallic, G=AO, A=Smoothness。
    half4 mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, uv);
    outSurface.metallic = saturate(mask.r * _Metallic);
    outSurface.smoothness = saturate(mask.a * _Smoothness);
    outSurface.occlusion = saturate(mask.g * _Occlusion);

    // UE 风格的 shading-model-specific CustomData。
    outSurface.toonCustomData = half4(
        _ToonDiffuseStep,
        _ToonDiffuseSoftness,
        _ToonSpecThreshold,
        _ToonSpecSoftness);
    outSurface.toonGIStrength = _ToonGIStrength;

    //自发光
    outSurface.emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, uv).rgb * _EmissionColor.rgb * _EmissionScale;

    #if defined(_NORMAL_MAP)
    //切线空间法线
    outSurface.normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv));
    #else
    outSurface.normalTS = 0;
    #endif
}

// 填充 URP 标准 InputData
inline void InitToonInputData(Varyings input, half3 normalTS, out InputData inputData)
{
    inputData = (InputData)0;

    inputData.positionWS = input.positionWS;
    inputData.positionCS = input.positionCS;
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    inputData.viewDirectionWS = viewDirWS;

#if defined(_NORMAL_MAP)
    float sgn = input.tangentWS.w;      // should be either +1 or -1
    float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
    half3x3 tangentToWorld = half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz);
    inputData.tangentToWorld = tangentToWorld;
    inputData.normalWS = TransformTangentToWorld(normalTS, tangentToWorld);
    inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
#else
    inputData.normalWS = normalize(input.normalWS);
#endif

    //ShadowMap UV（实时阴影在延迟光照阶段计算，这里只用于 GI 混合）
#if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
#else
    inputData.shadowCoord = float4(0, 0, 0, 0);
#endif

    inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.viewDirWS_fogFactor.w);
    inputData.vertexLighting = half3(0, 0, 0);

    //BakedGI
    half3 bakedGI = half3(0,0,0);

    #if defined(LIGHTMAP_ON)
        #ifdef UNITY_LIGHTMAP_FULL_HDR
            bool encodedLightmap = false;
        #else
            bool encodedLightmap = true;
        #endif
            half4 decodeInstructions = half4(LIGHTMAP_HDR_MULTIPLIER, LIGHTMAP_HDR_EXPONENT, 0.0h, 0.0h);
            half4 transformCoords = half4(1, 1, 0, 0);
            bakedGI = SampleSingleLightmap(TEXTURE2D_LIGHTMAP_ARGS(unity_Lightmap, samplerunity_Lightmap), input.lightmapUV, transformCoords, encodedLightmap, decodeInstructions);
    #else
        bakedGI = SampleSHPixel(input.vertexSH, inputData.normalWS);
    #endif

    inputData.bakedGI = bakedGI;

    //ShadowMask
    half4 shadowMask = half4(0,0,0,0);

    #ifdef LIGHTMAP_ON
    shadowMask = SAMPLE_TEXTURE2D(unity_ShadowMask, samplerunity_ShadowMask, input.lightmapUV);
    #endif

    inputData.shadowMask = shadowMask;

    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
}


GBufferFragOutput GetToonGBuffer(Varyings input, ToonSurfaceData surfaceData, InputData inputData)
{
    // 保留完整的 Metallic PBR BRDF；卡通参数单独写入 CustomData。
    half alpha = surfaceData.alpha;
    BRDFData brdfData;
    InitializeBRDFData(surfaceData.albedo, surfaceData.metallic, half3(0, 0, 0), surfaceData.smoothness, alpha, brdfData);

    Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);

    half3 GIColor = GlobalIllumination(brdfData, (BRDFData)0, 0,
                                          inputData.bakedGI, surfaceData.occlusion, inputData.positionWS,
                                          inputData.normalWS, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);
    // 卡通通常需要压低间接光保持色阶对比
    GIColor *= surfaceData.toonGIStrength;

    GBufferFragOutput output = PackGBuffersBRDFData(
        brdfData, inputData, surfaceData.smoothness,
        GIColor + surfaceData.emission, surfaceData.occlusion);

    uint materialFlags = UnpackGBufferMaterialFlags(output.gBuffer0.a) | kMaterialFlagToon;
    output.gBuffer0.a = PackGBufferMaterialFlags(materialFlags);
    output.customData = surfaceData.toonCustomData;

#if defined(LOY_RENDER_DEBUG)
    output.color.rgb = LoyGetSurfaceDebugColor(surfaceData.albedo, surfaceData.emission, GIColor,
        inputData.normalWS, surfaceData.smoothness, surfaceData.metallic, surfaceData.occlusion);
#endif

    return output;
}

GBufferFragOutput ToonGBufferPassFrag(Varyings input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    ToonSurfaceData surfaceData;
    InitToonSurfaceData(input.uv, surfaceData);

    InputData inputData;
    InitToonInputData(input, surfaceData.normalTS, inputData);

    //Dbuffer Decal todo

    GBufferFragOutput gbuffer = GetToonGBuffer(input, surfaceData, inputData);

    return gbuffer;
}

#endif
