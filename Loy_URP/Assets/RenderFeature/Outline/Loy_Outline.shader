Shader "Loy/Feature/PostProcess/Outline"
{
    Properties
    {
        [HideInInspector] _MainTex("Source", 2D) = "white" {}
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

    TEXTURE2D_X(_MainTex);
    SAMPLER(sampler_MainTex);
    float4 _MainTex_TexelSize;

    half4 _OutlineColor;
    float4 _OutlineParams;
    float4 _EdgeParams;
    float4 _FadeParams;

    #define OUTLINE_THICKNESS _OutlineParams.x
    #define OUTLINE_OPACITY _OutlineParams.y
    #define EDGE_THRESHOLD _OutlineParams.z
    #define EDGE_SOFTNESS _OutlineParams.w
    #define DEPTH_SENSITIVITY _EdgeParams.x
    #define NORMAL_SENSITIVITY _EdgeParams.y
    #define COLOR_SENSITIVITY _EdgeParams.z

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

    float EyeDepth(float2 uv)
    {
        return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
    }

    half Luma(half3 color)
    {
        return dot(color, half3(0.2126h, 0.7152h, 0.0722h));
    }

    half4 FragOutline(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        float2 uv = input.uv;
        float2 pixel = _MainTex_TexelSize.xy * OUTLINE_THICKNESS;

        float depthC = EyeDepth(uv);
        half3 normalC = SampleSceneNormals(uv);
        half lumaC = Luma(SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, uv).rgb);

        // Eight directions produce a smooth outline and avoid the directional bias of a 4-tap cross.
        const float2 directions[8] =
        {
            float2(-1, 0), float2(1, 0), float2(0, -1), float2(0, 1),
            float2(-0.7071, -0.7071), float2(0.7071, -0.7071),
            float2(-0.7071, 0.7071), float2(0.7071, 0.7071)
        };

        float depthEdge = 0.0;
        half normalEdge = 0.0h;
        half colorEdge = 0.0h;

        UNITY_UNROLL
        for (int i = 0; i < 8; i++)
        {
            float2 sampleUV = saturate(uv + directions[i] * pixel);
            float sampleDepth = EyeDepth(sampleUV);
            half3 sampleNormal = SampleSceneNormals(sampleUV);
            half sampleLuma = Luma(SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, sampleUV).rgb);

            // Relative depth keeps the threshold visually consistent as objects move away.
            depthEdge = max(depthEdge, abs(sampleDepth - depthC) / max(depthC, 0.25));
            normalEdge = max(normalEdge, 1.0h - saturate(dot(normalC, sampleNormal)));
            colorEdge = max(colorEdge, abs(sampleLuma - lumaC));
        }

        float edgeSignal = depthEdge * DEPTH_SENSITIVITY +
                           normalEdge * NORMAL_SENSITIVITY +
                           colorEdge * COLOR_SENSITIVITY;
        float edge = smoothstep(EDGE_THRESHOLD, EDGE_THRESHOLD + EDGE_SOFTNESS, edgeSignal);

        // Fade only distant outlines; nearby silhouettes remain crisp.
        float distanceFade = 1.0 - smoothstep(_FadeParams.x, _FadeParams.y, depthC);
        edge *= distanceFade * OUTLINE_OPACITY;

        half4 source = SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, uv);
        source.rgb = lerp(source.rgb, _OutlineColor.rgb, saturate(edge * _OutlineColor.a));
        return source;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "Post Process Outline"
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment FragOutline
            ENDHLSL
        }
    }
    Fallback Off
}
