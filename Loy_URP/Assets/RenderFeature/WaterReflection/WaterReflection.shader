Shader "Loy/Feature/WaterReflection"
{
    Properties
    {
        _ReflectionDistance("Reflection Distance", Float) = 50.0
        _ReflectionEdgeFade("Reflection Edge Fade", Range(0.0, 0.25)) = 0.05
    }

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

            // 水面世界高度（反射平面）+ 反射距离 + 边缘淡出宽度。由 WaterReflectionRenderFeature 传入。
            float _WaterPlaneY;
            float _ReflectionDistance;
            float _ReflectionEdgeFade;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.positionCS.xy / _ScaledScreenParams.xy;

                float rawDepth = SampleSceneDepth(uv);
                float3 scenePosWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 camPosWS = GetCameraPositionWS();
                float3 viewDirWS = normalize(scenePosWS - camPosWS);

                // 修复：max(viewDirWS.y,1e-4) 会把负的视线 Y（朝下的合法视线）钳成正数，
                // 导致 t 恒负 → 永远走 t<=0 分支 → 反射全黑。改为保持符号的除零保护。
                float vy = viewDirWS.y;
                float t = (abs(vy) < 1e-4) ? 0.0 : (_WaterPlaneY - camPosWS.y) / vy;
                if (t <= 0.0)
                    return half4(0.0, 0.0, 0.0, 0.0);   // 视线不碰水面 → alpha=0 回退探针

                float3 waterPosWS = camPosWS + viewDirWS * t;
                float3 reflectDirWS = reflect(viewDirWS, float3(0.0, 1.0, 0.0));
                float3 reflPt = waterPosWS + reflectDirWS * _ReflectionDistance;
                float4 reflClip = mul(UNITY_MATRIX_VP, float4(reflPt, 1.0));
                if (reflClip.w <= 0.0)
                    return half4(0.0, 0.0, 0.0, 0.0);

                float2 reflNDC = reflClip.xy / reflClip.w;
                float2 reflUV = reflNDC * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                reflUV.y = 1.0 - reflUV.y;
                #endif

                // 反射 UV 边缘淡出：reflUV 越贴屏幕边缘，alpha 越低。
                // 反射点出屏幕时不再硬切 alpha=0，而是平滑过渡到探针兜底，消除水面上硬边。
                float2 clampedUV = saturate(reflUV);
                float2 edgeDist = min(clampedUV, 1.0 - clampedUV);                 // 0=贴边, 0.5=中心
                float fade = saturate(edgeDist.x / max(_ReflectionEdgeFade, 1e-4))
                           * saturate(edgeDist.y / max(_ReflectionEdgeFade, 1e-4));
                if (fade <= 0.001)
                    return half4(0.0, 0.0, 0.0, 0.0);

                half3 color = SAMPLE_TEXTURE2D_X(_CameraOpaqueTexture, sampler_CameraOpaqueTexture, clampedUV).rgb;
                return half4(color, fade);
            }
            ENDHLSL
        }
    }
}
