Shader "Loy/Feature/PostProcess/Streak"
{
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    struct Attributes
    {
        uint vertexID : SV_VertexID;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
        UNITY_VERTEX_OUTPUT_STEREO
    };

    Varyings Vert(Attributes input)
    {
        Varyings output;
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
        output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
        output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
        return output;
    }

    TEXTURE2D_X(_SourceTexture);
    TEXTURE2D_X(_InputTexture);
    TEXTURE2D_X(_HighTexture);

    float4 _SourceTexture_TexelSize;
    float4 _InputTexture_TexelSize;
    float _Threshold;
    float _Stretch;
    float _Intensity;
    half4 _Tint;

    half4 FragPrefilter(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

        float2 offset = float2(0.0, _SourceTexture_TexelSize.y * 0.5);
        half3 c0 = SAMPLE_TEXTURE2D_X(_SourceTexture, sampler_LinearClamp, input.uv - offset).rgb;
        half3 c1 = SAMPLE_TEXTURE2D_X(_SourceTexture, sampler_LinearClamp, input.uv + offset).rgb;
        half3 color = (c0 + c1) * 0.5h;

        half brightness = Max3(color.r, color.g, color.b);
        color *= max(0.0h, brightness - _Threshold) / max(brightness, 1e-5h);
        return half4(color, 1.0h);
    }

    half4 FragDownsample(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

        float2 uv = input.uv;
        float dx = _InputTexture_TexelSize.x;
        half3 c0 = SAMPLE_TEXTURE2D_X(_InputTexture, sampler_LinearClamp, uv + float2(-5.0 * dx, 0.0)).rgb;
        half3 c1 = SAMPLE_TEXTURE2D_X(_InputTexture, sampler_LinearClamp, uv + float2(-3.0 * dx, 0.0)).rgb;
        half3 c2 = SAMPLE_TEXTURE2D_X(_InputTexture, sampler_LinearClamp, uv + float2(-1.0 * dx, 0.0)).rgb;
        half3 c3 = SAMPLE_TEXTURE2D_X(_InputTexture, sampler_LinearClamp, uv + float2( 1.0 * dx, 0.0)).rgb;
        half3 c4 = SAMPLE_TEXTURE2D_X(_InputTexture, sampler_LinearClamp, uv + float2( 3.0 * dx, 0.0)).rgb;
        half3 c5 = SAMPLE_TEXTURE2D_X(_InputTexture, sampler_LinearClamp, uv + float2( 5.0 * dx, 0.0)).rgb;
        return half4((c0 + c1 * 2.0h + c2 * 3.0h + c3 * 3.0h + c4 * 2.0h + c5) / 12.0h, 1.0h);
    }

    half4 FragUpsample(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

        float2 uv = input.uv;
        float dx = _InputTexture_TexelSize.x * 1.5;
        half3 c0 = SAMPLE_TEXTURE2D_X(_InputTexture, sampler_LinearClamp, uv + float2(-dx, 0.0)).rgb;
        half3 c1 = SAMPLE_TEXTURE2D_X(_InputTexture, sampler_LinearClamp, uv).rgb;
        half3 c2 = SAMPLE_TEXTURE2D_X(_InputTexture, sampler_LinearClamp, uv + float2(dx, 0.0)).rgb;
        half3 high = SAMPLE_TEXTURE2D_X(_HighTexture, sampler_LinearClamp, uv).rgb;
        half3 low = c0 * 0.25h + c1 * 0.5h + c2 * 0.25h;
        return half4(lerp(high, low, _Stretch), 1.0h);
    }

    half4 FragComposition(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

        float2 uv = input.uv;
        float dx = _InputTexture_TexelSize.x * 1.5;
        half3 c0 = SAMPLE_TEXTURE2D_X(_InputTexture, sampler_LinearClamp, uv + float2(-dx, 0.0)).rgb;
        half3 c1 = SAMPLE_TEXTURE2D_X(_InputTexture, sampler_LinearClamp, uv).rgb;
        half3 c2 = SAMPLE_TEXTURE2D_X(_InputTexture, sampler_LinearClamp, uv + float2(dx, 0.0)).rgb;
        half4 source = SAMPLE_TEXTURE2D_X(_SourceTexture, sampler_LinearClamp, uv);
        half3 streak = (c0 * 0.25h + c1 * 0.5h + c2 * 0.25h) * _Tint.rgb * _Intensity * 5.0h;
        return half4(source.rgb + streak, source.a);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "Streak Prefilter"
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment FragPrefilter
            ENDHLSL
        }

        Pass
        {
            Name "Streak Downsample"
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment FragDownsample
            ENDHLSL
        }

        Pass
        {
            Name "Streak Upsample"
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment FragUpsample
            ENDHLSL
        }

        Pass
        {
            Name "Streak Composition"
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment FragComposition
            ENDHLSL
        }
    }
    Fallback Off
}
