using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 简易 Auto-Exposure（对标 UE4 的眼睛自适应曝光）。
///
/// 原理：
///   1. 把场景 HDR 颜色降采样成小 RT（平均 log2 luminance）
///   2. AsyncGPUReadback 读回 → 算目标曝光 EV = log2(targetLuma / avgLuma)
///   3. 按 adaptationSpeed 平滑过渡，写入 Global Volume 的 ColorAdjustments.postExposure
///      （postExposure 是 EV，在 tonemapping 前生效，和 UE4 曝光时机一致）
///
/// 用法：把本 feature 加到渲染器，设置 settings.profile = 你的 Global Volume 的 profile。
/// </summary>
[Serializable]
public class AutoExposureSettings
{
    public VolumeProfile profile;                              // 全局 volume profile（写 postExposure 用）
    public RenderPassEvent passEvent = RenderPassEvent.BeforeRenderingPostProcessing;

    [Tooltip("目标亮度（场景平均亮度向它收敛）")]
    public float targetLuminance = 0.18f;

    [Tooltip("自适应速度（0~1，每帧向目标 EV 靠拢的比例）")]
    [Range(0.01f, 1f)] public float adaptationSpeed = 0.3f;

    [Tooltip("曝光 EV 下限")]
    public float minExposureEV = -2f;

    [Tooltip("曝光 EV 上限")]
    public float maxExposureEV = 2f;

    [Tooltip("手动曝光偏移（等效 UE4 的 exposure compensation）")]
    public float exposureOffsetEV = 0f;

    [Tooltip("亮度降采样分辨率（越小越快，建议 8~32）")]
    public int lumaRes = 16;

    [Tooltip("每输出 texel 采样网格（越大越准，建议 8）")]
    public int lumaSamples = 8;
}

public class AutoExposureRenderFeature : ScriptableRendererFeature
{
    public AutoExposureSettings settings = new AutoExposureSettings();

    /// <summary>供场景里的 AutoExposureController 读取亮度 RT 做回读。</summary>
    public static AutoExposureRenderFeature Instance { get; private set; }

    AutoExposurePass m_Pass;

    public RTHandle GetLumaRT() => m_Pass != null ? m_Pass.LumaRT : null;

    /// <summary>调试：手动设置曝光 EV（跳过自适应计算）。</summary>
    public void DebugSetEV(float ev) => m_Pass?.DebugSetEV(ev);

    public override void Create()
    {
        Instance = this;
        m_Pass = new AutoExposurePass(settings)
        {
            renderPassEvent = settings.passEvent
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_Pass == null)
            return;

        // 只在 Game/SceneView 生效，跳过 Preview / 反射等相机
        var camType = renderingData.cameraData.cameraType;
        if (camType != CameraType.Game && camType != CameraType.SceneView)
            return;

        renderer.EnqueuePass(m_Pass);
    }

    protected override void Dispose(bool disposing)
    {
        if (Instance == this)
            Instance = null;
        m_Pass?.Dispose();
        m_Pass = null;
    }

    // ---------------------------------------------------------------------
    // Pass
    // ---------------------------------------------------------------------

    class PassData
    {
        public AutoExposurePass pass;
        public Material material;
        public TextureHandle source;
        public int lumaRes;
        public int lumaSamples;
        public MaterialPropertyBlock block;
    }

    class AutoExposurePass : ScriptableRenderPass, IDisposable
    {
        readonly AutoExposureSettings m_Settings;
        readonly ProfilingSampler m_ProfilingSamplerGroup = new ProfilingSampler("Loy_AutoExposure");
        readonly ProfilingSampler m_ProfilingSamplerLuma = new ProfilingSampler("Loy_AutoExposure Luma");
        readonly ProfilingSampler m_ProfilingSamplerApply = new ProfilingSampler("Loy_AutoExposure Apply");

        Material m_Material;
        RTHandle m_LumaRT;
        bool m_PendingReadback;
        float m_CurrentEV;
        float m_LastAvgLuma;

        static readonly int kSourceTex = Shader.PropertyToID("_SourceTex");
        static readonly int kLumaRes = Shader.PropertyToID("_LumaRes");
        static readonly int kLumaSamples = Shader.PropertyToID("_LumaSamples");
        static readonly int kExposureEV = Shader.PropertyToID("_ExposureEV");

        public AutoExposurePass(AutoExposureSettings settings)
        {
            m_Settings = settings;
        }

        void EnsureMaterial()
        {
            if (m_Material == null)
                m_Material = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/Loy/AutoExposure"));
        }

        void EnsureLumaRT()
        {
            if (m_LumaRT != null)
                return;

            int res = Mathf.Clamp(m_Settings.lumaRes, 4, 64);
            var desc = new RenderTextureDescriptor(res, res, RenderTextureFormat.RFloat, 0);
            desc.sRGB = false;
            m_LumaRT = RTHandles.Alloc(desc, name: "_AutoExposureLuma");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            if (!resourcesData.activeColorTexture.IsValid())
                return;

            EnsureMaterial();
            EnsureLumaRT();

            TextureHandle lumaHandle = renderGraph.ImportTexture(m_LumaRT);

            // 外层分组：Frame Debugger 里 "Loy_AutoExposure" 下嵌套 Luma / Apply 两个阶段
            renderGraph.BeginProfilingSampler(m_ProfilingSamplerGroup);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(
                       "Loy_AutoExposure Luma", out var passData, m_ProfilingSamplerLuma))
            {
                passData.pass = this;
                passData.material = m_Material;
                passData.source = resourcesData.activeColorTexture;
                passData.lumaRes = m_LumaRT.rt.width;
                passData.lumaSamples = m_Settings.lumaSamples;
                passData.block = new MaterialPropertyBlock();

                builder.UseTexture(resourcesData.activeColorTexture, AccessFlags.Read);
                builder.SetRenderAttachment(lumaHandle, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    data.pass.ExecuteLuma(ctx.cmd, data);
                });
            }

            // 应用曝光：颜色 × 2^EV（在 luma 之后，tonemapping 之前）
            TextureDesc colorDesc = renderGraph.GetTextureDesc(resourcesData.activeColorTexture);
            colorDesc.name = "_AutoExposureApplied";
            TextureHandle applied = renderGraph.CreateTexture(colorDesc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(
                       "Loy_AutoExposure Apply", out var applyData, m_ProfilingSamplerApply))
            {
                applyData.pass = this;
                applyData.material = m_Material;
                applyData.source = resourcesData.activeColorTexture;
                applyData.block = new MaterialPropertyBlock();

                builder.UseTexture(resourcesData.activeColorTexture, AccessFlags.Read);
                builder.SetRenderAttachment(applied, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    data.pass.ExecuteApply(ctx.cmd, data);
                });
            }

            renderGraph.EndProfilingSampler(m_ProfilingSamplerGroup);

            // 后续后处理（tonemapping 等）使用曝光后的颜色
            resourcesData.cameraColor = applied;
        }

        void ExecuteLuma(RasterCommandBuffer cmd, PassData data)
        {
            MaterialPropertyBlock block = data.block;
            block.Clear();
            block.SetTexture(kSourceTex, data.source);
            block.SetFloat(kLumaRes, data.lumaRes);
            block.SetFloat(kLumaSamples, data.lumaSamples);
            cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, block);

            // 回调版回读（引擎泵，不依赖 MonoBehaviour Update，编辑模式可用）
            if (!m_PendingReadback && m_LumaRT?.rt != null)
            {
                m_PendingReadback = true;
                AsyncGPUReadback.Request(
                    (Texture)m_LumaRT.rt, 0,
                    0, m_LumaRT.rt.width,
                    0, m_LumaRT.rt.height,
                    0, 1,
                    GraphicsFormat.R32_SFloat,
                    OnReadback);
            }
        }

        void OnReadback(AsyncGPUReadbackRequest request)
        {
            m_PendingReadback = false;
            if (request.hasError || m_Settings == null)
                return;

            NativeArray<float> data = request.GetData<float>();
            int count = data.Length;
            if (count < 1)
                return;

            // 平均 log2(luminance) → 几何平均亮度
            float sum = 0f;
            for (int i = 0; i < count; i++)
                sum += data[i];
            float avgLogLuma = sum / count;
            float avgLuma = Mathf.Pow(2f, avgLogLuma);
            m_LastAvgLuma = avgLuma;

            // 目标 EV，让平均亮度收敛到 targetLuminance
            float targetEV = Mathf.Log(Mathf.Max(m_Settings.targetLuminance, 1e-5f) / Mathf.Max(avgLuma, 1e-5f), 2f);
            targetEV += m_Settings.exposureOffsetEV;
            targetEV = Mathf.Clamp(targetEV, m_Settings.minExposureEV, m_Settings.maxExposureEV);

            m_CurrentEV = Mathf.Lerp(m_CurrentEV, targetEV, m_Settings.adaptationSpeed);
        }

        // 应用曝光：把颜色 × 2^EV（tonemapping 前，绕过后处理 volume/LUT）
        void ExecuteApply(RasterCommandBuffer cmd, PassData data)
        {
            MaterialPropertyBlock block = data.block;
            block.Clear();
            block.SetTexture(kSourceTex, data.source);
            block.SetFloat(kExposureEV, m_CurrentEV);
            cmd.DrawProcedural(Matrix4x4.identity, data.material, 1, MeshTopology.Triangles, 3, 1, block);
        }

        /// <summary>当前曝光 EV（供场景脚本显示/调试）。</summary>
        public float CurrentEV => m_CurrentEV;

        /// <summary>调试：手动设置曝光 EV。</summary>
        public void DebugSetEV(float ev) => m_CurrentEV = ev;

        /// <summary>亮度 RT。</summary>
        public RTHandle LumaRT => m_LumaRT;

        public void Dispose()
        {
            m_LumaRT?.Release();
            m_LumaRT = null;
            CoreUtils.Destroy(m_Material);
            m_Material = null;
        }
    }
}
