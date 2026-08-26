Shader "Loy/Feature/WaterReflection"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "WaterSSPR"
            ZTest Always Cull Off ZWrite Off

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            #pragma vertex vert
            #pragma fragment frag

            // 水面所在的世界 Y（水平面）。由 WaterReflectionRenderFeature 每帧设置。
            float _WaterPlaneY;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                return o;
            }

            // 屏幕空间平面反射：把当前像素重建出的世界坐标，跨过水平面镜像，
            // 再投影回屏幕采样场景色。alpha=1 表示反射有效，0 表示反射点在屏幕外/相机后。
            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.positionCS.xy / _ScaledScreenParams.xy;

                float rawDepth = SampleSceneDepth(uv);
                float3 scenePosWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);

                // 水平面镜像：y' = 2*planeY - y
                float3 reflPosWS = scenePosWS;
                reflPosWS.y = 2.0 * _WaterPlaneY - scenePosWS.y;

                float4 reflClip = mul(UNITY_MATRIX_VP, float4(reflPosWS, 1.0));
                float2 reflNDC = reflClip.xy / reflClip.w;
                float2 reflUV = reflNDC * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                reflUV.y = 1.0 - reflUV.y;
                #endif

                // 屏幕外 / 相机后方 → 无效
                if (reflClip.w <= 0.0 || any(reflUV < 0.0) || any(reflUV > 1.0))
                    return half4(0.0, 0.0, 0.0, 0.0);

                half3 color = SAMPLE_TEXTURE2D_X(_CameraOpaqueTexture, sampler_CameraOpaqueTexture, reflUV).rgb;
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
