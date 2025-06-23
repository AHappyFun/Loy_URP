Shader "Hidden/Universal Render Pipeline/GaeaFxaa"
{
    HLSLINCLUDE
        
        #pragma exclude_renderers gles

    ENDHLSL

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        
        Pass
        {
            //Stencil
            //{
            //    WriteMask [_StencilMask]
            //    Ref [_StencilRef]
            //    Comp Always
            //    Pass Replace
            //}

            HLSLPROGRAM
            
                #pragma multi_compile_local_fragment _ HDR_INPUT
        
                #pragma vertex DefaultPassVertex
                #pragma fragment FXAAPassFragment2
                #include "GaeaFxaa.hlsl"

            ENDHLSL
        }
        
    }
}
