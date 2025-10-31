Shader "Loy/Feature/CustomLensFlare"
{
    Properties
    {
        //_Intensity("Intensity", Float) = .1
        //_Falloff("Falloff", Float) = 8
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }

       Pass
        {
            Name "CustomLensFlare"
            ZTest Always Cull Off ZWrite Off
            Blend One One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            
            int _FlareLayerCount;
            float4 _FlareParams[4];
            float4 _FlareSunPos;
            float _ScreenAspect;
            
            TEXTURE2D(_FlareTex0);
            TEXTURE2D(_FlareTex1);
            TEXTURE2D(_FlareTex2);
            TEXTURE2D(_FlareTex3);
            SAMPLER(sampler_FlareTex0);
            
            struct Attributes
            {
                uint vertexID   : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                
                OUT.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);

                return OUT;
            }

            //float3 hsv2rgb(float3 c)
            //{
            //    float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
            //    float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
            //    return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            //}
            
            float4 flare3(float2 uv, float2 center, float scale, Texture2D flareTex, float intensity)
            {
                float2 localUV = (uv - center) * float2(1.0f, _ScreenAspect) / scale + 0.5;
                if (any(localUV < 0.0) || any(localUV > 1.0)) return 0;

                float4 texColor = SAMPLE_TEXTURE2D(flareTex, sampler_FlareTex0, localUV);
                texColor.rgb *= intensity;
                return texColor * texColor.a;
            }

            float2 getOffset(float2 flarePos, float2 screenCenter, float offsetScale)
            {
                float2 dir = normalize(screenCenter - flarePos);
                return dir * offsetScale;
            }

            float LinearEyeDepth(float rawDepth)
            {
                return Linear01Depth(rawDepth, _ZBufferParams) * _ProjectionParams.z;
            }
            
            half4 frag(Varyings IN) : SV_Target
            {

                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                
                float2 uv = IN.positionCS.xy / _ScaledScreenParams.xy;
                half4 col = 0;

                float2 sunUV = _FlareSunPos.xy;
                float Z = SampleSceneDepth(sunUV);
            #if !UNITY_REVERSED_Z
                Z = lerp(UNITY_NEAR_CLIP_VALUE, 1, Z);
            #endif

                //太阳前面有东西挡住，不显示LensFlare
                float linearDepth = LinearEyeDepth(Z);
                if(linearDepth < 999.0f)
                    return 0;
                
                if (_FlareLayerCount > 0) col += flare3(uv, _FlareParams[0].xy, _FlareParams[0].z, _FlareTex0, _FlareParams[0].w);
                if (_FlareLayerCount > 1) col += flare3(uv, _FlareParams[1].xy, _FlareParams[1].z, _FlareTex1, _FlareParams[1].w);
                if (_FlareLayerCount > 2) col += flare3(uv, _FlareParams[2].xy, _FlareParams[2].z, _FlareTex2, _FlareParams[2].w);
                if (_FlareLayerCount > 3) col += flare3(uv, _FlareParams[3].xy, _FlareParams[3].z, _FlareTex3, _FlareParams[3].w);
                return col;
            }

            ENDHLSL
        }
    }
}
