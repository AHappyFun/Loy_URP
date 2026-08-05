Shader "Loy/Feature/SSR_View"
{
    Properties
    {

    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }

        Pass
        {
            Name "SSR Compute"
            ZTest Always Cull Off ZWrite Off
            //Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #include "SSR.hlsl"
            #pragma vertex vert
            #pragma fragment frag

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                OUT.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {

                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float2 uv = IN.positionCS.xy / _ScaledScreenParams.xy;

                return SSRRaymarchHIZ(uv);
                //return SSRRaymarch(uv);
            }

            ENDHLSL
        }

        Pass
        {
            Name "SSR Combine"
            ZTest Always Cull Off ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #include "SSR.hlsl"
            #pragma vertex vert
            #pragma fragment frag

            TEXTURE2D_X(_SSRResultTex);
            TEXTURE2D_X(_SSRHistoryTex);

            TEXTURE2D_X_HALF(_GBuffer0);
            TEXTURE2D_X_HALF(_GBuffer1);
            SamplerState my_point_clamp_sampler;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                OUT.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);

                return OUT;
            }

            float3 FresnelSchlick(float cosTheta, float3 F0)
            {
                return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
            }

            float4 SampleSSR(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X(_SSRResultTex, sampler_LinearClamp, uv);
            }

            float3 BlurSSR(float2 baseUV, float roughness)
            {
                float2 pixel = 1.0 / _ScreenParams.xy;
                float radius = roughness * 6; // 模糊范围随粗糙度增大

                float3 sum = 0;
                float total = 0;

                [unroll]
                for (int x = -1; x <= 1; x++)
                {
                    [unroll]
                    for (int y = -1; y <= 1; y++)
                    {
                        float2 offset = float2(x, y) * pixel * radius;
                        float2 uv = baseUV + offset;

                        float w = exp(-dot(float2(x, y), float2(x, y)) * 0.5);
                        sum += SampleSSR(uv) * w;
                        total += w;
                    }
                }

                return sum / total;
            }

            half4 frag(Varyings IN) : SV_Target
            {

                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float2 uv = IN.positionCS.xy / _ScaledScreenParams.xy;

                float4 buffer1 =  SAMPLE_TEXTURE2D_X(_GBuffer1, my_point_clamp_sampler, uv);

                float roughness = buffer1.g;

                float ao = buffer1.a;
                float metallic = buffer1.b;


                float4 ssrRes = SampleSSR(uv);
                ssrRes.rgb = BlurSSR(uv, roughness);
                float3 ssrColor = ssrRes;

                //结合历史图，TAA抗闪烁
                float3 lastFrameSSR = SAMPLE_TEXTURE2D_X(_SSRHistoryTex, sampler_LinearClamp, uv).rgb;
                float confidence = ssrRes.a; // 命中可靠度
                float stability = lerp(0.3, 0.95, confidence);
                ssrColor = lerp(ssrColor, lastFrameSSR, stability);

                float3 normalWS = SAMPLE_TEXTURE2D(_GBuffer2, sampler_GBuffer2, uv);
                float3 viewDirWS = normalize(_WorldSpaceCameraPos - TransformObjectToWorld(float3(0,0,0)));
                float NdotV = saturate(dot(normalWS, viewDirWS));

                float3 F0 = lerp(0.04, ssrColor, metallic);
                float3 fresnel = FresnelSchlick(NdotV, F0);

                float mask =  ao * fresnel * ssrRes.a;

                return float4(ssrColor.rgb, mask);
            }

            ENDHLSL
        }
    }
}
