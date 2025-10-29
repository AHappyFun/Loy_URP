Shader "Loy/DeferredLit"
{
    Properties
    {
        [HideInInspector]_Transparent("__transparent", Float) = 0.0
    	[HideInInspector]_Mode ("__mode", Float) = 0.0
    	[HideInInspector]_CastShadow("__castShadow", Float) = 1.0
        [HideInInspector]_SrcBlend ("__src", Float) = 1.0
        [HideInInspector]_DstBlend ("__dst", Float) = 0.0
	    [HideInInspector]_SrcBlendAlpha("__srcA", Float) = 1.0
        [HideInInspector]_DstBlendAlpha("__dstA", Float) = 0.0
        [HideInInspector]_ZWrite ("__zw", Float) = 1.0

        _MaterialSettingLable("MaterialSettings", int) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull Mode", Float) = 2
        [Space(20)]
    	_Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
    	[Toggle(_ALPHATEST_ON)] _Clipping("AlphaTest", float) = 0
		[Toggle(_ALPHAPREMULTIPLY_ON)] _PremulAlpha("Pre Mul Alpha", float) = 0

        _MainTexLable("主贴图", int) = 0
    	[MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color", Color) = (1,1,1,1)

    	_MaskLable("PBR Mask", int) = 0
        [NoScaleOffset] _MetallicGlossMap("R:金属 G:AO A:Smothness", 2D) = "white" {}
    	_Metallic("Metallic", Range(0.0, 1.0)) = 0
    	_Occlusion("Occlusion", Range(0.0, 1.0)) = 1.0
    	_Smoothness("Smoothness", Range(0.0, 1.0)) = 0

    	_NormalMapLable("NormalMap", int) = 0
    	[Toggle(_NORMAL_MAP)]_NormalMapToggle("是否使用法线贴图?", float) = 1
	    [NoScaleOffset][Normal]_BumpMap("Normal Map", 2D) = "bump" {}

        _EmissionLable("自发光", int) = 0
        [NoScaleOffset] _EmissionMap("Emission Map", 2D) = "white" {}
        [HDR] _EmissionColor("Emission Color", Color) = (0,0,0)


    }
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "DeferredLitInput.hlsl"
        ENDHLSL

        Pass
        {
            Name "GBuffer"
            Tags
            {
                "LightMode" = "UniversalGBuffer"
            }

            //Blend 不需要控制
            ZWrite[_ZWrite]
            ZTest LEqual
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 4.5

            // Deferred Rendering Path does not support the OpenGL-based graphics API:
            // dont support Desktop OpenGL, OpenGL ES 3.0, WebGL 2.0.
            #pragma exclude_renderers gles3 glcore

            #pragma multi_compile _ _ALPHATEST_ON
            #pragma multi_compile _ _NORMAL_MAP

            // GPU Instancing
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #pragma vertex LitGBufferPassVert
            #pragma fragment LitGBufferPassFrag

            #include "DeferredLitGBufferPass.hlsl"

            ENDHLSL

        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "GAEAStandardShaderGUI"
}
