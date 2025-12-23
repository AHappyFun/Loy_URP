Shader "Loy/Feature/VolumeCloud"
{
    Properties
    {
        _NoiseTex ("3D Noise", 3D) = "" {}
        _CloudDensity ("Cloud Density", Float) = 1.0
        _StepSize ("Step Size", Float) = 1.0
        _PhaseG ("Phase G", Range(-0.9, 0.9)) = 0.6

        _BoxCenter ("Box Center", Vector) = (0,10,0,0)
        _BoxSize ("Box Size", Vector) = (100,50,100,0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }

        Pass
        {
            Name "VolumeCloud"
            ZTest Always Cull Off ZWrite Off
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #include "VolumeCloud.hlsl"
            #pragma vertex vert
            #pragma fragment frag

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                OUT.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);

                OUT.positionWS = ComputeWorldSpacePosition(
                    OUT.positionCS,
                    UNITY_MATRIX_I_VP
                );

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {

                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                //float2 uv = IN.positionCS.xy;// / GetAOTexSize().xy;

                return VolumeCloudRayMarch(IN);
            }

            ENDHLSL
        }
    }
}