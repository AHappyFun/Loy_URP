#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
#include "Assets/RenderFeature/Hiz/HIZ.hlsl"

struct Attributes
{
    uint vertexID   : SV_VertexID;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    UNITY_VERTEX_OUTPUT_STEREO
};

TEXTURE3D(_NoiseTex);
SAMPLER(sampler_NoiseTex);


float _CloudDensity;
float _StepSize;
float _PhaseG; //相函数g

int _LightSteps;
float _LightStepSize;

float3 _BoxCenter;
float3 _BoxSize;

// Ray vs AABB
bool RayBoxIntersect(
    float3 ro, float3 rd,
    float3 bmin, float3 bmax,
    out float t0, out float t1)
{
    float3 inv = 1.0 / rd;
    float3 tmin = (bmin - ro) * inv;
    float3 tmax = (bmax - ro) * inv;
    float3 t1v = min(tmin, tmax);
    float3 t2v = max(tmin, tmax);

    t0 = max(max(t1v.x, t1v.y), t1v.z);
    t1 = min(min(t2v.x, t2v.y), t2v.z);

    return t1 > max(t0, 0.0);
}

// Henyey-Greenstein
float PhaseHG(float cosTheta, float g)
{
    float g2 = g * g;
    return (1 - g2) / (4 * PI * pow(1 + g2 - 2 * g * cosTheta, 1.5));
}

//不同于其他Raymrach是从场景中的点开始起点，体积云的都从摄像机直接开始
//世界空间Ray
float4 VolumeCloudRayMarch(Varyings IN)
{

    float3 rayDirWS = normalize(IN.positionWS - _WorldSpaceCameraPos);
    float3 rayOriginWS = _WorldSpaceCameraPos;

    //return float4(abs(rayDirWS),1);

    // Box
    float3 halfSize = _BoxSize * 0.5;
    float3 boxMin = _BoxCenter - halfSize;
    float3 boxMax = _BoxCenter + halfSize;

    float tEnter, tExit;
    bool hit = RayBoxIntersect(
        rayOriginWS,
        rayDirWS,
        boxMin,
        boxMax,
        tEnter,
        tExit
    );

    // 没看向 Box
    if (!hit)
        return 0;

    float t = max(tEnter, 0);
    float T = 1.0;
    float3 col = 0;

    Light mainLight = GetMainLight();
    float3 lightDir = normalize(mainLight.direction);

    float cosTheta = dot(rayDirWS, -lightDir);
    float phase = PhaseHG(cosTheta, _PhaseG);

    [loop]
    for (int step = 0; step < 128 && t < tExit; step++)
    {
        float3 pos = rayOriginWS + rayDirWS * t;

        float3 uvw = (pos - _BoxCenter) / _BoxSize + 0.5;

        float density = SAMPLE_TEXTURE3D(_NoiseTex, sampler_NoiseTex, uvw).r;
        //density *= _CloudDensity;

        if (density > 0.001)
        {
            float sigma = density * _CloudDensity;
            sigma = pow(saturate(density), 5);
            float dt = _StepSize;

            // --- 光线透射（丁达尔） ---
            float T_light = 1.0;
            float lt = 0;

            [loop]
            for (int l = 0; l<_LightSteps; l++)
            {
                float3 lpos = pos + lightDir * lt;
                float3 luvw = (lpos - _BoxCenter) / _BoxSize + 0.5;
                float ld = SAMPLE_TEXTURE3D(_NoiseTex, sampler_NoiseTex, luvw).r;
                T_light *= exp(-ld * _CloudDensity * _LightStepSize);

                if (T_light < 0.01) break;

                lt += _LightStepSize;
            }

            //float phase = PhaseHG(dot(rayDirWS, -lightDir), _PhaseG);
            float3 scatter = mainLight.color * T_light * sigma * phase * dt;
            col += T * scatter;

            T *= exp(-sigma * dt);

            if (T < 0.01) break;
        }

        t += _StepSize;
    }

    return float4(col, 1 - T);

}
