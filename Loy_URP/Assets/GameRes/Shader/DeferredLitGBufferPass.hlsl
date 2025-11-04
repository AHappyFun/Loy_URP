#ifndef LOY_LITGBUFFER_INCLUDED
#define LOY_LITGBUFFER_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Input.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/UnityGBuffer.hlsl"

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

struct LoyInputData
{
    float3  positionWS;
    float4  positionCS;
    float3  normalWS;
    half3   viewDirectionWS;
    float4  shadowCoord;
    half    fogCoord;
    half3   bakedGI;
    float2  normalizedScreenSpaceUV;
    half4   shadowMask;
    half3x3 tangentToWorld;
};

struct LoyFragmentOutput
{
    half4 GBuffer0 : SV_Target0;
    half4 GBuffer1 : SV_Target1;
    half4 GBuffer2 : SV_Target2;
    half4 GBuffer3 : SV_Target3; // Camera color attachment

    #ifdef GBUFFER_OPTIONAL_SLOT_1
    GBUFFER_OPTIONAL_SLOT_1_TYPE GBuffer4 : SV_Target4;
    #endif
    #ifdef GBUFFER_OPTIONAL_SLOT_2
    half4 GBuffer5 : SV_Target5;
    #endif
    #ifdef GBUFFER_OPTIONAL_SLOT_3
    half4 GBuffer6 : SV_Target6;
    #endif
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

inline void InitInputData(Varyings input, half3 normalTS, out LoyInputData inputData)
{
    inputData = (LoyInputData)0;

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

    //ShadowMap UV
#if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
#else
    inputData.shadowCoord = float4(0, 0, 0, 0);
#endif

    inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.viewDirWS_fogFactor.w);

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
        #ifdef LIGHTMAP_ON
            bakedGI = SampleSingleLightmap(TEXTURE2D_LIGHTMAP_ARGS(unity_Lightmap, samplerunity_Lightmap), input.lightmapUV, transformCoords, encodedLightmap, decodeInstructions);
        #endif
    #else
        bakedGI = SampleSHPixel(input.vertexSH,  inputData.normalWS);
    #endif

    inputData.bakedGI = bakedGI;

    //ShadowMask
    //有混合的情况
    half4 shadowMask = half4(0,0,0,0);

    #ifdef LIGHTMAP_ON
    shadowMask = SAMPLE_TEXTURE2D(unity_ShadowMask, samplerunity_ShadowMask, input.lightmapUV);
    #endif

    inputData.shadowMask = shadowMask;

    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

}


LoyFragmentOutput GetGBuffer(Varyings input, LoySurfaceData surfaceData, LoyInputData inputData)
{
    //Init Direct BRDF
    BRDFData brdfDataClearCoat = (BRDFData)0;
    BRDFData brdfData;
    half oneMinusReflectivity = kDielectricSpec.a - surfaceData.metallic * kDielectricSpec.a;
    brdfData.albedo = surfaceData.albedo;
    brdfData.diffuse = surfaceData.albedo * oneMinusReflectivity;
    brdfData.specular = lerp(kDielectricSpec.rgb, surfaceData.albedo, surfaceData.metallic);
    brdfData.reflectivity = 1.0 - oneMinusReflectivity;
    brdfData.perceptualRoughness = PerceptualSmoothnessToPerceptualRoughness(surfaceData.smoothness);
    brdfData.roughness = max(PerceptualRoughnessToRoughness(brdfData.perceptualRoughness), HALF_MIN_SQRT);
    brdfData.roughness2 = max(brdfData.roughness * brdfData.roughness, HALF_MIN);
    brdfData.grazingTerm = saturate(surfaceData.smoothness + brdfData.reflectivity);
    brdfData.normalizationTerm = brdfData.roughness * 4.0h + 2.0h;
    brdfData.roughness2MinusOne = brdfData.roughness2 - 1.0h;

    Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);

    half3 GIColor = GlobalIllumination(brdfData, brdfDataClearCoat, 0,
                                          inputData.bakedGI, surfaceData.occlusion, inputData.positionWS,
                                          inputData.normalWS, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);

    //合成Gbuffer
    LoyFragmentOutput output;

    half3 packedNormalWS = PackNormal(inputData.normalWS);

    uint materialFlags = 0;

    half3 packedMetallic;
    packedMetallic.r = brdfData.reflectivity;
    packedMetallic.g =  1 - surfaceData.smoothness;
    packedMetallic.b = surfaceData.metallic;

#if defined(LIGHTMAP_ON) && defined(_MIXED_LIGHTING_SUBTRACTIVE)
    materialFlags |= kMaterialFlagSubtractiveMixedLighting;
#endif

    output.GBuffer0 = half4(brdfData.albedo.rgb, PackMaterialFlags(materialFlags));
    output.GBuffer1 = half4(packedMetallic, surfaceData.occlusion);
    output.GBuffer2 = half4(packedNormalWS, surfaceData.smoothness);
    output.GBuffer3 = half4(GIColor, 1);
#if _RENDER_PASS_ENABLED
    output.GBuffer4 = inputData.positionCS.z;
#endif

    return output;
}

LoyFragmentOutput LitGBufferPassFrag(Varyings input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    LoySurfaceData surfaceData;
    InitSurfaceData(input.uv, surfaceData);

    LoyInputData inputData;
    InitInputData(input, surfaceData.normalTS, inputData);

    //Dbuffer Decal todo

    LoyFragmentOutput gbuffer = GetGBuffer(input, surfaceData, inputData);

    return gbuffer;
}

#endif