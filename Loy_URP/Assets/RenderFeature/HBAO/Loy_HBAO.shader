Shader "Loy/Feature/HBAO"
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
            Name "HBAO Compute"
            ZTest Always Cull Off ZWrite Off

            HLSLPROGRAM
            #include "HBAO.hlsl"
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

                float2 uv = IN.positionCS.xy / GetAOTexSize().xy;
                
                return HBAORaymarch(uv);
            }

            ENDHLSL
        }

        Pass
        {
            Name "HBAO Blur V"
            ZTest Always Cull Off ZWrite Off

            HLSLPROGRAM
            #include "HBAO.hlsl"
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

                float2 uv = IN.positionCS.xy / GetAOTexSize().xy;

                return HBAO_BlurV(uv);
            }

            ENDHLSL
        }

        Pass
        {
            Name "HBAO Blur H"
            ZTest Always Cull Off ZWrite Off

            HLSLPROGRAM
            #include "HBAO.hlsl"
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

                float2 uv = IN.positionCS.xy / GetAOTexSize().xy;

                return HBAO_BlurH(uv);
            }

            ENDHLSL
        }

    }
}