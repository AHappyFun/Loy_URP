// Stylized URP skybox: horizon/zenith/ground gradient, a sun-moon disk that follows the main light,
// and scrolling clouds. Time-of-day colour changes are made by SkyController, which writes the
// consolidated colours below. Values that depend only on material properties are combined once per
// vertex and handed to the fragment shader through flat interpolators.
Shader "Atrio/StylizedSky"
{
    Properties
    {
        _SunMoonColor("Sun-Moon Color", Color) = (1, 0.9803922, 0.9019608, 1)
        _SunMoonSize("Sun-Moon Size", Range(0, 1)) = 0.044
        _SunMoonSoftness("Sun-Moon Softness", Range(0, 1)) = 0.006
        _SunMoonGlowSpread("Sun-Moon Glow Spread", Range(0.1, 2)) = 0.209
        _SunMoonGlowIntensity("Sun-Moon Glow Intensity", Range(0, 5)) = 1
        [Space] _ZenithColor("Zenith Color", Color) = (0.3294117, 0.5128775, 0.8745098, 1)
        _HorizonColor("Horizon Color", Color) = (0.4784314, 0.5803922, 0.6588235, 1)
        _GroundColor("Ground Color", Color) = (0.2705882, 0.3372549, 0.3529412, 1)
        [Space] _HorizonSharpness("Horizon Sharpness", Range(0, 1)) = 0.5
        _HorizonOffset("Horizon Offset", Range(-1, 1)) = 0.1
        [Space] _CloudColor("Cloud Color", Color) = (1, 1, 1, 1)
        _CloudShadowColor("Cloud Shadow Color", Color) = (0.764151, 0.764151, 0.764151, 1)
        [NoScaleOffset] _CloudTexture("Cloud Texture", 2D) = "white" {}
        [Space] _CloudCoverage("Cloud Coverage", Range(0, 2)) = 0.5
        _CloudSharpness("Cloud Sharpness", Range(0, 1)) = 0.5
        _CloudScale("Cloud Scale", Vector) = (0, 0, 0, 0)
        [Space] _CloudSpeed("Cloud Speed", Range(0, 1)) = 0.015
        _CloudDirection("Cloud Direction", Vector) = (1, 0.4, 0, 0)
        [Space] _CloudRimLighting("Cloud Rim Lighting", Range(0, 5)) = 2
        _CloudGlobalBrightness("Cloud Global Brightness", Range(0, 5)) = 1
        _CloudDistortionScaleMultiplier("Cloud Distortion Scale Multiplier", Range(0.01, 10)) = 1
        _CloudDistortionSpeed("Cloud Distortion Speed", Range(-1, 1)) = 0
        _CloudDistortionOffset("Distortion Offset", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off
        ZWrite Off

        HLSLINCLUDE
        #pragma target 3.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _SunMoonColor;
            float _SunMoonSize;
            float _SunMoonSoftness;
            float _SunMoonGlowSpread;
            float _SunMoonGlowIntensity;
            half4 _ZenithColor;
            half4 _HorizonColor;
            half4 _GroundColor;
            float _HorizonSharpness;
            float _HorizonOffset;
            half4 _CloudColor;
            half4 _CloudShadowColor;
            float _CloudSpeed;
            float2 _CloudDirection;
            float _CloudCoverage;
            float _CloudSharpness;
            float2 _CloudScale;
            float _CloudRimLighting;
            float _CloudGlobalBrightness;
            float _CloudDistortionScaleMultiplier;
            float _CloudDistortionSpeed;
            float _CloudDistortionOffset;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "Sky"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            TEXTURE2D(_CloudTexture);
            SAMPLER(sampler_CloudTexture);
            // Added to the view ray's height before projecting onto the cloud plane. Larger values
            // flatten the dome (less squashing at the horizon); smaller values make it more planar.
            #define CLOUD_PLANE_BEND 0.5

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS                   : SV_POSITION;
                float3 viewDir                      : TEXCOORD0; // unnormalized; the sky mesh is centred on the camera
                nointerpolation float4 cloudOffset  : TEXCOORD1; // cloud-plane units (x = east, z = north); xy: clouds, zw: distortion
                nointerpolation half4 diskColor     : TEXCOORD2; // w: glow exponent
                nointerpolation half4 glowColor     : TEXCOORD3; // w: -1 / softness
                nointerpolation half3 horizonColor  : TEXCOORD4;
                nointerpolation half3 zenithDelta   : TEXCOORD5; // zenith - horizon
                nointerpolation half3 groundDelta   : TEXCOORD6; // ground - horizon
                nointerpolation half4 cloudShadow   : TEXCOORD7; // w: rim exponent
                nointerpolation half4 cloudLit      : TEXCOORD8; // w: 1 / cloud edge width
                nointerpolation half3 cloudRim      : TEXCOORD9;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.viewDir = input.positionOS.xyz;

                // The distortion layer drifts along the same heading, at a signed fraction of the cloud speed.
                float2 cloudTravel = _CloudSpeed * _TimeParameters.x * normalize(_CloudDirection);
                output.cloudOffset = float4(cloudTravel, cloudTravel * _CloudDistortionSpeed);

                // 2.5 and 1.2 are the disk and glow boosts carried over from the original graph.
                half cloudBrightness = half(_CloudGlobalBrightness);
                output.diskColor = half4(_SunMoonColor.rgb * 2.5, 12.0 / max(_SunMoonGlowSpread, 0.001));
                output.glowColor = half4(_SunMoonColor.rgb * half(_SunMoonGlowIntensity * 1.2), -1.0 / _SunMoonSoftness);
                output.horizonColor = _HorizonColor.rgb;
                output.zenithDelta = _ZenithColor.rgb - _HorizonColor.rgb;
                output.groundDelta = _GroundColor.rgb - _HorizonColor.rgb;
                output.cloudShadow = half4(_CloudShadowColor.rgb * cloudBrightness, max(_CloudRimLighting, 0.1));
                output.cloudLit = half4(_CloudColor.rgb * cloudBrightness, 1.0 / (1.0 - _CloudSharpness));
                output.cloudRim = _SunMoonColor.rgb * cloudBrightness;
                return output;
            }

            // Lagarde's acos approximation, kept in float: half cannot resolve angles this small near 1.
            float AcosFast(float x)
            {
                float ax = abs(x);
                float res = (0.0468878 * ax - 0.203471) * ax + 1.570796;
                res *= sqrt(1.0 - ax);
                return x >= 0.0 ? res : PI - res;
            }

            half Smooth01(half t)
            {
                return t * t * (3.0 - 2.0 * t);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 dir = normalize(input.viewDir);
                // The sun-moon disk sits where the main light comes from; URP stores that as a unit vector.
                float cosLight = dot(dir, _MainLightPosition.xyz);

                // Gradient: horizon -> zenith above the horizon line, horizon -> ground below it.
                float horizonY = dir.y - _HorizonOffset;
                bool above = horizonY >= 0.0;
                half groundT = half(saturate(horizonY * -6.0));
                half gradientT = above ? half(pow(saturate(horizonY), _HorizonSharpness)) : groundT;
                half3 color = input.horizonColor + (above ? input.zenithDelta : input.groundDelta) * gradientT;

                // Sun-moon disk and glow, faded out as the ground takes over so it sets behind the horizon.
                half disk = Smooth01(half(saturate((AcosFast(cosLight) - _SunMoonSize) * input.glowColor.w)));
                half glow = half(pow(max(cosLight, 0.0), input.diskColor.w));
                color += (input.diskColor.rgb * disk + input.glowColor.rgb * glow) * (half(1.0) - groundT);

                // Clouds sit on a plane above the camera (x = east, z = north), bent down towards the
                // horizon so they don't stretch to infinity, and scroll across it in a straight line.
                // Derivatives are taken here, outside the branch, so the sample below can use mips.
                float2 cloudPlane = dir.xz / (max(dir.y, 0.0) + CLOUD_PLANE_BEND);
                float2 uv = (cloudPlane - input.cloudOffset.xy) * _CloudScale;
                float2 uvDdx = ddx(uv);
                float2 uvDdy = ddy(uv);
                // Distortion tiles relative to the clouds: a multiplier of 10 repeats ten times as often.
                float2 distortionUV = (cloudPlane - input.cloudOffset.zw) * (_CloudScale * _CloudDistortionScaleMultiplier);
                float2 distortionDdx = ddx(distortionUV);
                float2 distortionDdy = ddy(distortionUV);

                // Clouds fade to zero at dir.y = 0.01, so skip the lower hemisphere entirely.
                UNITY_BRANCH
                if (dir.y > 0.01)
                {
                    half height = half(saturate(dir.y));
                    // A second read of the cloud texture at the distortion tiling; its RG, used as-is, pushes
                    // the cloud UVs around. The undistorted gradients are kept for mip selection, which is
                    // close enough and avoids a dependency.
                    half2 distortion = SAMPLE_TEXTURE2D_GRAD(_CloudTexture, sampler_CloudTexture, distortionUV, distortionDdx, distortionDdy).rg;
                    uv += distortion * _CloudDistortionOffset;
                    half density = SAMPLE_TEXTURE2D_GRAD(_CloudTexture, sampler_CloudTexture, uv, uvDdx, uvDdy).r;
                    half coverageEdge = half(1.0 - _CloudCoverage);
                    half cloud = Smooth01(saturate((density - coverageEdge) * input.cloudLit.w))
                        * smoothstep(half(0.01), half(0.25), height);

                    half rim = pow(half(saturate(cosLight)), input.cloudShadow.w);
                    half3 cloudColor = input.cloudRim * rim + lerp(input.cloudShadow.rgb, input.cloudLit.rgb, half(cosLight * 0.5 + 0.5));
                    color = lerp(color, cloudColor, cloud);
                }

                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // Lightmapper / GI: plain sky gradient only, no sun-moon or clouds, black below the horizon line.
        Pass
        {
            Name "Meta"
            Tags { "LightMode"="Meta" }

            HLSLPROGRAM
            #pragma vertex VertMeta
            #pragma fragment FragMeta
            #pragma shader_feature EDITOR_VISUALIZATION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv0        : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
                float2 uv2        : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 viewDir    : TEXCOORD0;
            #ifdef EDITOR_VISUALIZATION
                float2 vizUV      : TEXCOORD1;
                float4 lightCoord : TEXCOORD2;
            #endif
            };

            Varyings VertMeta(Attributes input)
            {
                Varyings output;
                output.positionCS = UnityMetaVertexPosition(input.positionOS.xyz, input.uv1, input.uv2);
                output.viewDir = input.positionOS.xyz;
            #ifdef EDITOR_VISUALIZATION
                UnityEditorVizData(input.positionOS.xyz, input.uv0, input.uv1, input.uv2, output.vizUV, output.lightCoord);
            #endif
                return output;
            }

            half4 FragMeta(Varyings input) : SV_Target
            {
                float horizonY = normalize(input.viewDir).y - _HorizonOffset;
                half3 color = horizonY >= 0.0
                    ? lerp(_HorizonColor.rgb, _ZenithColor.rgb, half(pow(saturate(horizonY), _HorizonSharpness)))
                    : half3(0.0, 0.0, 0.0);

                MetaInput metaInput = (MetaInput)0;
                metaInput.Albedo = color;
                metaInput.Emission = color;
            #ifdef EDITOR_VISUALIZATION
                metaInput.VizUV = input.vizUV;
                metaInput.LightCoord = input.lightCoord;
            #endif
                return UnityMetaFragment(metaInput);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
