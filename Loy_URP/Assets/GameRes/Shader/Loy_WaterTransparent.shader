Shader "Loy/WaterTransparent"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor("Water Tint / Opacity", Color) = (1, 1, 1, 1)
        _Smoothness("Smoothness", Range(0,1)) = 0.9
        [Normal] _BumpMap("Normal Map (optional detail)", 2D) = "bump" {}
        _BumpScale("Normal Strength", Range(0,2)) = 0.5

        [Header(Refraction and Reflection)]
        _RefractionStrength("Refraction Strength", Range(0, 0.5)) = 0.06
        _ReflectionStrength("Reflection Strength", Range(0, 2)) = 1
        _FresnelPower("Fresnel Power", Range(1, 8)) = 5
        _FresnelMin("Fresnel Min", Range(0, 1)) = 0.02

        [Header(Depth Color)]
        _ShallowColor("Shallow Color", Color) = (0.20, 0.55, 0.80, 1)
        _DeepColor("Deep Color", Color) = (0.005, 0.13, 0.32, 1)
        _DepthDistance("Depth Distance", Range(0.1, 30)) = 2.5
        _Absorption("Absorption", Range(0, 8)) = 0.9

        [Header(Waves)]
        _WaveScale("Wave Scale", Range(0.1, 30)) = 5
        _WaveAmplitude("Wave Amplitude", Range(0, 2)) = 0.25
        _WaveSpeed("Wave Speed", Range(0, 8)) = 1.2

        [Header(Foam)]
        _FoamColor("Foam Color", Color) = (0.9, 0.95, 1, 1)
        _FoamDistance("Foam Distance", Range(0.01, 3)) = 0.35
        _FoamNoiseScale("Foam Noise Scale", Range(0, 60)) = 14
        _FoamNoiseStrength("Foam Noise Strength", Range(0, 1)) = 0.3

        [Header(Lighting)]
        _SpecularStrength("Specular Strength", Range(0, 6)) = 2

        [HideInInspector] _Cull("__cull", Float) = 2.0
        [HideInInspector] _ZWrite("__zw", Float) = 0.0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "LoyWaterSurface" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite [_ZWrite]
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex WaterVert
            #pragma fragment WaterFrag
            #pragma multi_compile_fog
            #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            // 所有材质属性必须放在 UnityPerMaterial 中，SRP Batcher 才能生效。
            // 注意：CBUFFER 内不能使用 ifdef 改变布局，否则 SRP Batcher 无法工作。
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _ShallowColor;
                half4 _DeepColor;
                half4 _FoamColor;
                half _Smoothness;
                half _BumpScale;
                half _RefractionStrength;
                half _ReflectionStrength;
                half _FresnelPower;
                half _FresnelMin;
                half _DepthDistance;
                half _Absorption;
                half _WaveScale;
                half _WaveAmplitude;
                half _WaveSpeed;
                half _FoamDistance;
                half _FoamNoiseScale;
                half _FoamNoiseStrength;
                half _SpecularStrength;
            CBUFFER_END

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
            TEXTURE2D(_WaterReflectionTex); SAMPLER(sampler_WaterReflectionTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half3 tangentWS : TEXCOORD2;
                half3 bitangentWS : TEXCOORD3;
                float2 uv : TEXCOORD4;
                half fogFactor : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings WaterVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalInputs.normalWS;
                output.tangentWS = normalInputs.tangentWS;
                output.bitangentWS = normalInputs.bitangentWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                return output;
            }

            // 程序化水面法线波动：对一组方向/频率不同的正弦高度场求梯度，
            // 得到世界空间（水平面）的法线。无需法线贴图即可产生连续可动画的波纹。
            half3 WaterWaveNormal(float3 positionWS, half time)
            {
                float2 p = positionWS.xz * _WaveScale;
                float dhdx = 0.0;
                float dhdz = 0.0;
                // 主波（沿 X / 沿 Z 两个方向）
                dhdx += 1.00 * cos(p.x + time * 1.00);
                dhdz += 0.35 * cos(p.y - time * 1.30);
                // 斜向次波
                float q = 0.70 * p.x + 0.70 * p.y + time * 0.70;
                dhdx += 0.70 * cos(q);
                dhdz += 0.70 * cos(q);
                // 细波
                dhdx += 0.50 * cos(1.60 * p.x - 0.80 * p.y + time * 0.50);
                dhdz += 0.50 * cos(-0.80 * p.x + 1.60 * p.y - time * 0.50);
                half3 n = normalize(half3(-dhdx * _WaveAmplitude, 1.0, -dhdz * _WaveAmplitude));
                return n;
            }

            half4 WaterFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // 屏幕 UV（与 _CameraOpaqueTexture / _CameraDepthTexture 采样约定一致）
                float2 screenUV = input.positionCS.xy / _ScaledScreenParams.xy;

                // ---- 水深：水面到场景的距离（水柱厚度）----
                float sceneEyeDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                float waterEyeDepth = input.positionCS.w;
                float waterDepth = sceneEyeDepth - waterEyeDepth;

                // ---- 法线：程序化波纹为主，法线贴图细节作为扰动叠加 ----
                half3 geomNormalWS = input.normalWS;
                half3 waveNormalWS = WaterWaveNormal(input.positionWS, _Time.y);
                half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                half3 detailWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS,
                    half3x3(input.tangentWS, input.bitangentWS, geomNormalWS)));
                half3 normalWS = NormalizeNormalPerPixel(waveNormalWS + (detailWS - geomNormalWS));

                half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);

                // 视空间法线（折射偏移用）+ 世界空间反射方向（反射探针采样用）
                half3 normalVS = TransformWorldToViewDir(normalWS);
                half3 reflectDirWS = reflect(-viewDirWS, normalWS);

                // 屏幕空间 Y 轴方向修正（D3D 原点在左上，视空间 +Y 向上）
                float2 flip = float2(1.0, 1.0);
                #if UNITY_UV_STARTS_AT_TOP
                flip.y = -1.0;
                #endif

                // ---- 折射：沿法线扰动屏幕 UV 采样场景色 ----
                float2 refrOffset = normalVS.xy * flip * _RefractionStrength;
                refrOffset *= saturate(waterDepth / 0.4);   // 浅水处折射减弱，贴岸更平
                half3 refractedColor = SampleSceneColor(screenUV - refrOffset);

                // ---- 反射：屏幕空间平面反射(_WaterReflectionTex) + 反射探针兜底天空 ----
                // SSPR 反射屏幕内几何/天空；屏幕外(alpha=0)处回退到反射探针。
                half perceptualRoughness = 1.0h - _Smoothness;
                half3 probeRefl = GlossyEnvironmentReflection(reflectDirWS, perceptualRoughness, 1.0h);
                float2 reflWobble = normalVS.xy * flip * 0.02;   // 波面法线让倒影随波扭曲
                float4 sspr = SAMPLE_TEXTURE2D(_WaterReflectionTex, sampler_WaterReflectionTex, screenUV + reflWobble);
                half3 reflectedColor = lerp(probeRefl, sspr.rgb, sspr.a) * _ReflectionStrength;

                // ---- Fresnel：掠射角反射更强 ----
                half fresnel = pow(1.0 - saturate(dot(normalWS, viewDirWS)), _FresnelPower);
                fresnel = lerp(_FresnelMin, 1.0, fresnel);

                // ---- 深浅颜色：水柱越厚颜色越深、吸收越强 ----
                half depthFactor = saturate(waterDepth / _DepthDistance);
                half3 waterBody = lerp(_ShallowColor.rgb, _DeepColor.rgb, depthFactor);
                half3 refractedTinted = refractedColor * waterBody;

                // ---- 折射与反射按 Fresnel 混合 ----
                half3 color = lerp(refractedTinted, reflectedColor, fresnel);

                // ---- 边界泡沫：水越浅（贴岸/贴物体）泡沫越多，噪波做不规则边缘 ----
                float foamNoise = sin(input.positionWS.x * _FoamNoiseScale + _Time.y * 0.6)
                                * sin(input.positionWS.z * _FoamNoiseScale - _Time.y * 0.4);
                half foam = 1.0 - saturate(waterDepth / _FoamDistance);
                foam += foamNoise * _FoamNoiseStrength;
                foam = smoothstep(0.0, 1.0, foam);
                color = lerp(color, _FoamColor.rgb, foam * _FoamColor.a);

                // ---- 主光镜面高光（太阳反光）----
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half3 halfDir = SafeNormalize(mainLight.direction + viewDirWS);
                half spec = pow(saturate(dot(normalWS, halfDir)), lerp(8.0h, 512.0h, _Smoothness));
                color += mainLight.color * spec * _SpecularStrength * mainLight.shadowAttenuation;

                // ---- 微弱环境光，避免背光处全黑 ----
                color += SampleSH(normalWS) * 0.1;

                // ---- 雾与透明度 ----
                color = MixFog(color, input.fogFactor);
                half alpha = _BaseColor.a;

                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
