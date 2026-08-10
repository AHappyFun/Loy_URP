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
float _SSRMaxDistance;
float _Thickness;
int _Frame;
#define minSmoothness 0.5
#define binaryStepCount 16

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

float2 ViewPosToScreenUV(float3 viewPos)
{
    float4 clip = mul(UNITY_MATRIX_P, float4(viewPos, 1));
    float2 ndc = clip.xy / clip.w;
    float2 uv = ndc * 0.5f + 0.5f;
#if UNITY_UV_STARTS_AT_TOP
    uv.y = 1.0 - uv.y;
#endif
    return uv;
}

// ======================================================================
// Ray March  DDA
// 不直接在3d上走距离，沿着反射向量投影到屏幕上，一次跨越一个像素边界。
// 沿着像素栅格往前走。必须严格按照像素格子前进，不能单纯在屏幕UV均匀步进.
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

    float3 N = SAMPLE_TEXTURE2D(_GBuffer2, sampler_GBuffer2, uv);
    float3 V = normalize(float3(worldPos.xyz) - _WorldSpaceCameraPos);

    //反射方向转换到View
    float3 R = normalize(reflect(V, N));
    R = mul(UNITY_MATRIX_V, R);

    R.z *= -1;
    viewPos.z *= -1;

    //float viewReflectDot = saturate(dot(V, R));

    //反射角接近视角方向，增大步长
    //float cameraViewReflectDot = saturate(dot(_WorldSpaceViewForward, R));

    //float thickness = _SSRStepSize * 2;
    //float oneMinusViewReflectDot = sqrt(1 - viewReflectDot);
    ////步长和厚度，如果反射角和视角越平，步长和厚度增大
    //_SSRStepSize /= oneMinusViewReflectDot;
    //thickness /= oneMinusViewReflectDot;

    //Jitter优化 根据粗糙度和随机，调整步长的Scale，减少实际步数
    //float2 pixel = uv * _ScreenParams.xy; // tile noise pattern or scale to BlueNoise resolution

    // 在 uv 上加一个基于帧数的偏移, halftone，TAA同理
    //int2 ditherCoord = int2(
    //    fmod(pixel.x + _Frame * 1.3, 4),
    //    fmod(pixel.y + _Frame * 2.1, 4)
    //);
    //int2 ditherCoord = int2(fmod(pixel.x, 4), fmod(pixel.y, 4));

    //float ditherValue = dither[ditherCoord.x * 4 + ditherCoord.y]; // range 0..1

    float roughness = 1 - smoothness;
    float jitterAmp = lerp(0.25, 1.0, saturate(roughness)); // 粗糙度越大，jitter越强

    //步长Scale通过Jitter随机
    //float stepJitter = 1.0 + ditherValue * 0.5 * jitterAmp;
    //采样UV进行偏移
    //dirJitterUV 其实就是把这种“方向抖动”转换成 UV 空间的偏移量，
    //简单理解为：
    //“在屏幕空间上，射线方向稍微歪一点。”
    //通常 SSR 的光线是基于view-space 反射方向算的。
    //要让它 jitter，有两种常见写法：ViewDir上直接加扰动(费) 、 在屏幕空间偏移UV
    //float2 jitterUVOffset = ditherValue * rcp(_ScreenParams.xy) * jitterAmp;

    float hit = 0.0;
    float maskOut = 1;

    float2 currentScreenSpacePosition = uv;
    float3 rayPos = viewPos;

    //屏幕空间步进方向
    float2 startSS = ViewPosToScreenUV(rayPos);
    float2 endSS = ViewPosToScreenUV(rayPos + R * _SSRMaxDistance);



    float2 dirSS = normalize(endSS - startSS);

    bool doRayMarch =  smoothness > minSmoothness;

    float2 pixelSize = rcp(_ScreenParams.xy);  //一个像素的UV
    float2 stepSign  = sign(dirSS) * pixelSize * 1; // 步进方向,取符号是为了xy都取整，每次至少走一个像素。

    // 反射深度变化量（view-space） 需要记录每一次步进，Z走了多少
    float zDelta = abs(R * _SSRMaxDistance).z;
    float stepZ = zDelta / _SSRMaxSteps;

    float rayCurZ = viewPos.z;

    float _StepLength = _SSRMaxDistance / _SSRMaxSteps;

    if(doRayMarch)
    {
        float2 rayPer = stepSign;
        float deltaDepth = 0;
        [loop]
        for (int i = 0; i < _SSRMaxSteps; ++i)
        {
            //uv上移动一步
            startSS += rayPer;
            //记录深度也增加
            //rayCurZ += stepZ;

            if (any(startSS < 0.0) || any(startSS > 1.0)) break;

            float sceneDepth = SampleSceneDepth(startSS);

            //用LinearEyeDepth的时候Unity会自动处理ReverseZ的情况
            deltaDepth = rayCurZ - LinearEyeDepth(sceneDepth, _ZBufferParams);

            [branch]
            if(abs(deltaDepth) > 0 && abs(deltaDepth) < _Thickness)
            {
                currentScreenSpacePosition = startSS;
                hit = 1.0;
                break;
            }

            rayCurZ += R.z * _StepLength;

        }

    }

    maskOut *= hit;

    // 无命中时输出黑色（alpha=0，combine 按 alpha 忽略）
    half3 ssrColor = hit > 0 ? SampleSceneColor(currentScreenSpacePosition) : 0.0;

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

