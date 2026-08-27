Shader "Loy/Loy_Vegetation"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _TintMin ("Tint Min", Color) = (1,1,1,1)
        _TintMax ("Tint Max", Color) = (1,1,1,1)
        _WindStrength ("Wind Strength", Float) = 1
        _WindSpeed ("Wind Speed", Float) = 1
        _WindDirection ("Wind Direction (XZ)", Vector) = (1, 0, 0, 0)
        _WindFrequency ("Wind Frequency", Float) = 0.15
        _Smoothness ("Smoothness", Range(0,1)) = 0.1
        _Translucency ("Translucency", Range(0,1)) = 0.6
        _SheenStrength ("Sheen Strength", Range(0,1)) = 0.5
        _SheenPower ("Sheen Power", Range(1,16)) = 4
        _GlobalScale ("Global Scale", Float) = 1
        _AmbientStrength ("Ambient Strength", Range(0,5)) = 1.5
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.4
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "AlphaTest" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        struct VegetationVisible
        {
            float4 positionScale;
            float4 rotSeedPad;
        };

        StructuredBuffer<VegetationVisible> _Visible;

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        float4 _TintMin;
        float4 _TintMax;
        float _WindStrength;
        float _WindSpeed;
        float4 _WindDirection;
        float _WindFrequency;
        float _Smoothness;
        float _Translucency;
        float _SheenStrength;
        float _SheenPower;
        float _GlobalScale;
        float _AmbientStrength;
        float _Cutoff;

        // 场景控制脚本（GrassRuntimeControl）设置的全局覆盖值。
        // >0 时用它替代 _AmbientStrength，保证 Inspector 修改能实时生效
        //（shader 全局变量不经过 MPB/材质，一定能传到 shader）。
        float _GrassAmbientOverride;

        struct Attributes
        {
            float3 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
            uint instanceID : SV_InstanceID;
        };

        float Hash01(uint seed)
        {
            seed = (seed ^ 61u) ^ (seed >> 16u);
            seed *= 9u;
            seed = seed ^ (seed >> 4u);
            seed *= 0x27d4eb2du;
            seed = seed ^ (seed >> 15u);
            return frac(seed * (1.0 / 4294967296.0));
        }

        float3 RotateY(float3 v, float angle)
        {
            float s = sin(angle);
            float c = cos(angle);
            return float3(v.x * c + v.z * s, v.y, -v.x * s + v.z * c);
        }

        // Shared per-vertex work: reads the instance's visible-buffer slot, applies
        // rotation/scale/wind displacement, and returns world position + tint.
        void ComputeVegetationVertex(Attributes IN, out float3 worldPos, out float3 normalWS, out half3 tint)
        {
            VegetationVisible v = _Visible[IN.instanceID];
            worldPos = v.positionScale.xyz;
            float scale = v.positionScale.w * _GlobalScale;
            float rotY = v.rotSeedPad.x;
            uint seed = asuint(v.rotSeedPad.y);

            float3 local = RotateY(IN.positionOS, rotY) * scale;
            worldPos += local;

            float2 windDir = normalize(_WindDirection.xz + float2(1e-5, 0));
            float flow = dot(worldPos.xz, windDir) * _WindFrequency; // 沿风向的空间坐标
            float tWind = _Time.y * _WindSpeed;                      // 时间

            // 多层正弦叠加：不同频率/相位合成类噪声的复合波形
            float wave = sin(flow + tWind);
            wave += 0.7 * sin(flow * 1.9 + tWind * 1.5 + 2.1);
            wave += 0.4 * sin(flow * 3.7 + tWind * 2.3 + 4.5);
            wave *= 0.5; // 归一化到约 [-1, 1]

            // 低频阵风包络：风"一阵强一阵弱"，产生一波一波的传播感
            float gust = 0.5 + 0.5 * sin(flow * 0.25 - tWind * 0.4 + 1.3);

            // 每株草的个体差异（小相位偏移，保留差异但不破坏整体波场）
            float perInstanceOffset = (Hash01(seed) - 0.5) * 0.25;

            float wind = (wave * lerp(0.3, 1.0, gust) + perInstanceOffset) * _WindStrength;
            worldPos.xz += windDir * wind * IN.uv.y * 0.15;

            // 草地统一用朝上法线（billboard normal）。十字片是双面渲染(Cull Off)，
            // 若用法线水平朝外的真实法线，背光叶片的 ndotl=0 会让整片纯黑，
            // 且 SampleSH 采样环境光的方向也不对，导致调 Environment Lighting 没反应。
            // 朝上法线让方向光 + 环境光对每片草都稳定、均匀地响应。
            normalWS = float3(0.0, 1.0, 0.0);

            float tColor = Hash01(seed + 1u);   // 主色调
            float tLuma = Hash01(seed + 3u);    // 明暗斑驳
            tint = lerp(_TintMin.rgb, _TintMax.rgb, tColor) * lerp(0.75, 1.25, tLuma);
        }

        // 草叶 albedo：垂直渐变（根部暗、梢部偏黄亮），更接近真实草叶
        half3 VegetationAlbedo(half3 tint, float uvY)
        {
            half3 root = tint * 0.7;
            half3 tip = tint * half3(1.08, 1.02, 0.8) * 1.15;
            return lerp(root, tip, uvY);
        }
        ENDHLSL

        // ---------------------------------------------------------------------
        // Forward (used by compatibility-mode renderers / forward-only fallback)
        // ---------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                half3 tint : TEXCOORD2;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 worldPos, normalWS;
                half3 tint;
                ComputeVegetationVertex(IN, worldPos, normalWS, tint);

                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.normalWS = normalWS;
                OUT.uv = IN.uv;
                OUT.tint = tint;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                clip(tex.a - _Cutoff);

                Light mainLight = GetMainLight();
                float3 normalWS = normalize(IN.normalWS);
                half ndotl = saturate(dot(normalWS, mainLight.direction)) * 0.5 + 0.5;

                half3 albedo = VegetationAlbedo(tex.rgb * IN.tint, IN.uv.y);
                half3 color = albedo * mainLight.color * ndotl;
                return half4(color, 1);
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------
        // GBuffer (Deferred renderer). Lets grass participate in deferred
        // lighting: shadows and additional lights are resolved later by the
        // renderer's deferred lighting pass, same as every other opaque.
        // ---------------------------------------------------------------------
        Pass
        {
            Name "GBuffer"
            Tags { "LightMode" = "UniversalGBuffer" }
            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles3 glcore
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutput.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                half3 tint : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 worldPos, normalWS;
                half3 tint;
                ComputeVegetationVertex(IN, worldPos, normalWS, tint);

                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.normalWS = normalWS;
                OUT.uv = IN.uv;
                OUT.tint = tint;
                OUT.positionWS = worldPos;
                OUT.shadowCoord = TransformWorldToShadowCoord(worldPos);
                return OUT;
            }

            GBufferFragOutput frag(Varyings IN)
            {
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                clip(tex.a - _Cutoff);

                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.positionCS = IN.positionCS;
                inputData.normalWS = normalize(IN.normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord = IN.shadowCoord;
                inputData.fogCoord = 0;
                inputData.vertexLighting = half3(0, 0, 0);
                // 环境光 = SH（方向性）+ _GlossyEnvironmentColor 兜底（URP 全局，保证非零）
                // 乘数优先用 _GrassAmbientOverride（场景脚本实时设置），否则用材质/MPB 的 _AmbientStrength
                half3 ambientGI = max(SampleSH(inputData.normalWS), _GlossyEnvironmentColor.rgb);
                float ambientMul = _GrassAmbientOverride > 0.0 ? _GrassAmbientOverride : _AmbientStrength;
                inputData.bakedGI = ambientGI * ambientMul;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                // albedo = 纹理 RGB × 逐株 tint（写实：草叶颜色来自贴图，tint 做逐株变化）
                half3 albedo = VegetationAlbedo(tex.rgb * IN.tint, IN.uv.y);
                half occlusion = 1.0h;
                half alpha = 1.0h;

                // 草地用半 Lambert 提升 diffuse 权重（草叶多散射，高光不该被 diffuse 压制）。
                // 反射率走 0.04 介电常数，配合 GrassDeferred 里的 sheen 高光项。
                half reflectivity = 0.04h;
                BRDFData brdfData;
                InitializeBRDFDataDirect(albedo,
                    albedo * (1.0h - reflectivity),          // diffuse
                    half3(reflectivity, reflectivity, reflectivity), // specular（介电）
                    reflectivity,
                    1.0h - reflectivity,
                    _Smoothness, alpha, brdfData);

                Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
                MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);
                half3 color = GlobalIllumination(brdfData, (BRDFData)0, 0, inputData.bakedGI, occlusion,
                                                  inputData.positionWS, inputData.normalWS, inputData.viewDirectionWS,
                                                  inputData.normalizedScreenSpaceUV);

                GBufferFragOutput output = PackGBuffersBRDFData(brdfData, inputData, _Smoothness, color, occlusion);

                // 打上草地 shading model 标记，延迟光照阶段走 GrassDeferredLighting
                uint flags = UnpackGBufferMaterialFlags(output.gBuffer0.a) | kMaterialFlagGrass;
                output.gBuffer0.a = PackGBufferMaterialFlags(flags);

                // 草地参数写入 customData（延迟光照阶段读取）：
                //   R = 透光强度 translucency
                //   G = sheen 高光强度
                //   B = sheen 高光宽度（power / 16，因为 customData 是 UNorm 0~1）
                //   A = ambient 强度（/ 5 归一化；延迟阶段实际不用，环境光已在上面 bakedGI 生效）
                output.customData = half4(_Translucency, _SheenStrength, _SheenPower * (1.0h / 16.0h), _AmbientStrength * 0.2h);

                return output;
            }
            ENDHLSL
        }
    }
}
