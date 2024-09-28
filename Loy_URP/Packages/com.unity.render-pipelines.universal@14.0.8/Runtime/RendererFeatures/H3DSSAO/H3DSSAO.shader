Shader "Aley/Feature/SSAO"
{   
	
	Properties
    {
        [HideInInspector] _MainTex("Base (RGB)", 2D) = "white" {}
    }
	HLSLINCLUDE
		#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
		#include "Packages/com.unity.render-pipelines.universal/Shaders/PostProcessing/Common.hlsl"
		#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/AmbientOcclusion.hlsl"
		#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
		#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

		#pragma multi_compile _USE_DRAW_PROCEDURAL

		//两个Declare库包含了
		//TEXTURE2D(_CameraDepthTexture);
		//sampler sampler_CameraDepthTexture;
		//TEXTURE2D(_CameraNormalsTexture);
		//sampler sampler_CameraNormalsTexture;

		struct ProceduralAttributes
		{
		    uint vertexID : VERTEXID_SEMANTIC;
		};
		
		struct ProceduralVaryings
		{
		    float4 positionCS : SV_POSITION;
		    float2 uv : TEXCOORD;
		};
		
		ProceduralVaryings ProceduralVert (ProceduralAttributes input)
		{
		    ProceduralVaryings output;
		    output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
		    output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
		    return output;
		}
	
		TEXTURE2D(_MainTex);
		sampler sampler_MainTex;
		float4 _MainTex_TexelSize;
	
		#define RADIUS _AmbientOcclusionParams.x
		#define SAMPLE_COUNT _AmbientOcclusionParams.y
		#define INTENSITY _AmbientOcclusionParams.z

		float ScreenDepthToClipDepth(float z)
		{
			#if !UNITY_REVERSED_Z
				z = z * 2 - 1;
			#endif
			return z;
		}
	
		static half SSAORandomUV[40] =
		{
		    0.00000000,  // 00
		    0.33984375,  // 01
		    0.75390625,  // 02
		    0.56640625,  // 03
		    0.98437500,  // 04
		    0.07421875,  // 05
		    0.23828125,  // 06
		    0.64062500,  // 07
		    0.35937500,  // 08
		    0.50781250,  // 09
		    0.38281250,  // 10
		    0.98437500,  // 11
		    0.17578125,  // 12
		    0.53906250,  // 13
		    0.28515625,  // 14
		    0.23137260,  // 15
		    0.45882360,  // 16
		    0.54117650,  // 17
		    0.12941180,  // 18
		    0.64313730,  // 19
		
		    0.92968750,  // 20
		    0.76171875,  // 21
		    0.13333330,  // 22
		    0.01562500,  // 23
		    0.00000000,  // 24
		    0.10546875,  // 25
		    0.64062500,  // 26
		    0.74609375,  // 27
		    0.67968750,  // 28
		    0.35156250,  // 29
		    0.49218750,  // 30
		    0.12500000,  // 31
		    0.26562500,  // 32
		    0.62500000,  // 33
		    0.44531250,  // 34
		    0.17647060,  // 35
		    0.44705890,  // 36
		    0.93333340,  // 37
		    0.87058830,  // 38
		    0.56862750,  // 39
		};

		// Trigonometric function utility
		float2 CosSin(float theta)
		{
		    float sn, cs;
		    sincos(theta, sn, cs);
		    return float2(cs, sn);
		}

		// Pseudo random number generator with 2D coordinates
		float UVRandom(float u, float v)
		{
		    float f = dot(float2(12.9898, 78.233), float2(u, v));
		    return frac(43758.5453 * sin(f));
		}

		//float3 PickSamplePoint(float2 coord, float index)
		//{
		//	float u = InterleavedGradientNoise(coord, index * 2 ) * 2 - 1;
		//	float theta = InterleavedGradientNoise(coord, index * 2 + 1 ) * TWO_PI;
//
		//    float3 v = float3(CosSin(theta) * sqrt(1.0 - u * u), u);
		//    // Make them distributed between [0, _Radius]
		//    float l = sqrt((index + 1.0) / SAMPLE_COUNT) * RADIUS;
		//    return v * l;
		//}

		half GetRandomUVForSSAO(float u, int sampleIndex)
		{
		    return SSAORandomUV[u * 20 + sampleIndex];
		}

		// Sample point picker
		half3 PickSamplePoint(float2 uv, int sampleIndex)
		{
		    const float2 positionSS = uv;
		    const half gn = half(InterleavedGradientNoise(positionSS, sampleIndex));
		
		    const half u = frac(GetRandomUVForSSAO(half(0.0), sampleIndex) + gn) * half(2.0) - half(1.0);
		    const half theta = (GetRandomUVForSSAO(half(1.0), sampleIndex) + gn) * half(TWO_PI);
		
		    return half3(CosSin(theta) * sqrt(half(1.0) - u * u), u);
		}

		half4 PackAONormal(half ao, half3 n)
		{
		    return half4(ao, n * half(0.5) + half(0.5));
		}
		
		float4 frag(ProceduralVaryings input) : SV_Target
        {
        	float3 WorldNormal = SampleSceneNormals(input.uv);
        	//float3 WorldNormal = SAMPLE_TEXTURE2D(_CameraNormalsTexture, sampler_CameraNormalsTexture, input.uv).rgb * 2 - 1;
        	float3 ViewNormal = mul((float3x3)UNITY_MATRIX_V, WorldNormal);
        	//float depth = SAMPLE_TEXTURE2D(_CameraDepthTexture, sampler_CameraDepthTexture, input.uv).x;

        	float depth = SampleSceneDepth(input.uv);
    		depth = LinearEyeDepth(depth, _ZBufferParams);
        	
        	#if UNITY_REVERSED_Z
        		float3 NDC = float3(input.uv.x * 2 - 1, 1 - input.uv.y * 2, depth);
        	#else
        		float3 NDC = float3(input.uv.xy * 2 - 1, depth * 2 - 1);
        	#endif

        	
        	float4 T = mul(UNITY_MATRIX_I_P, float4(NDC, 1));
        	T /= T.w;
        	float EyeDepth =  abs(T.z);
        	float3 ViewPos = T.xyz;

        	float ao = 0;
        	UNITY_LOOP
			for(int s = 0; s < SAMPLE_COUNT; s++)
			{
				float3 v_s1 = PickSamplePoint(input.positionCS.xy, s);

				// Make it distributed between [0, _Radius]
				v_s1 *= sqrt((half(s) + half(1.0)) * (half)rcp(SAMPLE_COUNT)) * RADIUS;

				v_s1 = faceforward(v_s1, -ViewNormal, v_s1);

				float3 vpos_s1 = ViewPos.xyz + v_s1;
				
		        float4 spos_s1 = mul(UNITY_MATRIX_P, float4(vpos_s1, 1));
				spos_s1 /= spos_s1.w;
				
				float2 uv_s1_01 = (spos_s1.xy + 1) * 0.5f;
				#if UNITY_REVERSED_Z
					uv_s1_01.y = 1 - uv_s1_01.y;
				#endif
				 
		        // Depth at the sample point

				float depth_s1 = SAMPLE_TEXTURE2D(_CameraDepthTexture, sampler_CameraDepthTexture, uv_s1_01).x;
				#if !UNITY_REVERSED_Z
					depth_s1 = depth_s1 * 2 - 1;
				#endif
				float4 T2 = mul(UNITY_MATRIX_I_P, float4(spos_s1.xy, depth_s1, 1));
				T2 /= T2.w;
				float3 vpos_s2 = T2.xyz;

		        // Relative position of the sample point
		        float3 v_s2 = vpos_s2 - ViewPos;
				
				//
				half dotVal = dot(v_s2, ViewNormal) - half(half(0.002) * EyeDepth);
        		half a1 = max(dotVal, half(0.0));
				
        		half a2 = dot(v_s2, v_s2) + half(0.0001);
        		ao += a1 * rcp(a2);
			}

        	ao *= RADIUS;
        	
        	ao  = ao * (half)rcp(SAMPLE_COUNT) * INTENSITY;
        	ao = saturate(1 - ao);
        	return ao;
		}
		half4 fragAfterOpaque(ProceduralVaryings input) : SV_TARGET
		{
			half ssao = SampleAmbientOcclusion(input.uv);
			return half4(0, 0, 0, ssao);
		}
	
		half blur_h(Varyings input) : SV_Target
        {
            half texelSize = _MainTex_TexelSize.x;
            half c0 = SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, input.texcoord - half2(texelSize * 3.23076923, 0.0)).r;
            half c1 = SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, input.texcoord - half2(texelSize * 1.38461538, 0.0)).r;
            half c2 = SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, input.texcoord).rgb;  
            half c3 = SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, input.texcoord + half2(texelSize * 1.38461538, 0.0)).r;
            half c4 = SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, input.texcoord + half2(texelSize * 3.23076923, 0.0)).r;

            half color = c0 * 0.07027027 + c1 * 0.31621622
                    + c2 * 0.22702703
                    + c3 * 0.31621622 + c4 * 0.07027027;
            return color;
        }

		half blur_v(Varyings input) : SV_Target
        {
            half texelSize = _MainTex_TexelSize.y;
            half c0 = SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, input.texcoord - half2(0.0, texelSize * 3.23076923)).r;
            half c1 = SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, input.texcoord - half2(0.0, texelSize * 1.38461538)).r;
            half c2 = SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, input.texcoord).r;  
            half c3 = SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, input.texcoord + half2(0.0, texelSize * 1.38461538)).r;
            half c4 = SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, input.texcoord + half2(0.0, texelSize * 3.23076923)).r;

            half color = c0 * 0.07027027 + c1 * 0.31621622
                    + c2 * 0.22702703
                    + c3 * 0.31621622 + c4 * 0.07027027;
            return color;
        }
		
	ENDHLSL
	
	SubShader
	{
		
		
		Pass 
		{
			 Name "DrawSSAO"
			ZTest Always Cull Off ZWrite Off
			Blend One Zero

			HLSLPROGRAM
				#pragma vertex ProceduralVert
				#pragma fragment frag
			ENDHLSL
		}
		
		Pass 
		{
			 Name "Blur_H"
			ZTest Always 
			Cull Off
			ZWrite Off
			Blend One Zero

			HLSLPROGRAM
				#pragma vertex ProceduralVert
				#pragma fragment blur_h
			ENDHLSL
		}
		
		Pass 
		{
			 Name "Blur_V"
			ZTest Always
			Cull Off
			ZWrite Off
			Blend One Zero

			HLSLPROGRAM
				#pragma vertex ProceduralVert
				#pragma fragment blur_v
			ENDHLSL
		}
		
		Pass
		{
			 Name "AdditiveAfterOpaque"
			ZTest Always
            ZWrite Off
            Cull Off
            Blend One SrcAlpha, Zero One
            BlendOp Add, Add
			
			HLSLPROGRAM
				#pragma vertex ProceduralVert
				#pragma fragment fragAfterOpaque				
			ENDHLSL
			
		}
		
	}
}
