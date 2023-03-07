#ifndef Loy_OCEAN_INCLUDED
#define Loy_OCEAN_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

#include "Assets/Effect/Ocean/GerstnerWaves.hlsl"
#include "Assets/Effect/Ocean/CommonUtilities.hlsl"

#include "Assets/Effect/Ocean/Aley_WaterFunc.hlsl"

//TEXTURE2D(_BaseMap);
//SAMPLER(sampler_BaseMap);
TEXTURE2D(_NormalMap);
SAMPLER(sampler_NormalMap);

SAMPLER(sampler_ScreenTextures_linear_clamp);
#if defined(_REFLECTION_PLANARREFLECTION)
    TEXTURE2D(_PlanarReflectionTexture);
#endif

#if defined(_SSRREFLECT)
    TEXTURE2D(_ScreenSpaceReflectMap);
#endif

TEXTURE2D(_AbsorptionScatteringLUT);
SAMPLER(sampler_AbsorptionScatteringLUT);

TEXTURE2D(_WaterFXMap);
TEXTURE2D(_CameraOpaqueTexture);
SAMPLER(sampler_CameraOpaqueTexture_linear_clamp);

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    float4 _NormalMap_ST;

//Sine Gerstner
    float _AY;
    float4 _SineParam;

    float4 _GerstnerAParam;
    float4 _GerstnerBParam;
    float4 _GerstnerCParam;
    float4 _GerstnerDParam;
    float4 _GerstnerEParam;
    float4 _GerstnerFParam;

    float _MAXHeight;

    //深浅
    float _DepthScale;
    float _DeepCurve;
    float4 _DeepColor;
    float4 _ShallowColor;

    //
    float _NormalScale;

    //曲面细分
    float _MinDist;
    float _MaxDist;
    float _Tessellation;

CBUFFER_END

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float4 tangentOS : TANGENT;
    float2 uv0 : TEXCOORD0;
};

struct Varyings
{
    float2 uv0 : TEXCOORD0;
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD1;
    float3 normalWS : TEXCOORD2;
    float4 tangentWS : TEXCOORD3;
    float3 viewDirWS : TEXCOORD4;
    float2 positionSS : TEXCOORD5;
    float2 positionNDC : TEXCOORD6;
    float3 bitangentWS : TEXCOORD7;
    float4 normalUV : TEXCOORD8;
    float4 addData : TEXCOORD9;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

struct TessVertex
{
    float4 positionOS : INTERNALTESSPOS;
    float3 normalOS : NORMAL;
    float4 tangentOS : TANGENT;
    float2 uv0 : TEXCOORD0;
};

TessVertex tessVert(Attributes v)
{
    TessVertex o;
    o.positionOS = v.positionOS;
    o.tangentOS = v.tangentOS;
    o.normalOS = v.normalOS;
    o.uv0 = v.uv0;
    return o;
}

struct OutputPatchConstant {
    float edge[3] : SV_TESSFACTOR;
    float inside : SV_INSIDETESSFACTOR;
};

Varyings OceanVert(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    output.positionWS = mul(unity_ObjectToWorld, input.positionOS);
    
    float noiseFactor = ((noise((output.positionWS.xz * 0.5) + _Time.y) + noise((output.positionWS.xz * 1) + _Time.y)) * 0.25 - 0.5) + 1;
    // Detail UVs
    output.normalUV.zw = output.positionWS.xz * 0.07h - _Time.y * 0.05h + (noiseFactor* 0.1);
    output.normalUV.xy = output.positionWS.xz * .28h + _Time.y.xx * 0.1h + (noiseFactor* 0.2);

    //顶点波形 
    float3 p = input.positionOS.xyz;
    #if _SIN_WAVE
    //1.Sin
        float k = 2 * PI / _SineParam.w;
        float2 offset = k * (p.xz * float2(_SineParam.x, _SineParam.y) - _SineParam.z * _Time.y);
  
        p.y += _AY * sin(offset.x);
        p.y += _AY * sin(offset.y);

        //修正法线
        input.tangentOS.xyz = normalize(float3(1, k * _AY * cos(offset.x), 0));
        float3 biTangent = normalize(float3(0, k * _AY * cos(offset.y), 1));
        input.normalOS = normalize( cross(biTangent, input.tangentOS));
    
        input.positionOS.xyz = p.xyz;
    
    #elif _GERSTNER_WAVE
    //2.Gerstner
        float3 biTangent = float3(0,0,1);
        float3 tangent = float3(1,0,0);
        float3 wavePos = float3(0,0,0);
        wavePos += GerstnerWave(_GerstnerAParam, p, biTangent, tangent);
        wavePos += GerstnerWave(_GerstnerBParam, p, biTangent, tangent);
        wavePos += GerstnerWave(_GerstnerCParam, p, biTangent, tangent);
        //wavePos += GerstnerWave(_GerstnerDParam, p, biTangent, tangent);
        //wavePos += GerstnerWave(_GerstnerEParam, p, biTangent, tangent);
        //wavePos += GerstnerWave(_GerstnerFParam, p, biTangent, tangent);

        input.positionOS.xyz = wavePos.xyz;
        input.normalOS = normalize( cross(biTangent, tangent));

        //output.addData.z = (wavePos.y) / _MAXHeight * 0.5 + 0.5;

    #endif      

    
    //shader中常用的坐标转换中间类 
    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);   
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);
    

    //output的数据
    output.uv0 = TRANSFORM_TEX(input.uv0, _BaseMap);
    output.normalWS = normalInput.normalWS;
    
    //#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR) || defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    //    real sign = input.tangentOS.w * GetOddNegativeScale();
    //    half4 tangentWS = half4(normalInput.tangentWS.xyz, sign);
    //#endif
    //#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    //    output.tangentWS = tangentWS;
    //#endif
    
    //output.tangentWS.xyz = normalInput.tangentWS.xyz;
    //output.bitangentWS = normalInput.bitangentWS;

    output.positionWS =  vertexInput.positionWS;
    output.viewDirWS = GetWorldSpaceNormalizeViewDir(vertexInput.positionWS);
    output.positionCS = vertexInput.positionCS;
    output.positionNDC = vertexInput.positionNDC;
    
    half distanceBlend = smoothstep(0, 100, length(_WorldSpaceCameraPos.xz - output.positionWS.xz));
    output.normalWS = lerp( half3(0, 1, 0), output.normalWS, distanceBlend);

    output.addData.x = distanceBlend;
    
    return output;
}

inline float UnityCalcDistanceTessFactor (float4 vertex, float minDist, float maxDist, float tess)
{
    float3 wpos = mul(unity_ObjectToWorld,vertex).xyz;
    float dist = distance (wpos, _WorldSpaceCameraPos);
    float f = clamp(1.0 - (dist - minDist) / (maxDist - minDist), 0.01, 1.0) * tess;
    return f;
}

inline float3 UnityCalcTriEdgeTessFactors (float3 triVertexFactors)
{
    float3 tess;
    tess.x = 0.5 * (triVertexFactors.y + triVertexFactors.z);
    tess.y = 0.5 * (triVertexFactors.x + triVertexFactors.z);
    tess.z = 0.5 * (triVertexFactors.x + triVertexFactors.y);
    return tess;
}

inline float3 tessDist (float4 v0, float4 v1, float4 v2)
{
    float3 f;
    f.x = UnityCalcDistanceTessFactor (v0,_MinDist,_MaxDist,_Tessellation);
    f.y = UnityCalcDistanceTessFactor (v1,_MinDist,_MaxDist,_Tessellation);
    f.z = UnityCalcDistanceTessFactor (v2,_MinDist,_MaxDist,_Tessellation);
    return UnityCalcTriEdgeTessFactors (f);

}

inline OutputPatchConstant hsconst (InputPatch<TessVertex,3> v) {
    OutputPatchConstant o;
    float3 tf = (tessDist(v[0].positionOS, v[1].positionOS, v[2].positionOS));

    o.edge[0] = tf.x;
    o.edge[1] = tf.y;
    o.edge[2] = tf.z;
    o.inside =  (tf.x + tf.y + tf.z) * 0.33333333;
    return o;
}

[domain("tri")] //确定图元
[partitioning("fractional_odd")] //拆分edge的规则 equal_spacing,fractional_odd,fractional_even
[outputtopology("triangle_cw")]
[patchconstantfunc("hsconst")]
[outputcontrolpoints(3)]

inline TessVertex OceanHull (InputPatch<TessVertex,3> v, uint id : SV_OutputControlPointID)
{
    return v[id];
}

[domain("tri")] //确定图元
inline Varyings OceanDomain(OutputPatchConstant patchTess,float3 bary:SV_DomainLocation,const OutputPatch<TessVertex,3> vi)
{
    //重心坐标 bary
    Attributes v = (Attributes)0;
    
    v.positionOS.xyz = vi[0].positionOS*bary.x+vi[1].positionOS*bary.y+vi[2].positionOS*bary.z;
    v.tangentOS = vi[0].tangentOS*bary.x + vi[1].tangentOS*bary.y + vi[2].tangentOS*bary.z;
    v.normalOS = vi[0].normalOS*bary.x + vi[1].normalOS*bary.y + vi[2].normalOS*bary.z;
    v.uv0 = vi[0].uv0*bary.x + vi[1].uv0*bary.y + vi[2].uv0*bary.z;
    
    Varyings dout = OceanVert(v);
    return dout;
}

float3 SSS(float depth)
{
    return SAMPLE_TEXTURE2D(_AbsorptionScatteringLUT, sampler_AbsorptionScatteringLUT, half2(depth, 0.375h)).rgb;
}

float3 Absorption(float depth)
{
    return SAMPLE_TEXTURE2D(_AbsorptionScatteringLUT, sampler_AbsorptionScatteringLUT, half2(depth, 0.0h)).rgb;
}


half3 Refraction(half2 distortion, half depth, real depthMulti)
{
    half3 output = SAMPLE_TEXTURE2D_LOD(_CameraOpaqueTexture, sampler_CameraOpaqueTexture_linear_clamp, distortion, depth * 0.25).rgb;
    output *= Absorption((depth) * depthMulti);
    return output;
}

half3 SampleReflections(half3 normalWS, half3 viewDirectionWS, half2 screenUV, half roughness)
{
    half3 reflection = 0;
    half2 refOffset = 0;

    #if _REFLECTION_PLANARREFLECTION

        // get the perspective projection
        float2 p11_22 = float2(unity_CameraInvProjection._11, unity_CameraInvProjection._22) * 10;
        // conver the uvs into view space by "undoing" projection
        float3 viewDir = -(float3((screenUV * 2 - 1) / p11_22, -1));
    
        half3 viewNormal = mul(normalWS, (float3x3)GetWorldToViewMatrix()).xyz;
        half3 reflectVector = reflect(-viewDir, viewNormal);
    
        half2 reflectionUV = screenUV + normalWS.zx * half2(0.02, 0.15);
        reflection += SAMPLE_TEXTURE2D_LOD(_PlanarReflectionTexture, sampler_ScreenTextures_linear_clamp, reflectionUV, 6 * roughness).rgb;//planar reflection
    
    #elif _SSRREFLECT
        reflection = SAMPLE_TEXTURE2D_LOD(_ScreenSpaceReflectMap, sampler_ScreenTextures_linear_clamp, screenUV, 0);
    #endif
    
    return reflection;
}


half4 OceanFrag(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    //return  float4(input.addData.xxx, 1);
    
    //-------深浅---------
    float2 positionSS = input.positionCS.xy / _ScaledScreenParams.xy;
    
    #if UNITY_REVERSED_Z
        real depth = SampleSceneDepth(positionSS);
    #else
    // 调整 z 以匹配 OpenGL 的 NDC
        real depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, SampleSceneDepth(positionSS));
    #endif
   
    float3 depthWorldPos = ComputeWorldSpacePosition(positionSS, depth, UNITY_MATRIX_I_VP);
    
    float waterDepth = abs(input.positionWS.y - depthWorldPos.y);
    float depthMulti = 1.0 / 12;

    float a = smoothstep(0, 12, waterDepth);

    //a = Pow4(waterDepth / 12.0);
    //return  float4(a.xxx,1);

    
    
    //----------detail waves 法线---------
    half2 detailBump1 = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.normalUV.zw).xy * 2 - 1;
    half2 detailBump2 = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.normalUV.xy).xy * 2 - 1;
    half2 detailBump = (detailBump1 + detailBump2 * 0.5);// * saturate(waterDepth * 0.01 + 0.25);

    //detailBump = detailBump2;

    input.normalWS += half3(detailBump.x, 0, detailBump.y) * _NormalScale * input.addData.x;
    //input.normalWS += half3(1-waterFX.y, 0.5h, 1- waterFX.z) - 0.5;
    input.normalWS = normalize(input.normalWS);

    
    //------------着色-------------
    Light light = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
    half shadow = light.shadowAttenuation;  //unity boat有一种特殊的软阴影   
    half3 GI = SampleSH(input.normalWS); //lightprobe

    //fresnel
    half fresnel = saturate(pow(1.0 - dot(input.normalWS, input.viewDirWS), 5));
    
    //SSS
    half3 directLighting = dot(light.direction, input.normalWS) * light.color;  
    directLighting += saturate(pow(dot(input.viewDirWS, -light.direction) * input.addData.z, 3)) * 5 * light.color;
    half3 sss = directLighting * shadow + GI;

    //BRDF
    BRDFData brdfData;
    half alpha = a;
    InitializeBRDFData(half3(0, 0, 0), 0, half3(1, 1, 1), 0.95, alpha, brdfData);
    half3 spec = DirectBDRF(brdfData, input.normalWS, light.direction, input.viewDirWS) * shadow * light.color;
    #ifdef _ADDITIONAL_LIGHTS
        uint pixelLightCount = GetAdditionalLightsCount();
        for (uint lightIndex = 0u; lightIndex < pixelLightCount; ++lightIndex)
        {
            Light light = GetAdditionalLight(lightIndex, IN.posWS);
            spec += LightingPhysicallyBased(brdfData, light, IN.normal, IN.viewDir);
            sss += light.distanceAttenuation * light.color;
        }
    #endif

    sss *= SSS(waterDepth * depthMulti);
    //return  waterDepth  * depthMulti;
    //Reflections
    half3 reflection = SampleReflections(input.normalWS, input.viewDirWS, positionSS.xy, 0.0);

    //Refraction
    half3 refraction = Refraction(positionSS.xy, waterDepth, depthMulti);

    half3 finCol = lerp(refraction, reflection, fresnel) + sss + spec;
        
    return half4(finCol, a);
    
    
}


#endif