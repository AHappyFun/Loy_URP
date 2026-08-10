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
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

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

        Pass
        {
            Name "HBAO ApplyToGI"
            ZTest Always Cull Off ZWrite Off
            // dst.rgb = src*1 + dst*SrcAlpha = 0 + dst*ao = dst*ao
            // dst.a   = src*0 + dst*1 = dst.a（不变）
            Blend One SrcAlpha, Zero One

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

                // 本 pass 写全分辨率颜色目标（GBuffer3 = GI + 自发光），
                // _ScaledScreenParams 是全分辨率尺寸，所以这是全屏 [0,1] uv
                float2 uv = IN.positionCS.xy / _ScaledScreenParams.xy;
                float ao = SAMPLE_TEXTURE2D_X(_HBAOResultTex, sampler_HBAOResultTex, uv).r;

                // 只把 AO 压进间接光；直接光随后由延迟光照 pass 加性叠加
                return half4(0.0, 0.0, 0.0, ao);
            }

            ENDHLSL
        }

    }
}