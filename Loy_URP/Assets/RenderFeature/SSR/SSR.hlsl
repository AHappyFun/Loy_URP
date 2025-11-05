// SSR.hlsl
// URP deferred-style screen space reflection pass (pure HLSL)
// Compatible with Blitter.BlitCameraTexture or CoreBlit

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

// jitter dither map
static half dither[16] = {
    0.0, 0.5, 0.125, 0.625,
    0.75, 0.25, 0.875, 0.375,
    0.187, 0.687, 0.0625, 0.562,
    0.937, 0.437, 0.812, 0.312
};


TEXTURE2D_X(_CameraOpaqueTexture);
SAMPLER(sampler_CameraOpaqueTexture);
TEXTURE2D_X_HALF(_GBuffer2);
SAMPLER(sampler_GBuffer2);


float4x4 _CameraView;
float4x4 _CameraInvView;
float4x4 _CameraProjection;
float4x4 _CameraInvProjection;

float3 _WorldSpaceViewForward;

int _SSRMaxSteps;
float _SSRStepSize;
//float _SSRThickness;
int _Frame;
#define minSmoothness 0.5
#define binaryStepCount 16
//int _SSRBinarySearch;

// ======================================================================
// Helpers
// ======================================================================

float3 ReconstructViewPos(float2 uv, float rawDepth)
{
    float4 clip = float4(uv * 2.0 - 1.0, rawDepth, 1.0);

    float4 view = mul(_CameraInvProjection, clip);
    view /= view.w;
    view.y *= -1;
    return view;
}

float3 ReconstructWorldPos(float2 uv, float rawDepth)
{
    float3 viewPos = ReconstructViewPos(uv, rawDepth);
    float4 worldPos = mul(_CameraInvView, float4(viewPos, 1));
    return worldPos.xyz;
}

float3 SampleSceneColor(float2 uv)
{
    return SAMPLE_TEXTURE2D_X(_CameraOpaqueTexture, sampler_CameraOpaqueTexture, uv).rgb;
}

float3 SampleWorldNormal(float2 uv)
{
    float3 n = SampleSceneNormals(uv);
    return normalize(n);
}

// ======================================================================
// Ray March  ViewPos 视角空间
// ======================================================================

float4 SSRRaymarch(float2 uv)
{
    //深度重建worldPos
    float rawDepth = SampleSceneDepth(uv);

    float4 gbuffer2 = SAMPLE_TEXTURE2D(_GBuffer2, sampler_GBuffer2, uv);
    float smoothness = gbuffer2.w;
//#if UNITY_REVERSED_Z
//    rawDepth = 1.0 - rawDepth;
//#else
//    rawDepth = 2.0 * rawDepth - 1.0;
//#endif

    float3 worldPos = ReconstructWorldPos(uv, rawDepth);
    float3 viewPos = ReconstructViewPos(uv, rawDepth);

    //都需要转换到View
    float3 N = SAMPLE_TEXTURE2D(_GBuffer2, sampler_GBuffer2, uv);

    float3 V = normalize(float3(worldPos.xyz) - _WorldSpaceCameraPos);

    //反射方向转换到View
    float3 R = normalize(reflect(V, N));
    R = mul(UNITY_MATRIX_V, R);

    R.z *= -1;
    viewPos.z *= -1;

    float viewReflectDot = saturate(dot(V, R));

    //反射角接近视角方向，增大步长
    float cameraViewReflectDot = saturate(dot(_WorldSpaceViewForward, R));

    float thickness = _SSRStepSize * 2;
    float oneMinusViewReflectDot = sqrt(1 - viewReflectDot);
    //步长和厚度，如果反射角和视角越平，步长和厚度增大
    _SSRStepSize /= oneMinusViewReflectDot;
    thickness /= oneMinusViewReflectDot;

    //Jitter优化 根据粗糙度和随机，调整步长的Scale，减少实际步数
    float2 pixel = uv * _ScreenParams.xy; // tile noise pattern or scale to BlueNoise resolution

    // 在 uv 上加一个基于帧数的偏移, halftone，TAA同理
    int2 ditherCoord = int2(
        fmod(pixel.x + _Frame * 1.3, 4),
        fmod(pixel.y + _Frame * 2.1, 4)
    );
    //int2 ditherCoord = int2(fmod(pixel.x, 4), fmod(pixel.y, 4));

    float ditherValue = dither[ditherCoord.x * 4 + ditherCoord.y]; // range 0..1

    float roughness = 1 - smoothness;
    float jitterAmp = lerp(0.25, 1.0, saturate(roughness)); // 粗糙度越大，jitter越强

    //步长Scale通过Jitter随机
    float stepJitter = 1.0 + ditherValue * 0.5 * jitterAmp;
    //采样UV进行偏移
    //dirJitterUV 其实就是把这种“方向抖动”转换成 UV 空间的偏移量，
    //简单理解为：
    //“在屏幕空间上，射线方向稍微歪一点。”
    //通常 SSR 的光线是基于view-space 反射方向算的。
    //要让它 jitter，有两种常见写法：ViewDir上直接加扰动(费) 、 在屏幕空间偏移UV
    float2 jitterUVOffset = ditherValue * rcp(_ScreenParams.xy) * jitterAmp;

    float hit = 0.0;
    float maskOut = 1;

    float2 currentScreenSpacePosition = uv;
    float3 rayPos = viewPos;
    
    bool doRayMarch = smoothness > minSmoothness;

    float maxRayLength = _SSRMaxSteps * _SSRStepSize;
    float maxDist = lerp(min(viewPos.z, maxRayLength), maxRayLength, cameraViewReflectDot);
    float numSteps_f = maxDist / _SSRStepSize;
    _SSRMaxSteps = max(numSteps_f, 0);

    if(doRayMarch)
    {
        float3 rayPer = R * _SSRStepSize * stepJitter;
        float deltaDepth = 0;

        [loop]
        for (int i = 0; i < _SSRMaxSteps; ++i)
        {
            rayPos += rayPer;

            float4 clip = mul(_CameraProjection, float4(rayPos.x, -rayPos.y, -rayPos.z, 1));
            float2 ndc = clip.xy / clip.w;
            float2 sampleUV = ndc * 0.5 + 0.5;
            if (any(sampleUV < 0.0) || any(sampleUV > 1.0)) break;

            float sceneDepth = SampleSceneDepth(sampleUV);

            if(abs(rawDepth - sceneDepth) > 0 && sceneDepth != 0)
            {
                deltaDepth = rayPos.z - LinearEyeDepth(sceneDepth, _ZBufferParams);

                [branch]
                if(deltaDepth > 0 && deltaDepth < _SSRStepSize * 2)
                {
                    currentScreenSpacePosition = sampleUV;
                    hit = 1.0;
                    break;
                }
            }
        }

        if (deltaDepth > thickness) {
            hit = 0;
        }

        int binarySearchSteps = binaryStepCount * hit;

        //击中的二分查找提高精度
        [loop]
        for (int i = 0; i < binarySearchSteps; ++i)
        {
            rayPer *= 0.5f; //每次减半，二分

            //大了减，少了加，一直逼近
            if (deltaDepth > 0) {
                rayPos -= rayPer;
            }
            else if(deltaDepth < 0)
            {
                rayPos += rayPer;
            }
            else {
                break;
            }

            float4 clip = mul(_CameraProjection, float4(rayPos.x, -rayPos.y, -rayPos.z, 1));
            float2 ndc = clip.xy / clip.w;

            ndc += jitterUVOffset;// jitter in screen space to break coherent misses

            float2 sampleUV = ndc * 0.5 + 0.5;
            currentScreenSpacePosition = sampleUV;

            float sceneDepth = SampleSceneDepth(sampleUV);
            deltaDepth = rayPos.z - LinearEyeDepth(sceneDepth, _ZBufferParams);

            float minv = 1 / max((oneMinusViewReflectDot * float(i)), 0.001);
            //如果走了所有的二分之后，仍然在minv里，说明hit准确
            if (abs(deltaDepth) > minv) {
                hit = 0;
                break;
            }

        }

        //剔除背面三角形 (与反射方向相反的应该是正确像素，相同方向的看不到所以剔除)
        float3 N = UnpackNormal(SAMPLE_TEXTURE2D(_GBuffer2, sampler_GBuffer2, currentScreenSpacePosition))  ;
        float backFaceDot = dot(N, R);
        if (backFaceDot > 0) {
            hit = 0;
        }
    }



    //命中衰减逻辑 的核心之一，用于让反射在接近最大步长时逐渐淡出（fade out），防止“突然断掉”的硬边。
    //距离越大，SSR的程度降低，通过平方做非线性过渡
    float3 deltaDir = viewPos.xyz - rayPos;
    float progress = dot(deltaDir, deltaDir) / (maxDist * maxDist);
    progress = smoothstep(0, .5, 1 - progress);


    maskOut *= progress;
    maskOut *= hit;


    half3 ssrColor = SampleSceneColor(currentScreenSpacePosition);

    return half4(ssrColor, maskOut);

}

struct Attributes
{
    uint vertexID   : SV_VertexID;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

