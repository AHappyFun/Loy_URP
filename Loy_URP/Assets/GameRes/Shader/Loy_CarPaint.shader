Shader "Loy/CarPaint"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor("Paint Color", Color) = (0.8, 0.82, 0.86, 1)

        [Header(Paint Layers)]
        _Metallic("Base Metallic", Range(0, 1)) = 0.04
        _Smoothness("Base Coat Smoothness", Range(0, 1)) = 0.78
        _Occlusion("Occlusion", Range(0, 1)) = 1
        _ClearCoatMask("Clear Coat Strength", Range(0, 1)) = 0.85
        _ClearCoatSmoothness("Clear Coat Smoothness", Range(0, 1)) = 0.96
        _GeometricSpecularAA("Geometric Specular AA", Range(0, 1)) = 0.3

        [Header(Normal)]
        [Toggle(_NORMAL_MAP)] _NormalMapToggle("Use Normal Map", Float) = 0
        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Strength", Range(0, 2)) = 1

        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2
        [HideInInspector] _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "UniversalMaterialType" = "Lit"
        }

        Pass
        {
            Name "CarPaintGBuffer"
            Tags { "LightMode" = "UniversalGBuffer" }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles3 glcore
            #pragma vertex CarPaintGBufferVert
            #pragma fragment CarPaintGBufferFrag

            #pragma shader_feature_local _NORMAL_MAP
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_fragment _ _RENDER_PASS_ENABLED
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Input.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutput.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Metallic;
                half _Smoothness;
                half _Occlusion;
                half _ClearCoatMask;
                half _ClearCoatSmoothness;
                half _GeometricSpecularAA;
                half _BumpScale;
            CBUFFER_END

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                float2 lightmapUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv : TEXCOORD0;
                DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 1);
                float3 positionWS : TEXCOORD2;
                half3 normalWS : TEXCOORD3;
                half4 tangentWS : TEXCOORD4;
                float4 positionCS : SV_POSITION;
                #ifdef USE_APV_PROBE_OCCLUSION
                    float4 probeOcclusion : TEXCOORD5;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings CarPaintGBufferVert(Attributes input)
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
                output.tangentWS = half4(normalInputs.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                OUTPUT_LIGHTMAP_UV(input.lightmapUV, unity_LightmapST, output.lightmapUV);
                OUTPUT_SH4(positionInputs.positionWS, output.normalWS,
                    GetWorldSpaceNormalizeViewDir(positionInputs.positionWS), output.vertexSH, output.probeOcclusion);
                return output;
            }

            GBufferFragOutput CarPaintGBufferFrag(Varyings input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
                #if defined(_NORMAL_MAP)
                    half3 bitangentWS = input.tangentWS.w * cross(normalWS, input.tangentWS.xyz);
                    half3 normalTS = UnpackNormalScale(
                        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                    normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(
                        normalTS, half3x3(input.tangentWS.xyz, bitangentWS, normalWS)));
                #endif

                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

                // URP deferred has one specular lobe. Fold the clear coat into a stable,
                // high-smoothness dielectric surface instead of perturbing reflection per pixel.
                half paintSmoothness = saturate(lerp(_Smoothness, _ClearCoatSmoothness, _ClearCoatMask));
                half paintMetallic = saturate(_Metallic);

                // Imported vehicle panels contain many disconnected boundary edges. At high
                // smoothness their normal discontinuities sample very different bright probe
                // directions and become white seams. Increase roughness only where the screen-
                // space geometric normal changes rapidly (geometric specular anti-aliasing).
                float3 normalDx = ddx(normalWS);
                float3 normalDy = ddy(normalWS);
                half normalVariance = saturate(dot(normalDx, normalDx) + dot(normalDy, normalDy));
                half geometricRoughness = min(0.24h, sqrt(normalVariance) * _GeometricSpecularAA);
                half paintRoughness = max(1.0h - paintSmoothness, geometricRoughness);
                paintSmoothness = 1.0h - saturate(paintRoughness);

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.vertexLighting = half3(0, 0, 0);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(0, 0, 0, 0);

                #if !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
                    inputData.bakedGI = SAMPLE_GI(input.vertexSH,
                        GetAbsolutePositionWS(input.positionWS), normalWS, inputData.viewDirectionWS,
                        input.positionCS.xy, input.probeOcclusion, inputData.shadowMask);
                #elif defined(LIGHTMAP_ON)
                    #ifdef UNITY_LIGHTMAP_FULL_HDR
                        bool encodedLightmap = false;
                    #else
                        bool encodedLightmap = true;
                    #endif
                    half4 decodeInstructions = half4(LIGHTMAP_HDR_MULTIPLIER, LIGHTMAP_HDR_EXPONENT, 0, 0);
                    inputData.bakedGI = SampleSingleLightmap(
                        TEXTURE2D_LIGHTMAP_ARGS(unity_Lightmap, samplerunity_Lightmap),
                        input.lightmapUV, half4(1, 1, 0, 0), encodedLightmap, decodeInstructions);
                #else
                    inputData.bakedGI = SampleSHPixel(input.vertexSH, normalWS);
                #endif

                #ifdef LIGHTMAP_ON
                    inputData.shadowMask = SAMPLE_TEXTURE2D(unity_ShadowMask, samplerunity_ShadowMask, input.lightmapUV);
                #endif

                BRDFData brdfData;
                half alpha = 1;
                InitializeBRDFData(baseSample.rgb, paintMetallic, half3(0, 0, 0), paintSmoothness, alpha, brdfData);

                Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
                MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);
                half3 gi = GlobalIllumination(brdfData, (BRDFData)0, 0,
                    inputData.bakedGI, _Occlusion, inputData.positionWS, inputData.normalWS,
                    inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);

                return PackGBuffersBRDFData(brdfData, inputData, paintSmoothness, gi, _Occlusion);
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
        UsePass "Universal Render Pipeline/Lit/DepthNormals"
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
