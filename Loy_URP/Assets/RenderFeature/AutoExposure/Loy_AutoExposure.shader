Shader "Hidden/Loy/AutoExposure"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        // Pass 0: 把场景颜色降采样成平均 log2(luminance) 的小 RT
        // 每输出 texel 采样它对应的源区域内的网格，求平均 log-luma
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragLumaReduce

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_X(_SourceTex);
            SAMPLER(sampler_SourceTex);
            float4 _SourceTex_TexelSize;
            float _LumaRes;     // 输出 RT 边长（lumaRes x lumaRes）
            float _LumaSamples; // 每个输出 texel 内的网格采样数（_LumaSamples x _LumaSamples）

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            half Luma(half3 c) { return dot(c, half3(0.2126h, 0.7152h, 0.0722h)); }

            float FragLumaReduce(Varyings input) : SV_Target
            {
                float2 blockUV = rcp(_LumaRes);
                float2 blockMin = input.uv - blockUV * 0.5;
                float sum = 0.0;
                float n = 0.0;
                float samples = _LumaSamples;
                UNITY_LOOP
                for (float gy = 0.0; gy < samples; gy += 1.0)
                {
                    UNITY_LOOP
                    for (float gx = 0.0; gx < samples; gx += 1.0)
                    {
                        float2 uv = blockMin + float2((gx + 0.5) / samples, (gy + 0.5) / samples) * blockUV;
                        half3 c = SAMPLE_TEXTURE2D_X(_SourceTex, sampler_SourceTex, uv).rgb;
                        sum += log2(max(Luma(c), 1e-4));
                        n += 1.0;
                    }
                }
                return sum / max(n, 1.0);
            }
            ENDHLSL
        }

        // Pass 1: 应用曝光 —— 颜色 × 2^EV（tonemapping 前，绕过后处理 volume/LUT）
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragApply

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_X(_SourceTex);
            SAMPLER(sampler_SourceTex);
            float4 _SourceTex_TexelSize;
            float _ExposureEV;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            half4 FragApply(Varyings input) : SV_Target
            {
                half3 c = SAMPLE_TEXTURE2D_X(_SourceTex, sampler_SourceTex, input.uv).rgb;
                return half4(c * exp2(_ExposureEV), 1);
            }
            ENDHLSL
        }
    }
}
