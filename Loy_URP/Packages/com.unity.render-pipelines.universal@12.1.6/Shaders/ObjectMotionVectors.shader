Shader "Hidden/kMotion/ObjectMotionVectors"
{
    SubShader
    {
        Pass
        {
            // Lightmode tag required setup motion vector parameters by C++ (legacy Unity)
            Tags{ "LightMode" = "MotionVectors" }

            HLSLPROGRAM
            // Required to compile gles 2.0 with standard srp library
            #pragma prefer_hlslcc gles
            #pragma exclude_renderers d3d11_9x
            #pragma target 3.0

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing

            #pragma vertex vert
            #pragma fragment frag

            // -------------------------------------
            // Includes
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"


            // -------------------------------------
            // Structs
            struct Attributes
            {
                float4 position             : POSITION;
                float3 positionOld          : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS           : SV_POSITION;
                float4 positionVP           : TEXCOORD0;
                float4 previousPositionVP   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            half2 EncodeVelocityToTexture(half2 V)
            {
                //编码范围是-2~2
                //0.499f是中间值，表示速度为0，
                //0是Clear值，表示当前没有速度写入，注意区分和速度为0的区别
                half2 EncodeV =  V.xy * (0.499f * 0.5f) + 32767.0f / 65535.0f;
                return EncodeV;
            }


            // -------------------------------------
            // Vertex
            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.position.xyz);

                // this works around an issue with dynamic batching
                // potentially remove in 5.4 when we use instancing
                #if defined(UNITY_REVERSED_Z)
                    output.positionCS.z -= unity_MotionVectorsParams.z * output.positionCS.w;
                #else
                    output.positionCS.z += unity_MotionVectorsParams.z * output.positionCS.w;
                #endif

                output.positionVP = mul(UNITY_MATRIX_UNJITTERED_VP, mul(UNITY_MATRIX_M, input.position));

                const float4 prevPos = (unity_MotionVectorsParams.x == 1) ? float4(input.positionOld, 1) : input.position;
                output.previousPositionVP = mul(UNITY_MATRIX_PREV_VP, mul(unity_MatrixPreviousM, prevPos));

                return output;
            }

            // -------------------------------------
            // Fragment
            half2 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                
                // Calculate positions
                float4 posVP = input.positionVP;
                float4 prevPosVP = input.previousPositionVP;
                posVP.xy /= posVP.w;
                prevPosVP.xy /= prevPosVP.w;

                // Calculate velocity
                float2 velocity = (posVP.xy - prevPosVP.xy);
                #if UNITY_UV_STARTS_AT_TOP
                    velocity.y = -velocity.y;
                #endif

                // Convert from NDC space (-1..1) to Screen 0..1 space.
                //(-0.5, 0.5)
                // Note it doesn't mean we don't have negative value, we store negative or positive offset in NDC space.
                // Note: ((positionCS * 0.5 + 0.5) - (previousPositionCS * 0.5 + 0.5)) = (velocity * 0.5)
                velocity *= 0.5f;
                // Note: unity_MotionVectorsParams.y is 0 is forceNoMotion is enabled
                bool forceNoMotion = unity_MotionVectorsParams.y == 0.0;
                if (forceNoMotion)
                {
                    velocity = half2(2, 2);
                }

                return EncodeVelocityToTexture(velocity);
            }
            ENDHLSL
        }
    }
}
