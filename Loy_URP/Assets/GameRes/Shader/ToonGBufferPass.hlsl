#ifndef LOY_TOON_GBUFFER_INCLUDED
#define LOY_TOON_GBUFFER_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Input.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutput.hlsl"

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
    half  toonDiffuseStep;      // 漫反射色阶阈值
    half  toonSpecIntensity;    // 卡通高光强度
    half  toonSpecularSize;     // 卡通高光大小
    half  toonGIStrength;       // 间接光强度
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

    //卡通参数
    outSurface.toonDiffuseStep   = _ToonDiffuseStep;
    outSurface.toonSpecIntensity = _ToonSpecIntensity;
    outSurface.toonSpecularSize  = _ToonSpecularSize;
    outSurface.toonGIStrength    = _ToonGIStrength;
    outSurface.occlusion = _Occlusion;

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
    // 卡通材质：金属度 0，光滑度只在 GI 计算里占位
    half alpha = surfaceData.alpha;
    BRDFData brdfData;
    InitializeBRDFData(surfaceData.albedo, 0, half3(0, 0, 0), surfaceData.toonSpecularSize, alpha, brdfData);

    Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);

    half3 GIColor = GlobalIllumination(brdfData, (BRDFData)0, 0,
                                          inputData.bakedGI, surfaceData.occlusion, inputData.positionWS,
                                          inputData.normalWS, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);
    // 卡通通常需要压低间接光保持色阶对比
    GIColor *= surfaceData.toonGIStrength;

    // URP 17 标准 GBuffer 打包（卡通版）：
    //  GBuffer0 = albedo + materialFlags(kMaterialFlagToon)
    //  GBuffer1 = reflectivity(0) + 漫反射色阶阈值 + 高光强度 + occlusion
    //  GBuffer2 = Oct 编码法线 + 卡通高光大小
    //  GBuffer3 = GI(*强度) + emission
    GBufferFragOutput output;
    uint materialFlags = kMaterialFlagToon;

    output.gBuffer0 = half4(surfaceData.albedo, PackGBufferMaterialFlags(materialFlags));
    output.gBuffer1 = half4(0.0, surfaceData.toonDiffuseStep, surfaceData.toonSpecIntensity, surfaceData.occlusion);
    output.gBuffer2 = half4(PackGBufferNormal(inputData.normalWS), surfaceData.toonSpecularSize);
    output.color = half4(GIColor + surfaceData.emission, 1.0);

#if defined(GBUFFER_FEATURE_DEPTH)
    output.depth = inputData.positionCS.z;
#endif
#if defined(GBUFFER_FEATURE_SHADOWMASK)
    output.shadowMask = inputData.shadowMask;
#endif
#if defined(GBUFFER_FEATURE_RENDERING_LAYERS)
    output.meshRenderingLayers = EncodeMeshRenderingLayer();
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
