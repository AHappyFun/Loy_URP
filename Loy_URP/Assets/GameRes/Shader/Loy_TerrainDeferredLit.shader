// Loy Terrain Deferred Lit
// ---------------------------------------------------------------------------
// 精简自包含的地形 shader（不依赖 Unity 的 TerrainLit*.hlsl include）。
//
// 核心：
//   * 4 层 splat 混合，由 _Control（layer 图）的 RGBA 四通道控制每层权重
//   * 每层 = Albedo(_Splat0-3) + Normal(_Normal0-3)
//   * 金属度 / 光滑度 = 每层标量滑动条（_Metallic0-3 / _Smoothness0-3）
//   * 标准 PBR（albedo / metallic / smoothness / normal / occlusion）
//
// 每层额外贴图插槽（独立贴图，不走 Unity 的 Mask Map 打包）：
//   _Roughness0-3   灰度，Roughness 贴图。Smoothness = 1 - Roughness（无独立滑动条）。
//                   默认全白（=1，即 Smoothness=0，全粗糙不反光），务必手动指定贴图。
//   _Displacement0-3 灰度，高度/位移贴图。用于层间高度混合（比线性羽化更自然的过渡），
//                   默认中灰（=0.5，四层相同则退化为普通权重混合）。
//   _AO0-3          灰度，环境光遮蔽贴图。默认白（=1），无贴图时不影响遮蔽。
// 注意：这三个是自定义插槽，Unity Terrain Layer 面板不会自动填充，需要手动指定
// （或者你自己写脚本/工具绑定 TerrainLayer）。
//
// 已移除（相比 Unity 原版 TerrainLit）：
//   * 逐像素法线 instancing（_TERRAIN_INSTANCED_PERPIXEL_NORMAL）
//   * Density 混合（albedo alpha 通道当密度）
//   * DBuffer 贴花、探针体积、屏幕空间辐照、Debug 显示、Mipmap streaming
//
// 保留 Unity Terrain 组件所需：TerrainInstancing（heightmap 重建位置/法线）、
// instancing 关键字、holes 挖洞（_ALPHATEST_ON）。
Shader "Loy/TerrainDeferredLit"
{
    Properties
    {
        // ---- 以下由 Unity Terrain / TerrainLayer 自动填充，[HideInInspector] ----
        _Control("Control (Layer Map)", 2D) = "red" {}
        _Splat0("Albedo 0", 2D) = "grey" {}
        _Splat1("Albedo 1", 2D) = "grey" {}
        _Splat2("Albedo 2", 2D) = "grey" {}
        _Splat3("Albedo 3", 2D) = "grey" {}
        _Normal0("Normal 0", 2D) = "bump" {}
        _Normal1("Normal 1", 2D) = "bump" {}
        _Normal2("Normal 2", 2D) = "bump" {}
        _Normal3("Normal 3", 2D) = "bump" {}
        _Metallic0("Metallic 0", Range(0, 1)) = 0
        _Metallic1("Metallic 1", Range(0, 1)) = 0
        _Metallic2("Metallic 2", Range(0, 1)) = 0
        _Metallic3("Metallic 3", Range(0, 1)) = 0
        // ---- 以下是自定义插槽，需要手动指定（Unity Terrain 不会自动填充） ----
        _Roughness0("Roughness 0", 2D) = "white" {}
        _Roughness1("Roughness 1", 2D) = "white" {}
        _Roughness2("Roughness 2", 2D) = "white" {}
        _Roughness3("Roughness 3", 2D) = "white" {}
        _Displacement0("Displacement 0", 2D) = "grey" {}
        _Displacement1("Displacement 1", 2D) = "grey" {}
        _Displacement2("Displacement 2", 2D) = "grey" {}
        _Displacement3("Displacement 3", 2D) = "grey" {}
        _AO0("AO 0", 2D) = "white" {}
        _AO1("AO 1", 2D) = "white" {}
        _AO2("AO 2", 2D) = "white" {}
        _AO3("AO 3", 2D) = "white" {}
        _HeightTransition("Height Transition", Range(0.01, 1)) = 0.5

        [HideInInspector] _TerrainHolesTexture("Holes", 2D) = "white" {}
    }

    HLSLINCLUDE
    #pragma multi_compile_fragment __ _ALPHATEST_ON

    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutput.hlsl"
    #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"

    // -------------------------------------------------------------------------
    // 材质属性（由 Terrain 系统按名字填充）
    // -------------------------------------------------------------------------
    CBUFFER_START(_Terrain)
        half _NormalScale0, _NormalScale1, _NormalScale2, _NormalScale3;
        half _Metallic0, _Metallic1, _Metallic2, _Metallic3;
        half _HeightTransition;
        // Smoothness 已改为纯贴图驱动（1 - Roughness），不再需要滑动条
        float4 _Control_ST;
        float4 _Control_TexelSize;
        half4 _Splat0_ST, _Splat1_ST, _Splat2_ST, _Splat3_ST;

        #ifdef UNITY_INSTANCING_ENABLED
        float4 _TerrainHeightmapRecipSize;  // 1/width, 1/height, 1/(width-1), 1/(height-1)
        float4 _TerrainHeightmapScale;      // hmScale.x, hmScale.y/kMaxHeight, hmScale.z, 0
        #endif
    CBUFFER_END

    // 贴图。采样统一用全局共享的 sampler_TrilinearRepeat：13 个独立 SAMPLER 会
    // 叠加 Forward pass 内部的阴影/cookie sampler，超过 ps_4_0 的 16 个 sampler
    // 寄存器上限，导致 Forward pass 编译失败。共享 sampler 不受 keyword 裁剪影响。
    TEXTURE2D(_Control);
    TEXTURE2D(_Splat0);
    TEXTURE2D(_Splat1);
    TEXTURE2D(_Splat2);
    TEXTURE2D(_Splat3);
    TEXTURE2D(_Normal0);
    TEXTURE2D(_Normal1);
    TEXTURE2D(_Normal2);
    TEXTURE2D(_Normal3);

    // Roughness / AO 复用各层 Albedo 的采样（_Splat0-3 始终被采样）
    TEXTURE2D(_Roughness0);
    TEXTURE2D(_Roughness1);
    TEXTURE2D(_Roughness2);
    TEXTURE2D(_Roughness3);
    TEXTURE2D(_AO0);
    TEXTURE2D(_AO1);
    TEXTURE2D(_AO2);
    TEXTURE2D(_AO3);

    TEXTURE2D(_Displacement0);
    TEXTURE2D(_Displacement1);
    TEXTURE2D(_Displacement2);
    TEXTURE2D(_Displacement3);

    #ifdef _ALPHATEST_ON
    TEXTURE2D(_TerrainHolesTexture); SAMPLER(sampler_TerrainHolesTexture);
    #endif

    #ifdef UNITY_INSTANCING_ENABLED
    TEXTURE2D(_TerrainHeightmapTexture);
    TEXTURE2D(_TerrainNormalmapTexture);
    #endif

    UNITY_INSTANCING_BUFFER_START(Terrain)
    UNITY_DEFINE_INSTANCED_PROP(float4, _TerrainPatchInstanceData)  // xy = base, z = skipScale
    UNITY_INSTANCING_BUFFER_END(Terrain)

    // 编辑器场景选择用（SceneSelectionPass）
    int _ObjectId;
    int _PassValue;

    // -------------------------------------------------------------------------
    // 挖洞（terrain holes）
    // -------------------------------------------------------------------------
    #ifdef _ALPHATEST_ON
    float SampleTerrainHolesTexture(float2 uv)
    {
        return SAMPLE_TEXTURE2D(_TerrainHolesTexture, sampler_TerrainHolesTexture, uv).r;
    }

    void ClipHoles(float2 uv)
    {
        float hole = SampleTerrainHolesTexture(uv);
        float epsilon = 0.0005f; // 避免压缩导致 0 不为 0（UUM-61913）
        clip(hole < epsilon ? -1 : 1);
    }
    #endif

    // -------------------------------------------------------------------------
    // Terrain instancing：从 heightmap 重建位置 / 法线 / uv
    // -------------------------------------------------------------------------
    void TerrainInstancing(inout float4 positionOS, inout float3 normal, inout float2 uv)
    {
    #ifdef UNITY_INSTANCING_ENABLED
        float2 patchVertex = positionOS.xy;
        float4 instanceData = UNITY_ACCESS_INSTANCED_PROP(Terrain, _TerrainPatchInstanceData);

        float2 sampleCoords = (patchVertex.xy + instanceData.xy) * instanceData.z;
        float height = UnpackHeightmap(_TerrainHeightmapTexture.Load(int3(sampleCoords, 0)));

        positionOS.xz = sampleCoords * _TerrainHeightmapScale.xz;
        positionOS.y = height * _TerrainHeightmapScale.y;
        normal = _TerrainNormalmapTexture.Load(int3(sampleCoords, 0)).rgb * 2 - 1;
        uv = sampleCoords * _TerrainHeightmapRecipSize.zw;
    #endif
    }

    void TerrainInstancing(inout float4 positionOS, inout float3 normal)
    {
        float2 uv = { 0, 0 };
        TerrainInstancing(positionOS, normal, uv);
    }

    // -------------------------------------------------------------------------
    // 顶点输入 / 输出
    // -------------------------------------------------------------------------
    struct Attributes
    {
        float4 positionOS : POSITION;
        float3 normalOS   : NORMAL;
        float2 texcoord   : TEXCOORD0;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct Varyings
    {
        float4 uvMainAndLM : TEXCOORD0;  // xy: control, zw: lightmap
        float4 uvSplat01   : TEXCOORD1;  // xy: splat0, zw: splat1
        float4 uvSplat23   : TEXCOORD2;  // xy: splat2, zw: splat3
        #if defined(_NORMALMAP)
            half4 normal     : TEXCOORD3; // xyz normalWS, w viewDir.x
            half4 tangent    : TEXCOORD4; // xyz tangentWS, w viewDir.y
            half4 bitangent  : TEXCOORD5; // xyz bitangentWS, w viewDir.z
        #else
            half3 normal     : TEXCOORD3;
        #endif
        half3 vertexSH       : TEXCOORD6;
        float3 positionWS    : TEXCOORD7;
        #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
            float4 shadowCoord : TEXCOORD8;
        #endif
        float4 clipPos       : SV_POSITION;
        UNITY_VERTEX_OUTPUT_STEREO
    };

    // -------------------------------------------------------------------------
    // Splat 混合：_Control 的 RGBA 通道 → 各层权重，加权混合 albedo / normal
    // -------------------------------------------------------------------------
    void NormalMapMix(float4 uvSplat01, float4 uvSplat23, inout half4 splatControl, inout half3 mixedNormal)
    {
    #if defined(_NORMALMAP)
        half3 nrm = half3(0, 0, 0);
        nrm += splatControl.r * UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal0, sampler_TrilinearRepeat, uvSplat01.xy), _NormalScale0);
        nrm += splatControl.g * UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal1, sampler_TrilinearRepeat, uvSplat01.zw), _NormalScale1);
        nrm += splatControl.b * UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal2, sampler_TrilinearRepeat, uvSplat23.xy), _NormalScale2);
        nrm += splatControl.a * UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal3, sampler_TrilinearRepeat, uvSplat23.zw), _NormalScale3);

        // 避免归一化时 NaN
        #if !HALF_IS_FLOAT
            nrm.z += 0.01h;
        #else
            nrm.z += 1e-5f;
        #endif
        mixedNormal = normalize(nrm.xyz);
    #endif
    }

    // 基于 Displacement（高度）贴图做层间混合：比线性羽化更自然，
    // 高的地方优先显现，过渡由 _HeightTransition 控制宽窄。
    void ComputeHeightBlend(float4 uvSplat01, float4 uvSplat23, inout half4 splatControl)
    {
        half4 height = half4(
            SAMPLE_TEXTURE2D(_Displacement0, sampler_TrilinearRepeat, uvSplat01.xy).r,
            SAMPLE_TEXTURE2D(_Displacement1, sampler_TrilinearRepeat, uvSplat01.zw).r,
            SAMPLE_TEXTURE2D(_Displacement2, sampler_TrilinearRepeat, uvSplat23.xy).r,
            SAMPLE_TEXTURE2D(_Displacement3, sampler_TrilinearRepeat, uvSplat23.zw).r);

        half4 splatHeight = height * splatControl;
        half maxHeight = max(splatHeight.r, max(splatHeight.g, max(splatHeight.b, splatHeight.a)));

        half transition = max(_HeightTransition, 1e-5h);
        half4 weightedHeights = max(0.0h, splatHeight + transition - maxHeight);
        // 加一点 epsilon，保证原本权重为零的层不会被抢回来
        weightedHeights = (weightedHeights + 1e-6h) * splatControl;

        half sumHeight = max(dot(weightedHeights, 1.0h), 1e-6h);
        splatControl = weightedHeights / sumHeight;
    }

    void SplatmapMix(float4 uvSplat01, float4 uvSplat23, half4 splatControl,
                     out half3 mixedDiffuse, inout half3 mixedNormal)
    {
        half3 diffAlbedo[4];
        diffAlbedo[0] = SAMPLE_TEXTURE2D(_Splat0, sampler_TrilinearRepeat, uvSplat01.xy).rgb;
        diffAlbedo[1] = SAMPLE_TEXTURE2D(_Splat1, sampler_TrilinearRepeat, uvSplat01.zw).rgb;
        diffAlbedo[2] = SAMPLE_TEXTURE2D(_Splat2, sampler_TrilinearRepeat, uvSplat23.xy).rgb;
        diffAlbedo[3] = SAMPLE_TEXTURE2D(_Splat3, sampler_TrilinearRepeat, uvSplat23.zw).rgb;

        mixedDiffuse = 0.0h;
        mixedDiffuse += diffAlbedo[0] * splatControl.rrr;
        mixedDiffuse += diffAlbedo[1] * splatControl.ggg;
        mixedDiffuse += diffAlbedo[2] * splatControl.bbb;
        mixedDiffuse += diffAlbedo[3] * splatControl.aaa;

        NormalMapMix(uvSplat01, uvSplat23, splatControl, mixedNormal);
    }

    // 采样 control 图 + 混合，输出 PBR 输入
    void TerrainSurface(Varyings IN, out half3 albedo, out half3 normalTS, out half metallic, out half smoothness, out half occlusion)
    {
        float2 splatUV = (IN.uvMainAndLM.xy * (_Control_TexelSize.zw - 1.0f) + 0.5f) * _Control_TexelSize.xy;
        half4 splatControl = SAMPLE_TEXTURE2D(_Control, sampler_TrilinearRepeat, splatUV);

        // 高度混合会把 splatControl 归一化，替代原来的简单除法
        ComputeHeightBlend(IN.uvSplat01, IN.uvSplat23, splatControl);

        normalTS = half3(0.0h, 0.0h, 1.0h);
        half3 mixedDiffuse;
        SplatmapMix(IN.uvSplat01, IN.uvSplat23, splatControl, mixedDiffuse, normalTS);
        albedo = mixedDiffuse;

        metallic = dot(splatControl, half4(_Metallic0, _Metallic1, _Metallic2, _Metallic3));

        half4 roughness = half4(
            SAMPLE_TEXTURE2D(_Roughness0, sampler_TrilinearRepeat, IN.uvSplat01.xy).r,
            SAMPLE_TEXTURE2D(_Roughness1, sampler_TrilinearRepeat, IN.uvSplat01.zw).r,
            SAMPLE_TEXTURE2D(_Roughness2, sampler_TrilinearRepeat, IN.uvSplat23.xy).r,
            SAMPLE_TEXTURE2D(_Roughness3, sampler_TrilinearRepeat, IN.uvSplat23.zw).r);
        smoothness = dot(splatControl, 1.0h - roughness);

        half4 ao = half4(
            SAMPLE_TEXTURE2D(_AO0, sampler_TrilinearRepeat, IN.uvSplat01.xy).r,
            SAMPLE_TEXTURE2D(_AO1, sampler_TrilinearRepeat, IN.uvSplat01.zw).r,
            SAMPLE_TEXTURE2D(_AO2, sampler_TrilinearRepeat, IN.uvSplat23.xy).r,
            SAMPLE_TEXTURE2D(_AO3, sampler_TrilinearRepeat, IN.uvSplat23.zw).r);
        occlusion = dot(splatControl, ao);
    }

    // -------------------------------------------------------------------------
    // InputData / GI
    // -------------------------------------------------------------------------
    void InitializeInputData(Varyings IN, half3 normalTS, out InputData inputData)
    {
        inputData = (InputData)0;

        inputData.positionWS = IN.positionWS;
        inputData.positionCS = IN.clipPos;

        #if defined(_NORMALMAP)
            half3 viewDirWS = half3(IN.normal.w, IN.tangent.w, IN.bitangent.w);
            inputData.tangentToWorld = half3x3(-IN.tangent.xyz, IN.bitangent.xyz, IN.normal.xyz);
            inputData.normalWS = TransformTangentToWorld(normalTS, inputData.tangentToWorld);
        #else
            half3 viewDirWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
            inputData.normalWS = IN.normal;
        #endif

        inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
        inputData.viewDirectionWS = viewDirWS;

        #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
            inputData.shadowCoord = IN.shadowCoord;
        #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
            inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
        #else
            inputData.shadowCoord = float4(0, 0, 0, 0);
        #endif

        inputData.fogCoord = InitializeInputDataFog(float4(IN.positionWS, 1.0), 0.0);
        inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.clipPos);
    }

    void InitializeBakedGIData(Varyings IN, inout InputData inputData)
    {
        half3 SH = IN.vertexSH;
        inputData.bakedGI = SAMPLE_GI(IN.uvMainAndLM.zw, SH, inputData.normalWS);
        inputData.shadowMask = SAMPLE_SHADOWMASK(IN.uvMainAndLM.zw);
    }

    // -------------------------------------------------------------------------
    // 顶点 shader
    // -------------------------------------------------------------------------
    Varyings SplatmapVert(Attributes v)
    {
        Varyings o = (Varyings)0;

        UNITY_SETUP_INSTANCE_ID(v);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
        TerrainInstancing(v.positionOS, v.normalOS, v.texcoord);

        VertexPositionInputs positionInputs = GetVertexPositionInputs(v.positionOS.xyz);

        o.uvMainAndLM.xy = v.texcoord;
        o.uvMainAndLM.zw = v.texcoord * unity_LightmapST.xy + unity_LightmapST.zw;

        o.uvSplat01.xy = TRANSFORM_TEX(v.texcoord, _Splat0);
        o.uvSplat01.zw = TRANSFORM_TEX(v.texcoord, _Splat1);
        o.uvSplat23.xy = TRANSFORM_TEX(v.texcoord, _Splat2);
        o.uvSplat23.zw = TRANSFORM_TEX(v.texcoord, _Splat3);

        #if defined(_NORMALMAP)
            half3 viewDirWS = GetWorldSpaceNormalizeViewDir(positionInputs.positionWS);
            float4 vertexTangent = float4(cross(float3(0, 0, 1), v.normalOS), 1.0);
            VertexNormalInputs normalInput = GetVertexNormalInputs(v.normalOS, vertexTangent);

            o.normal = half4(normalInput.normalWS, viewDirWS.x);
            o.tangent = half4(normalInput.tangentWS, viewDirWS.y);
            o.bitangent = half4(normalInput.bitangentWS, viewDirWS.z);
        #else
            o.normal = half3(TransformObjectToWorldNormal(v.normalOS));
        #endif

        OUTPUT_SH(o.normal.xyz, o.vertexSH);

        o.positionWS = positionInputs.positionWS;
        o.clipPos = positionInputs.positionCS;

        #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
            o.shadowCoord = GetShadowCoord(positionInputs);
        #endif

        return o;
    }

    // -------------------------------------------------------------------------
    // Forward 片段 shader（前向渲染路径）
    // -------------------------------------------------------------------------
    void SplatmapFragmentForward(Varyings IN, out half4 outColor : SV_Target0)
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
        #ifdef _ALPHATEST_ON
            ClipHoles(IN.uvMainAndLM.xy);
        #endif

        half3 normalTS;
        half3 albedo;
        half metallic;
        half smoothness;
        half occlusion;
        TerrainSurface(IN, albedo, normalTS, metallic, smoothness, occlusion);

        InputData inputData;
        InitializeInputData(IN, normalTS, inputData);
        InitializeBakedGIData(IN, inputData);

        half alpha = 1.0h;
        half4 color = UniversalFragmentPBR(inputData, albedo, metallic, half3(0, 0, 0), smoothness, occlusion, half3(0, 0, 0), alpha);
        color.rgb = MixFog(color.rgb, inputData.fogCoord);

        outColor = half4(color.rgb, 1.0h);
    }

    // -------------------------------------------------------------------------
    // GBuffer 片段 shader（延迟渲染路径）
    // -------------------------------------------------------------------------
    GBufferFragOutput SplatmapFragmentGBuffer(Varyings IN)
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
        #ifdef _ALPHATEST_ON
            ClipHoles(IN.uvMainAndLM.xy);
        #endif

        half3 normalTS;
        half3 albedo;
        half metallic;
        half smoothness;
        half occlusion;
        TerrainSurface(IN, albedo, normalTS, metallic, smoothness, occlusion);

        InputData inputData;
        InitializeInputData(IN, normalTS, inputData);
        InitializeBakedGIData(IN, inputData);

        half alpha = 1.0h;

        BRDFData brdfData;
        InitializeBRDFData(albedo, metallic, half3(0, 0, 0), smoothness, alpha, brdfData);

        // 烘焙 GI 写入 lighting buffer
        half4 color;
        Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
        MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);
        color.rgb = GlobalIllumination(brdfData, (BRDFData)0, 0, inputData.bakedGI, occlusion, inputData.positionWS,
                                       inputData.normalWS, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);
        color.a = alpha;

        return PackGBuffersBRDFData(brdfData, inputData, smoothness, color.rgb, occlusion);
    }

    // -------------------------------------------------------------------------
    // ShadowCaster / DepthOnly 用精简结构
    // -------------------------------------------------------------------------
    float3 _LightDirection;
    float3 _LightPosition;

    struct AttributesLean
    {
        float4 position : POSITION;
        float3 normalOS : NORMAL;
        float2 texcoord : TEXCOORD0;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct VaryingsLean
    {
        float4 clipPos  : SV_POSITION;
        float2 texcoord : TEXCOORD0;
        UNITY_VERTEX_OUTPUT_STEREO
    };

    VaryingsLean ShadowPassVertex(AttributesLean v)
    {
        VaryingsLean o = (VaryingsLean)0;
        UNITY_SETUP_INSTANCE_ID(v);
        TerrainInstancing(v.position, v.normalOS, v.texcoord);

        float3 positionWS = TransformObjectToWorld(v.position.xyz);
        float3 normalWS = TransformObjectToWorldNormal(v.normalOS);

        #if _CASTING_PUNCTUAL_LIGHT_SHADOW
            float3 lightDirectionWS = normalize(_LightPosition - positionWS);
        #else
            float3 lightDirectionWS = _LightDirection;
        #endif

        float4 clipPos = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

        #if UNITY_REVERSED_Z
            clipPos.z = min(clipPos.z, UNITY_NEAR_CLIP_VALUE);
        #else
            clipPos.z = max(clipPos.z, UNITY_NEAR_CLIP_VALUE);
        #endif

        o.clipPos = clipPos;
        o.texcoord = v.texcoord;
        return o;
    }

    half4 ShadowPassFragment(VaryingsLean IN) : SV_TARGET
    {
        #ifdef _ALPHATEST_ON
            ClipHoles(IN.texcoord);
        #endif
        return 0;
    }

    VaryingsLean DepthOnlyVertex(AttributesLean v)
    {
        VaryingsLean o = (VaryingsLean)0;
        UNITY_SETUP_INSTANCE_ID(v);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
        TerrainInstancing(v.position, v.normalOS);
        o.clipPos = TransformObjectToHClip(v.position.xyz);
        o.texcoord = v.texcoord;
        return o;
    }

    half4 DepthOnlyFragment(VaryingsLean IN) : SV_TARGET
    {
        #ifdef _ALPHATEST_ON
            ClipHoles(IN.texcoord);
        #endif
        return IN.clipPos.z;
    }

    half4 SceneSelectionFragment(VaryingsLean IN) : SV_TARGET
    {
        #ifdef _ALPHATEST_ON
            ClipHoles(IN.texcoord);
        #endif
        return half4(_ObjectId, _PassValue, 1.0, 1.0);
    }

    // -------------------------------------------------------------------------
    // DepthNormals pass
    // -------------------------------------------------------------------------
    struct AttributesDepthNormal
    {
        float4 positionOS : POSITION;
        half3 normalOS    : NORMAL;
        float2 texcoord   : TEXCOORD0;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct VaryingsDepthNormal
    {
        float4 uvMainAndLM : TEXCOORD0;
        float4 uvSplat01   : TEXCOORD1;
        float4 uvSplat23   : TEXCOORD2;
        #if defined(_NORMALMAP)
            half4 normal     : TEXCOORD3;
            half4 tangent    : TEXCOORD4;
            half4 bitangent  : TEXCOORD5;
        #else
            half3 normal     : TEXCOORD3;
        #endif
        float4 clipPos      : SV_POSITION;
        UNITY_VERTEX_OUTPUT_STEREO
    };

    VaryingsDepthNormal DepthNormalOnlyVertex(AttributesDepthNormal v)
    {
        VaryingsDepthNormal o = (VaryingsDepthNormal)0;

        UNITY_SETUP_INSTANCE_ID(v);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
        TerrainInstancing(v.positionOS, v.normalOS, v.texcoord);

        const VertexPositionInputs positionInputs = GetVertexPositionInputs(v.positionOS.xyz);

        o.uvMainAndLM.xy = v.texcoord;
        o.uvMainAndLM.zw = v.texcoord * unity_LightmapST.xy + unity_LightmapST.zw;
        o.uvSplat01.xy = TRANSFORM_TEX(v.texcoord, _Splat0);
        o.uvSplat01.zw = TRANSFORM_TEX(v.texcoord, _Splat1);
        o.uvSplat23.xy = TRANSFORM_TEX(v.texcoord, _Splat2);
        o.uvSplat23.zw = TRANSFORM_TEX(v.texcoord, _Splat3);

        #if defined(_NORMALMAP)
            half3 viewDirWS = GetWorldSpaceNormalizeViewDir(positionInputs.positionWS);
            float4 vertexTangent = float4(cross(float3(0, 0, 1), v.normalOS), 1.0);
            VertexNormalInputs normalInput = GetVertexNormalInputs(v.normalOS, vertexTangent);

            o.normal = half4(normalInput.normalWS, viewDirWS.x);
            o.tangent = half4(normalInput.tangentWS, viewDirWS.y);
            o.bitangent = half4(normalInput.bitangentWS, viewDirWS.z);
        #else
            o.normal = half3(TransformObjectToWorldNormal(v.normalOS));
        #endif

        o.clipPos = positionInputs.positionCS;
        return o;
    }

    void DepthNormalOnlyFragment(VaryingsDepthNormal IN, out half4 outNormalWS : SV_Target0)
    {
        #ifdef _ALPHATEST_ON
            ClipHoles(IN.uvMainAndLM.xy);
        #endif

        float2 splatUV = (IN.uvMainAndLM.xy * (_Control_TexelSize.zw - 1.0f) + 0.5f) * _Control_TexelSize.xy;
        half4 splatControl = SAMPLE_TEXTURE2D(_Control, sampler_TrilinearRepeat, splatUV);
        ComputeHeightBlend(IN.uvSplat01, IN.uvSplat23, splatControl);

        half3 normalTS = half3(0.0h, 0.0h, 1.0h);
        NormalMapMix(IN.uvSplat01, IN.uvSplat23, splatControl, normalTS);

        #if defined(_NORMALMAP)
            half3 normalWS = TransformTangentToWorld(normalTS, half3x3(-IN.tangent.xyz, IN.bitangent.xyz, IN.normal.xyz));
        #else
            half3 normalWS = IN.normal;
        #endif

        normalWS = NormalizeNormalPerPixel(normalWS);
        outNormalWS = half4(normalWS, 0.0);
    }
    ENDHLSL

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry-100"
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "False"
            "TerrainCompatible" = "True"
        }

        // ---------------------------------------------------------------------
        // 前向渲染
        // ---------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex SplatmapVert
            #pragma fragment SplatmapFragmentForward

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ _MIXED_LIGHTING_SUBTRACTIVE
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap

            #pragma shader_feature_local _NORMALMAP
            ENDHLSL
        }

        // ---------------------------------------------------------------------
        // 延迟渲染（GBuffer）
        // ---------------------------------------------------------------------
        Pass
        {
            Name "GBuffer"
            Tags { "LightMode" = "UniversalGBuffer" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles3 glcore
            #pragma vertex SplatmapVert
            #pragma fragment SplatmapFragmentGBuffer

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ _MIXED_LIGHTING_SUBTRACTIVE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap

            #pragma shader_feature_local _NORMALMAP
            ENDHLSL
        }

        // ---------------------------------------------------------------------
        // ShadowCaster
        // ---------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            ENDHLSL
        }

        // ---------------------------------------------------------------------
        // DepthOnly
        // ---------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            ENDHLSL
        }

        // ---------------------------------------------------------------------
        // DepthNormals
        // ---------------------------------------------------------------------
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex DepthNormalOnlyVertex
            #pragma fragment DepthNormalOnlyFragment
            #pragma shader_feature_local _NORMALMAP
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            ENDHLSL
        }

        // ---------------------------------------------------------------------
        // 编辑器场景选择
        // ---------------------------------------------------------------------
        Pass
        {
            Name "SceneSelectionPass"
            Tags { "LightMode" = "SceneSelectionPass" }

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex DepthOnlyVertex
            #pragma fragment SceneSelectionFragment
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            ENDHLSL
        }

        UsePass "Hidden/Nature/Terrain/Utilities/PICKING"
    }

    Dependency "AddPassShader" = "Hidden/Universal Render Pipeline/Terrain/Lit (Add Pass)"
    Dependency "BaseMapShader" = "Hidden/Universal Render Pipeline/Terrain/Lit (Base Pass)"
    Dependency "BaseMapGenShader" = "Hidden/Universal Render Pipeline/Terrain/Lit (Basemap Gen)"
    // 不用 TerrainLitShaderGUI：它只认识官方固定属性名，会把 Roughness/Displacement/AO
    // 这几个自定义插槽隐藏掉。去掉后材质用默认 Inspector，所有贴图插槽都能看到并拖拽。
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
