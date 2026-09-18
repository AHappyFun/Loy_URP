Shader "Loy/AutomotiveGlass"
{
    Properties
    {
        [MainTexture] _BaseMap("Tint Map", 2D) = "white" {}
        [MainColor] _BaseColor("Glass Tint", Color) = (0.08, 0.12, 0.16, 1)
        _Opacity("Center Opacity", Range(0, 1)) = 0.26
        _EdgeOpacity("Edge Opacity", Range(0, 1)) = 0.48
        _Smoothness("Smoothness", Range(0, 1)) = 0.97
        _IOR("Index Of Refraction", Range(1.0, 2.5)) = 1.5
        _FresnelPower("Fresnel Power", Range(1, 8)) = 5
        _ReflectionStrength("Reflection Strength", Range(0, 3)) = 1.25
        [HDR] _ReflectionTint("Reflection Tint", Color) = (1, 1, 1, 1)
        _ReflectionFloor("Minimum Reflection", Range(0, 0.25)) = 0.04
        [NoScaleOffset] _ReflectionMap("Sharp Environment HDRI", 2D) = "black" {}
        _ReflectionMapWeight("HDRI Reflection Weight", Range(0, 1)) = 0
        _ReflectionExposure("HDRI Reflection Exposure", Range(0, 2)) = 1
        _ReflectionRotation("HDRI Rotation", Range(0, 360)) = 0
        _SpecularStrength("Direct Specular", Range(0, 6)) = 2.0

        [Header(Normal)]
        [Toggle(_NORMAL_MAP)] _NormalMapToggle("Use Normal Map", Float) = 0
        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Strength", Range(0, 2)) = 0.35

        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2
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
            Name "AutomotiveGlassForward"
            Tags { "LightMode" = "UniversalForward" }

            // Premultiplied blending keeps reflected radiance visible instead of
            // multiplying it by the already-low glass opacity a second time.
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex GlassVert
            #pragma fragment GlassFrag
            #pragma shader_feature_local _NORMAL_MAP
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Opacity;
                half _EdgeOpacity;
                half _Smoothness;
                half _IOR;
                half _FresnelPower;
                half _ReflectionStrength;
                half4 _ReflectionTint;
                half _ReflectionFloor;
                half _ReflectionMapWeight;
                half _ReflectionExposure;
                half _ReflectionRotation;
                half _SpecularStrength;
                half _BumpScale;
            CBUFFER_END

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
            TEXTURE2D(_ReflectionMap); SAMPLER(sampler_ReflectionMap);

            float2 DirectionToLatLong(float3 directionWS)
            {
                directionWS = normalize(directionWS);
                float2 uv;
                uv.x = atan2(directionWS.z, directionWS.x) * INV_TWO_PI + 0.5;
                uv.y = asin(clamp(directionWS.y, -1.0, 1.0)) * INV_PI + 0.5;
                uv.x = frac(uv.x + _ReflectionRotation / 360.0);
                return uv;
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                half3 normalOS : NORMAL;
                half4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half4 tangentWS : TEXCOORD2;
                float2 uv : TEXCOORD3;
                half fogFactor : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings GlassVert(Attributes input)
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
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                return output;
            }

            half4 GlassFrag(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
                #if defined(_NORMAL_MAP)
                    half3 bitangentWS = input.tangentWS.w * cross(normalWS, input.tangentWS.xyz);
                    half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                    normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS,
                        half3x3(input.tangentWS.xyz, bitangentWS, normalWS)));
                #endif
                normalWS *= IS_FRONT_VFACE(face, 1.0h, -1.0h);

                half4 tintSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                half3 viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                half NoV = saturate(dot(normalWS, viewDirectionWS));
                half f0 = pow((_IOR - 1.0h) / max(_IOR + 1.0h, 0.001h), 2.0h);
                half fresnel = f0 + (1.0h - f0) * pow(1.0h - NoV, _FresnelPower);

                half3 reflectDirectionWS = reflect(-viewDirectionWS, normalWS);
                half3 probeReflection = GlossyEnvironmentReflection(
                    reflectDirectionWS, input.positionWS, 1.0h - _Smoothness, 1.0h,
                    GetNormalizedScreenSpaceUV(input.positionCS));
                float2 reflectionUV = DirectionToLatLong(reflectDirectionWS);
                half3 hdriReflection = SAMPLE_TEXTURE2D_LOD(
                    _ReflectionMap, sampler_ReflectionMap, reflectionUV, 0).rgb * _ReflectionExposure;
                half3 reflection = lerp(probeReflection, hdriReflection,
                    saturate(_ReflectionMapWeight)) * _ReflectionTint.rgb;

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half3 halfDirection = SafeNormalize(mainLight.direction + viewDirectionWS);
                half NoL = saturate(dot(normalWS, mainLight.direction));
                half NoH = saturate(dot(normalWS, halfDirection));
                half specularPower = exp2(10.0h * _Smoothness + 1.0h);
                half3 directSpecular = mainLight.color * (mainLight.distanceAttenuation * mainLight.shadowAttenuation) *
                                       NoL * pow(NoH, specularPower) * _SpecularStrength;

                half edgeFactor = pow(1.0h - NoV, max(_FresnelPower * 0.5h, 1.0h));
                half surfaceAlpha = saturate(lerp(_Opacity, _EdgeOpacity, edgeFactor) * tintSample.a);
                half reflectionWeight = saturate(max(fresnel, _ReflectionFloor) * _ReflectionStrength);

                half3 transmittedTint = tintSample.rgb * (0.22h + 0.10h * NoL);
                half3 reflectedLight = reflection + directSpecular;

                // Premultiplied transmission plus an unattenuated reflection layer.
                // This preserves the highlight/probe reflection on a mostly transparent lens.
                half3 color = transmittedTint * surfaceAlpha + reflectedLight * reflectionWeight;
                color = MixFog(color, input.fogFactor);

                half alpha = saturate(surfaceAlpha + reflectionWeight * (1.0h - surfaceAlpha));
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
