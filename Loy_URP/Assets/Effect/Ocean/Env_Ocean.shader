Shader "Aley/Env/Ocean"
{
    Properties
    {
        //_BaseMap("BaseMap", 2D) = "white" {}
        
        [NormalMap]
        _NormalMap("NormalMap", 2D) = "bump" {}
        _NormalScale("NormalScale", float) = 1
        
        [Wave]
        [Toggle(_SIN_WAVE)] _Sin_Wave ("SinWave?", Float) = 0

        _AY("Y振幅", float) = 1
        _SineParam("Sine X-x方向 Y-z方向 Z-波速 W-波长", vector) = (1,1,0.2,0.33)
        
        [Toggle(_GERSTNER_WAVE)] _Gerstner_Wave ("GerstnerWave?", Float) = 0
        
        _GerstnerAParam("GertnerA参数 X-x方向 Y-z方向 Z-振幅 W-波长", vector) = (1,1,1,1)
        _GerstnerBParam("GertnerB参数 X-x方向 Y-z方向 Z-振幅 W-波长", vector) = (1,1,1,1)
        _GerstnerCParam("GertnerC参数 X-x方向 Y-z方向 Z-振幅 W-波长", vector) = (1,1,1,1)
        _GerstnerDParam("GertnerD参数 X-x方向 Y-z方向 Z-振幅 W-波长", vector) = (1,1,1,1)
        _GerstnerEParam("GertnerE参数 X-x方向 Y-z方向 Z-振幅 W-波长", vector) = (1,1,1,1)
        _GerstnerFParam("GertnerF参数 X-x方向 Y-z方向 Z-振幅 W-波长", vector) = (1,1,1,1)
        
        _MAXHeight("Max Height", float) = 2
        
        [DepthShallow]
        _WaterFXMap("WaterFX", 2D) = "white" {}
        
        
        [Tessellation]
    	_MinDist("Tess Min Distance", float) = 10
		_MaxDist("Tess Max Distance", float) = 25
		_Tessellation("Tessellation", Range(1,63)) = 1
        
        [Enum(No,2,Yes,0)] _TwoSided ("Two Sided", Int) = 2 // enum matches cull mode
    }
    SubShader
    {
         Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            //"DisableBatching"="LODFading"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
        }

        Cull [_TwoSided]
        ZWrite Off
        
        BLEND SrcAlpha OneMinusSrcAlpha
        
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            
            #pragma target 5.0

            #include "Assets/Effect/Ocean/Env_Ocean.hlsl"
            
            #pragma vertex tessVert
            #pragma hull OceanHull
            #pragma domain OceanDomain
            //#pragma vertex OceanVert
            #pragma fragment OceanFrag

            #pragma shader_feature _NONE_WAVE _SIN_WAVE _GERSTNER_WAVE
            

            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            #pragma multi_compile_fragment _REFLECTION_PLANARREFLECTION _SSRREFLECT
            
            #pragma multi_compile_fog
            
            //#include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            //#include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"
      
            
            ENDHLSL
            
        }
        
    }
}
