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
        float _Cutoff;

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
            float scale = v.positionScale.w;
            float rotY = v.rotSeedPad.x;
            uint seed = asuint(v.rotSeedPad.y);

            float3 local = RotateY(IN.positionOS, rotY) * scale;
            worldPos += local;

            float2 windDir = normalize(_WindDirection.xz + float2(1e-5, 0));
            float perInstanceOffset = (Hash01(seed) - 0.5) * 0.6; // subtle per-blade variation, not full randomization
            float wave = sin(_Time.y * _WindSpeed + dot(worldPos.xz, windDir) * _WindFrequency + perInstanceOffset);
            worldPos.xz += windDir * wave * _WindStrength * IN.uv.y * 0.15;

            normalWS = TransformObjectToWorldNormal(RotateY(IN.normalOS, rotY));

            float t = Hash01(seed + 1u);
            tint = lerp(_TintMin.rgb, _TintMax.rgb, t);
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

                half3 color = tex.rgb * IN.tint * mainLight.color * ndotl;
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
                inputData.bakedGI = half3(0, 0, 0);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                half3 albedo = tex.rgb * IN.tint;
                half occlusion = 1.0h;
                half alpha = 1.0h;

                BRDFData brdfData;
                InitializeBRDFData(albedo, 0.0h, half3(0, 0, 0), _Smoothness, alpha, brdfData);

                Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
                MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);
                half3 color = GlobalIllumination(brdfData, (BRDFData)0, 0, inputData.bakedGI, occlusion,
                                                  inputData.positionWS, inputData.normalWS, inputData.viewDirectionWS,
                                                  inputData.normalizedScreenSpaceUV);

                return PackGBuffersBRDFData(brdfData, inputData, _Smoothness, color, occlusion);
            }
            ENDHLSL
        }
    }
}
