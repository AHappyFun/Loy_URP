Shader "Loy/Feature/PostProcess/Glitch"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "Glitch Copy"
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Loy_Glitch.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "Glitch Basic"
            HLSLPROGRAM
            #define GLITCH_BASIC
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Loy_Glitch.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "Glitch Block"
            HLSLPROGRAM
            #define GLITCH_BLOCK
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Loy_Glitch.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "Glitch Basic + Block"
            HLSLPROGRAM
            #define GLITCH_BASIC
            #define GLITCH_BLOCK
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Loy_Glitch.hlsl"
            ENDHLSL
        }
    }
    Fallback Off
}
