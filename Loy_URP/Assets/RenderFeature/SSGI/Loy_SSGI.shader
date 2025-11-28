Shader "Loy/Feature/SSGI"
{
    Properties
    {
        _MainTex("Base (RGB)", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Pass
        {
            Name "SSGI Compute"
            ZTest Always Cull Off ZWrite Off

            HLSLPROGRAM
            #include "SSGI.hlsl"
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

                float2 uv = IN.positionCS.xy / GetGITexSize().xy;

                return SSGIRaymarch(uv);
            }

            ENDHLSL
        }

        Pass
        {
            Name "SSR Blur V"
            ZTest Always Cull Off ZWrite Off

            HLSLPROGRAM
            #include "SSGI.hlsl"
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

                float2 uv = IN.positionCS.xy / GetGITexSize().xy;

                return SSGI_BlurV(uv);
            }

            ENDHLSL
        }

        Pass
        {
            Name "SSR Blur H"
            ZTest Always Cull Off ZWrite Off

            HLSLPROGRAM
            #include "SSGI.hlsl"
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

                float2 uv = IN.positionCS.xy / GetGITexSize().xy;

                return SSGI_BlurH(uv);
            }

            ENDHLSL
        }

        Pass
        {
            Name "SSGI Combine"
            ZTest Always Cull Off ZWrite Off
            Blend SrcAlpha One

            HLSLPROGRAM
            #include "SSGI.hlsl"
            #pragma vertex vert
            #pragma fragment frag

            TEXTURE2D_X(_HBAOResultTex);
            SAMPLER(sampler_HBAOResultTex);

            float _GIRange;

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

                float2 uv = IN.positionCS.xy /  _ScaledScreenParams.xy;

                float ao = SAMPLE_TEXTURE2D_X(_HBAOResultTex, sampler_HBAOResultTex, uv).x;
                //return ao;

                return float4(SampleSSGI(uv).rgb * ao, _GIRange);
            }

            ENDHLSL
        }
    }
}