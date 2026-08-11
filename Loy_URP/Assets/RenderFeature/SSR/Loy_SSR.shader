Shader "Loy/Feature/SSR_View"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }

        Pass
        {
            Name "SSR Compute"
            ZTest Always Cull Off ZWrite Off

            HLSLPROGRAM
            #include "SSR.hlsl"
            #pragma vertex vert
            #pragma fragment frag

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.positionCS.xy / _ScaledScreenParams.xy;
                return SSRRaymarchHIZ(uv);
            }
            ENDHLSL
        }

        Pass
        {
            Name "SSR Temporal Resolve"
            ZTest Always Cull Off ZWrite Off

            HLSLPROGRAM
            #include "SSR.hlsl"
            #pragma vertex vert
            #pragma fragment frag

            TEXTURE2D_X(_SSRResultTex);
            TEXTURE2D_X(_SSRHistoryTex);
            TEXTURE2D_X(_MotionVectorTexture);
            TEXTURE2D_X_HALF(_GBuffer1);

            float _SSRHistoryValid;

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                return output;
            }

            float4 SampleCurrentSSR(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X(_SSRResultTex, sampler_LinearClamp, uv);
            }

            float3 BlurCurrentSSR(float2 baseUV, float roughness)
            {
                float2 pixel = rcp(_ScaledScreenParams.xy);
                float radius = roughness * 6.0;
                float3 sum = 0;
                float total = 0;

                [unroll]
                for (int x = -1; x <= 1; ++x)
                {
                    [unroll]
                    for (int y = -1; y <= 1; ++y)
                    {
                        float weight = exp(-dot(float2(x, y), float2(x, y)) * 0.5);
                        sum += SampleCurrentSSR(baseUV + float2(x, y) * pixel * radius).rgb * weight;
                        total += weight;
                    }
                }
                return sum / max(total, 1e-4);
            }

            void CurrentNeighborhood(float2 uv, out float3 minimum, out float3 maximum)
            {
                float2 pixel = rcp(_ScaledScreenParams.xy);
                minimum = 1e20;
                maximum = -1e20;

                [unroll]
                for (int x = -1; x <= 1; ++x)
                {
                    [unroll]
                    for (int y = -1; y <= 1; ++y)
                    {
                        float3 value = SampleCurrentSSR(uv + float2(x, y) * pixel).rgb;
                        minimum = min(minimum, value);
                        maximum = max(maximum, value);
                    }
                }
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.positionCS.xy / _ScaledScreenParams.xy;
                float4 current = SampleCurrentSSR(uv);
                float roughness = SAMPLE_TEXTURE2D_X(_GBuffer1, sampler_PointClamp, uv).g;
                current.rgb = BlurCurrentSSR(uv, roughness);

                float2 motion = SAMPLE_TEXTURE2D_X(_MotionVectorTexture, sampler_PointClamp, uv).xy;
                float2 historyUV = uv - motion;
                float historyWeight = _SSRHistoryValid;
                historyWeight *= all(historyUV > 0.0) && all(historyUV < 1.0);
                historyWeight *= lerp(0.65, 0.92, saturate(current.a));

                if (historyWeight > 0.0)
                {
                    float3 history = SAMPLE_TEXTURE2D_X(_SSRHistoryTex, sampler_LinearClamp, historyUV).rgb;
                    float3 neighborhoodMin;
                    float3 neighborhoodMax;
                    CurrentNeighborhood(uv, neighborhoodMin, neighborhoodMax);
                    history = clamp(history, neighborhoodMin, neighborhoodMax);
                    current.rgb = lerp(current.rgb, history, historyWeight);
                }

                return current;
            }
            ENDHLSL
        }

        Pass
        {
            Name "SSR Composite"
            ZTest Always Cull Off ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #include "SSR.hlsl"
            #pragma vertex vert
            #pragma fragment frag

            TEXTURE2D_X(_SSRResolvedTex);
            TEXTURE2D_X_HALF(_GBuffer1);

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                return output;
            }

            float3 FresnelSchlick(float cosTheta, float3 f0)
            {
                return f0 + (1.0 - f0) * pow(1.0 - cosTheta, 5.0);
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.positionCS.xy / _ScaledScreenParams.xy;
                float4 resolved = SAMPLE_TEXTURE2D_X(_SSRResolvedTex, sampler_LinearClamp, uv);
                float4 gbuffer1 = SAMPLE_TEXTURE2D_X(_GBuffer1, sampler_PointClamp, uv);
                float3 normalWS = normalize(SAMPLE_TEXTURE2D_X(_GBuffer2, sampler_GBuffer2, uv).rgb);

                float rawDepth = SampleSceneDepth(uv);
                float3 positionWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 viewDirectionWS = normalize(_WorldSpaceCameraPos - positionWS);
                float nDotV = saturate(dot(normalWS, viewDirectionWS));

                float ao = gbuffer1.a;
                float metallic = gbuffer1.b;
                float3 f0 = lerp(0.04.xxx, resolved.rgb, metallic);
                float3 fresnel = FresnelSchlick(nDotV, f0);
                float mask = saturate(ao * Max3(fresnel.r, fresnel.g, fresnel.b) * resolved.a);
                return float4(resolved.rgb, mask);
            }
            ENDHLSL
        }
    }
}
