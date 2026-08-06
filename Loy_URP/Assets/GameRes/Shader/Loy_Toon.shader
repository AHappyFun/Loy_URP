Shader "Loy/Toon"
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

        _ToonLable("卡通", int) = 0
        _ToonDiffuseStep("漫反射色阶阈值", Range(0.0, 1.0)) = 0.5
        _ToonSpecIntensity("卡通高光强度", Range(0.0, 1.0)) = 1.0
        _ToonSpecularSize("卡通高光大小", Range(0.0, 1.0)) = 0.6
        _ToonGIStrength("间接光强度", Range(0.0, 2.0)) = 0.5
        _Occlusion("Occlusion", Range(0.0, 1.0)) = 1.0

    	_NormalMapLable("NormalMap", int) = 0
    	[Toggle(_NORMAL_MAP)]_NormalMapToggle("是否使用法线贴图?", float) = 1
	    [NoScaleOffset][Normal]_BumpMap("Normal Map", 2D) = "bump" {}

        _EmissionLable("自发光", int) = 0
        [NoScaleOffset] _EmissionMap("Emission Map", 2D) = "black" {}
        [HDR] _EmissionColor("Emission Color", Color) = (0,0,0)
        _EmissionScale("Emission Scale", Float) = 1.0


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
        #include "ToonInput.hlsl"
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

			#pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ _ALPHATEST_ON
            #pragma multi_compile _ _NORMAL_MAP

            // URP 17 Deferred 所需的变体（法线 Oct 编码 / RenderPass 深度槽 / 主光阴影）
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_fragment _ _RENDER_PASS_ENABLED
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            // GPU Instancing
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #pragma vertex ToonGBufferPassVert
            #pragma fragment ToonGBufferPassFrag

            #include "ToonGBufferPass.hlsl"

            ENDHLSL

        }

        Pass
        {
            Name "DepthOnly"
            Tags
            {
                "LightMode" = "DepthOnly"
            }

            // 深度预通道/深度纹理（_CameraDepthTexture）依赖此通道
            ZWrite On
            ColorMask R
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 4.5

            #pragma multi_compile _ _ALPHATEST_ON
            #pragma multi_compile_instancing

            #pragma vertex StandardDepthOnlyVertex
            #pragma fragment StandardDepthOnlyFragment

            #include "StandardShadowPass.hlsl"

            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags
            {
                "LightMode" = "DepthNormals"
            }

            // Deferred 下 SSAO/深度-法线预通道依赖此通道（写入 GBuffer2 法线槽）
            ZWrite On
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 4.5

            #pragma multi_compile _ _ALPHATEST_ON
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_instancing

            #pragma vertex StandardDepthNormalsVertex
            #pragma fragment StandardDepthNormalsFragment

            #include "StandardShadowPass.hlsl"

            ENDHLSL
        }

		Pass
		{
			Name "ShadowCaster"
            Tags
            {
                "LightMode" = "ShadowCaster"
            }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 3.0

            //需要支持AlphaTest的Shadow
		 	#pragma multi_compile _ _ALPHATEST_ON
            #pragma multi_compile_instancing

            #pragma vertex StandardShadowPassVertex
            #pragma fragment StandardShadowPassFragment

            #include "StandardShadowPass.hlsl"

            ENDHLSL
		}
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "GAEAStandardShaderGUI"
}
