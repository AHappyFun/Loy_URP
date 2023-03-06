#ifndef WATER_FUNC
#define WATER_FUNC

#define PI   3.14159265358979323846

float3 GerstnerWave(float4 waveParam, float3 pos, inout float3 bitangent, inout float3 tangent)
{
    float3 position = pos;
    float A = waveParam.z;
    float waveLength = waveParam.w;
    float speed = .2;

    float2 dir = normalize(waveParam.xy);

    float k = 2 * PI / waveLength;
    
    speed *= sqrt(9.8 / k);

    float f = k * (dot(dir, pos.xz) - _Time.y * speed);

    A /= k;
    
    position.x += dir.x * A * cos(f); 
    position.y = A * sin(f);
    position.z += dir.y * A * cos(f);

    tangent += float3(  - A * k * sin(f) * dir.x * dir.x,
                        A * k * cos(f) * dir.x,
                        - A * k * sin(f) * dir.x * dir.y);

    bitangent += float3(- A * k * sin(f) * dir.x * dir.y,
                        A * k * cos(f) * dir.y,
                        - A * k * sin(f) * dir.y * dir.y);
    
    
    return position;
}

#endif