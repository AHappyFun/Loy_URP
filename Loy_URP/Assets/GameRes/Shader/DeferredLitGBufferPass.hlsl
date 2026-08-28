#ifndef LOY_LITGBUFFER_INCLUDED
#define LOY_LITGBUFFER_INCLUDED

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

    DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 1);

    float3 positionWS               : TEXCOORD2;
    float3 normalWS                 : TEXCOORD3;
    float4 tangentWS                : TEXCOORD4;    // xyz: tangent, w: sign
    float4 viewDirWS_fogFactor      : TEXCOORD5;
#ifdef USE_APV_PROBE_OCCLUSION
    float4 probeOcclusion           : TEXCOORD6;
#endif
    float4 positionCS               : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

//----------------
//SufaceData 目前使用金属流PBR
struct LoySurfaceData
{
    half3 albedo;
    half  metallic;
    half  smoothness;
    half3 normalTS;
    half3 emission;
    half  occlusion;
    half  alpha;
};

Varyings LitGBufferPassVert(Attributes input)
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

    OUTPUT_LIGHTMAP_UV(input.lightmapUV, unity_LightmapST, output.lightmapUV);
    OUTPUT_SH4(vertexInput.positionWS, output.normalWS.xyz,
        GetWorldSpaceNormalizeViewDir(vertexInput.positionWS), output.vertexSH, output.probeOcclusion);

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

inline void InitSurfaceData(float2 uv, out LoySurfaceData outSurface)
{
    //表面
    half4 albedoAlpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
    outSurface.alpha = albedoAlpha.a * _BaseColor.a;
#ifdef _ALPHATEST_ON
    clip(outSurface.alpha - _Cutoff);
 #endif
    outSurface.albedo = albedoAlpha.rgb * _BaseColor.rgb;

    //MOXS  金属度、AO、还没使用、光滑度
    half4 specGloss = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, uv);
    outSurface.metallic = specGloss.r * _Metallic;
    outSurface.smoothness = saturate(specGloss.a * _Smoothness);
    outSurface.occlusion = 1 * _Occlusion;//specGloss.b;

    //自发光
    //白天夜晚可以Fade 自发光强度
    half EmissionScale = _EmissionScale;
    outSurface.emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_BaseMap, uv).rgb * _EmissionColor.rgb * EmissionScale;

    #if defined(_NORMAL_MAP)
    //切线空间法线
    outSurface.normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv));
    #else
    outSurface.normalTS = 0;
    #endif
}

// 填充 URP 标准 InputData
inline void InitInputData(Varyings input, half3 normalTS, out InputData inputData)
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
    inputData.shadowMask = half4(0, 0, 0, 0);

    #if !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
        bakedGI = SAMPLE_GI(input.vertexSH,
            GetAbsolutePositionWS(inputData.positionWS),
            inputData.normalWS,
            inputData.viewDirectionWS,
            inputData.positionCS.xy,
            input.probeOcclusion,
            inputData.shadowMask);
    #elif defined(LIGHTMAP_ON)
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
    //有混合的情况
    half4 shadowMask = half4(0,0,0,0);

    #ifdef LIGHTMAP_ON
    shadowMask = SAMPLE_TEXTURE2D(unity_ShadowMask, samplerunity_ShadowMask, input.lightmapUV);
    #endif

    #if defined(LIGHTMAP_ON) || !(defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
    inputData.shadowMask = shadowMask;
    #endif

    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
}


GBufferFragOutput GetGBuffer(Varyings input, LoySurfaceData surfaceData, InputData inputData)
{
    // 使用 URP 标准 BRDF 初始化（金属流：specular 传 0）
    half alpha = surfaceData.alpha;
    BRDFData brdfData;
    InitializeBRDFData(surfaceData.albedo, surfaceData.metallic, half3(0, 0, 0), surfaceData.smoothness, alpha, brdfData);

    Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);

    half3 GIColor = GlobalIllumination(brdfData, (BRDFData)0, 0,
                                          inputData.bakedGI, surfaceData.occlusion, inputData.positionWS,
                                          inputData.normalWS, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);

    // URP 17 标准 GBuffer 打包：
    //  GBuffer0 = albedo + materialFlags
    //  GBuffer1 = reflectivity(金属流) + occlusion
    //  GBuffer2 = Oct 编码法线 + smoothness
    //  GBuffer3 = GI + emission（补上了之前丢失的自发光）
    GBufferFragOutput output = PackGBuffersBRDFData(brdfData, inputData, surfaceData.smoothness, surfaceData.emission + GIColor, surfaceData.occlusion);
#if defined(LOY_RENDER_DEBUG)
    output.color.rgb = LoyGetSurfaceDebugColor(surfaceData.albedo, surfaceData.emission, GIColor,
        inputData.normalWS, surfaceData.smoothness, surfaceData.metallic, surfaceData.occlusion);
#endif
    return output;
}

GBufferFragOutput LitGBufferPassFrag(Varyings input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    LoySurfaceData surfaceData;
    InitSurfaceData(input.uv, surfaceData);

    InputData inputData;
    InitInputData(input, surfaceData.normalTS, inputData);

    //Dbuffer Decal todo

    GBufferFragOutput gbuffer = GetGBuffer(input, surfaceData, inputData);

    return gbuffer;
}

#endif
