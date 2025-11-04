Shader "Loy/Feature/SSR"
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

                return SSRRaymarch(uv);
            }

            ENDHLSL
        }

        Pass
        {
            Name "SSR Combine"
            ZTest Always Cull Off ZWrite Off
            //Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #include "SSR.hlsl"
            #pragma vertex vert
            #pragma fragment frag

            TEXTURE2D_X(_SSRResultTex);
            SAMPLER(sampler_LinearClamp);

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

            half4 frag(Varyings IN) : SV_Target
            {

                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float2 uv = IN.positionCS.xy / _ScaledScreenParams.xy;

                float4 ssrUV = SAMPLE_TEXTURE2D_X(_SSRResultTex, sampler_LinearClamp, uv);
                float3 ssrColor = SampleSceneColor(ssrUV.xy);

                float3 sceneColor = SampleSceneColor(uv);
                float3 buffer0 =  SAMPLE_TEXTURE2D_X(_GBuffer1, my_point_clamp_sampler, uv);
                float3 buffer1 =  SAMPLE_TEXTURE2D_X(_GBuffer1, my_point_clamp_sampler, uv);

                float3 normalWS = SAMPLE_TEXTURE2D(_GBuffer2, sampler_GBuffer2, uv);
                float3 viewDirWS = normalize(_WorldSpaceCameraPos - TransformObjectToWorld(float3(0,0,0)));
                float NdotV = saturate(dot(normalWS, viewDirWS));

                float3 F0 = lerp(0.04, buffer0, buffer1.b);
                float3 fresnel = FresnelSchlick(NdotV, F0);

                float mask = (1 - buffer1.g) * fresnel;
                //return fresnel.rrrr;

                return float4(sceneColor * (1 - mask) + mask * ssrColor.rgb, 1);
            }

            ENDHLSL
        }
    }
}
