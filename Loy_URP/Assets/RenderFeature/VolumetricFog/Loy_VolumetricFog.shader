Shader "Loy/Feature/VolumetricFog"
{
    Properties
    {
        _NoiseTex ("3D Noise", 3D) = "" {}
        [HideInInspector] _MainTex ("Base (RGB)", 2D) = "white" {}
    }
    HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        TEXTURE2D(_CameraDepthTexture);
        // sampler_LinearClamp 由 GlobalSamplers.hlsl 提供，无需重复声明
        TEXTURE3D(_NoiseTex);
        SAMPLER(sampler_NoiseTex);

        // ---- 体积雾 raymarch 参数（记录阶段由 C# 侧设置）----
        float  _FogDensity;
        float  _FogHeightStart;
        float  _FogHeightFalloff;
        float  _NoiseScale;
        float  _NoiseIntensity;
        float3 _NoiseScroll;
        float  _PhaseG;
        float  _ShadowStrength;
        int    _HasShadowMap;
        float  _Intensity;
        float  _MaxDistance;
        int    _Steps;
        float3 _Tint;
        float3 _FogColor;      // 环境雾色（阴影区/雾自身的基础色）

        // ---- blur / composite 参数 ----
        TEXTURE2D(_BlurSource);
        TEXTURE2D(_VolumetricLightTex);
        TEXTURE2D(_CameraColorTexture);   // composite 原地反馈：读当前场景颜色做消光混合
        float2 _BlurDir;        // 预乘了 texel size 的方向：V=(0, 1/h), H=(1/w, 0)

        struct Attributes
        {
            uint vertexID : SV_VertexID;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv         : TEXCOORD0;
        };

        Varyings vert(Attributes IN)
        {
            Varyings OUT;
            OUT.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
            OUT.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
            return OUT;
        }

        // Henyey-Greenstein 前向散射相函数
        float PhaseHG(float cosTheta, float g)
        {
            float g2 = g * g;
            // 底数 1+g2-2g*cosθ 在 cosθ=-1 时最小为 (1+g)^2 >= 0；clamp 防除零并消 warning
            float denom = max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4);
            return (1.0 - g2) / (4.0 * PI * pow(denom, 1.5));
        }

        // 主光阴影图集采样：真实光束遮挡。直接走级联 + _MainLightWorldToShadow，
        // 不经过 MainLightRealtimeShadow，避免 _MAIN_LIGHT_SHADOWS_SCREEN 分支
        // 采到的是表面屏幕空间阴影而不是阴影图集。
        float SampleSunShadow(float3 worldPos)
        {
        #ifdef _MAIN_LIGHT_SHADOWS_CASCADE
            half cascadeIndex = ComputeCascadeIndex(worldPos);
        #else
            half cascadeIndex = 0;
        #endif
            float4 shadowCoord = float4(mul(_MainLightWorldToShadow[cascadeIndex], float4(worldPos, 1.0)).xyz, 0.0);
            real shadow = SampleShadowmap(TEXTURE2D_ARGS(_MainLightShadowmapTexture, sampler_LinearClampCompare),
                shadowCoord, GetMainLightShadowSamplingData(), _MainLightShadowParams, false);
            return shadow;
        }

        // Pass 0：半分辨率体积雾 raymarch
        half4 RayMarch(Varyings i) : SV_Target
        {
            float2 uv = i.uv;
            float rawDepth = SAMPLE_TEXTURE2D(_CameraDepthTexture, sampler_LinearClamp, uv).r;
            // 场景表面世界坐标（天空像素会落在远平面）
            float3 scenePosWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);

            float3 rayOrigin = _WorldSpaceCameraPos;
            float3 rayDir = normalize(scenePosWS - rayOrigin);
            float sceneDist = distance(scenePosWS, rayOrigin);

            float tEnd = min(sceneDist, _MaxDistance);
            float t = 0.02;
            if (tEnd <= t) return 0;

            float dt = (tEnd - t) / max(_Steps, 1);
            int steps = _Steps;

            Light mainLight = GetMainLight();
            float3 lightDir = mainLight.direction;   // 指向太阳
            // 前向散射：cosTheta = dot(rayDir, lightDir)，看向太阳时取峰值 → 太阳附近光束最强
            float phase = PhaseHG(dot(rayDir, lightDir), _PhaseG);
            float3 lightColor = mainLight.color * _Tint * _Intensity;

            float3 uvwOffset = _NoiseScroll * _Time.y;

            float3 col = 0;
            float T = 1.0;

            [loop]
            for (int s = 0; s < steps; s++)
            {
                float3 pos = rayOrigin + rayDir * t;

                // 3D Worley 噪声密度（固定 LOD0 采样，避免循环内梯度指令）
                // 半连续介质：noiseIntensity=0 时均匀雾，=1 时纯噪声，默认让光束能扫过连续雾体
                float3 uvw = pos * _NoiseScale + uvwOffset;
                float noise = SAMPLE_TEXTURE3D_LOD(_NoiseTex, sampler_NoiseTex, uvw, 0).r;
                float density = lerp(1.0, noise, _NoiseIntensity);

                // 高度雾：越贴近地面越浓
                density *= exp(-max(pos.y - _FogHeightStart, 0.0) * _FogHeightFalloff);

                float sigma = density * _FogDensity;
                if (sigma > 1e-5)
                {
                    // 光束遮挡：阴影区不散射，形成丁达尔光束
                    float shadow = 1.0;
                    if (_HasShadowMap > 0)
                    {
                        shadow = SampleSunShadow(pos);
                        shadow = lerp(1.0, shadow, _ShadowStrength);
                    }

                    col += T * lightColor * (sigma * dt) * phase * shadow;
                    T *= exp(-sigma * dt);
                    if (T < 0.01) break;
                }
                t += dt;
            }

            return half4(col, 1.0 - T);
        }

        // Pass 1 / 2：可分离高斯模糊（9 tap）。RGBA 全通道模糊，A 通道是不透明度，供雾混合使用
        half4 Blur(Varyings i) : SV_Target
        {
            float2 uv = i.uv;
            half4 col = SAMPLE_TEXTURE2D(_BlurSource, sampler_LinearClamp, uv) * 0.227027;
            col += SAMPLE_TEXTURE2D(_BlurSource, sampler_LinearClamp, uv + 1.0 * _BlurDir) * 0.1945946;
            col += SAMPLE_TEXTURE2D(_BlurSource, sampler_LinearClamp, uv - 1.0 * _BlurDir) * 0.1945946;
            col += SAMPLE_TEXTURE2D(_BlurSource, sampler_LinearClamp, uv + 2.0 * _BlurDir) * 0.1216216;
            col += SAMPLE_TEXTURE2D(_BlurSource, sampler_LinearClamp, uv - 2.0 * _BlurDir) * 0.1216216;
            col += SAMPLE_TEXTURE2D(_BlurSource, sampler_LinearClamp, uv + 3.0 * _BlurDir) * 0.0540540;
            col += SAMPLE_TEXTURE2D(_BlurSource, sampler_LinearClamp, uv - 3.0 * _BlurDir) * 0.0540540;
            col += SAMPLE_TEXTURE2D(_BlurSource, sampler_LinearClamp, uv + 4.0 * _BlurDir) * 0.0162160;
            col += SAMPLE_TEXTURE2D(_BlurSource, sampler_LinearClamp, uv - 4.0 * _BlurDir) * 0.0162160;
            return col;
        }

        half4 BlurV(Varyings i) : SV_Target { return Blur(i); }
        half4 BlurH(Varyings i) : SV_Target { return Blur(i); }

        // Pass 3：完整体积雾合成（消光 + 散射）
        // result = scene * (1 - opacity) + 阳光散射光 + 环境雾色 * opacity
        // opacity = 1 - T：雾不透明度；近处物体几乎不受影响，远处淡出到雾色
        half4 Composite(Varyings i) : SV_Target
        {
            float2 uv = i.uv;
            half3 sceneColor = SAMPLE_TEXTURE2D(_CameraColorTexture, sampler_LinearClamp, uv).rgb;
            half4 vol = SAMPLE_TEXTURE2D(_VolumetricLightTex, sampler_LinearClamp, uv);
            half opacity = saturate(vol.a);
            half3 result = sceneColor * (1.0 - opacity) + vol.rgb + _FogColor * opacity;
            return half4(result, 1.0);
        }

    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Overlay" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
                #pragma vertex vert
                #pragma fragment RayMarch
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
                #pragma vertex vert
                #pragma fragment BlurV
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
                #pragma vertex vert
                #pragma fragment BlurH
            ENDHLSL
        }
        Pass
        {
            // 非加性：shader 里做完整雾混合（消光 + 散射），直接覆盖输出
            HLSLPROGRAM
                #pragma vertex vert
                #pragma fragment Composite
            ENDHLSL
        }
    }
}
