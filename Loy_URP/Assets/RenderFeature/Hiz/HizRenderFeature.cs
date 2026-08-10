using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

[Serializable]
public class HiZSettings
{
    public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingGbuffer;
    public ComputeShader hizBuildCS = null;
    public int mipCount = 8; // stop mip when smaller than this
}

/// <summary>
/// Hi-Z resources for the current render graph frame. Consumers use these
/// handles directly so unrelated global-texture users do not keep Hi-Z alive.
/// </summary>
public sealed class HiZFrameData : ContextItem
{
    public TextureHandle[] mips;
    public int mipCount;

    public override void Reset()
    {
        mips = null;
        mipCount = 0;
    }
}

public class HizRenderFeature : ScriptableRendererFeature
{
    /// <summary>Hiz 是否在启用状态，供依赖 _HiZMip* 的特性（HBAO/SSGI/SSR）检查。</summary>
    public static bool IsActive { get; private set; }

    /// <summary>实际构建的 mip 数，供 SSR 等通过 MaterialPropertyBlock 显式传 _HiZMipCount（RG 里 SetGlobalInt 跨 pass 不可靠）。</summary>
    public static int MipCount { get; private set; }

    public HiZSettings settings = new HiZSettings();

    HiZPass hizPass;

    public override void Create()
    {
        IsActive = false; // 特性重建/切换时重置，由 AddRenderPasses 实际启用后再置 true
        MipCount = 0;

        hizPass = new HiZPass(settings)
        {
            renderPassEvent = settings.renderPassEvent
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.hizBuildCS == null)
        {
            IsActive = false;
            Debug.LogWarning("HiZFeature: ComputeShader not assigned.");
            return;
        }

        // 始终把逻辑 Pass 录入 Render Graph。没有消费者读取 HiZFrameData 时，
        // Render Graph 会自动剔除 HiZ Build 及其临时纹理。
        IsActive = true;
        MipCount = Mathf.Max(1, settings.mipCount);
        hizPass.Setup();

        renderer.EnqueuePass(hizPass);
    }

    class HiZPass : ScriptableRenderPass
    {
        readonly HiZSettings s;
        readonly ProfilingSampler m_ProfilingSampler;
        ComputeShader cs;

#if URP_COMPATIBILITY_MODE
        // resources
        private RenderTexture[] mipsTex;
#endif

        int hizKernel_CopyDepth;
        int hizKernel_BuildMip;

        // shader property IDs
        static readonly int kSrcDepth = Shader.PropertyToID("_SrcDepthTexture");
        static readonly int kHiZMipCount = Shader.PropertyToID("_HiZMipCount");
        static readonly int kFirstMip = Shader.PropertyToID("_FirstMip");
        static readonly int kSrcMip = Shader.PropertyToID("_SrcMip");
        static readonly int kDstMip = Shader.PropertyToID("_DstMip");
        static readonly int kSrcWidth = Shader.PropertyToID("SrcWidth");
        static readonly int kSrcHeight = Shader.PropertyToID("SrcHeight");
        static readonly string kHiZNamePrefix = "_HiZMip"; // we will set _HiZMip0, _HiZMip1 ...

        public HiZPass(HiZSettings settings)
        {
            s = settings;
            cs = settings.hizBuildCS;
            m_ProfilingSampler = new ProfilingSampler("Loy_HiZ Build");
        }

        public void Setup()
        {
            if (cs == null) return;
            hizKernel_CopyDepth = cs.FindKernel("KCopyDepth");
            hizKernel_BuildMip = cs.FindKernel("KBuildMip");
        }

#if URP_COMPATIBILITY_MODE
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            Camera cam = renderingData.cameraData.camera;
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            int w = desc.width;
            int h = desc.height;

            mipCount = s.mipCount;
            if (mipCount < 1) mipCount = 1;

            if (mipsTex == null)
            {
                mipsTex = new RenderTexture[mipCount];

                int cw = w, ch = h;
                for (int i = 0; i < mipCount; ++i)
                {
                    mipsTex[i] = new RenderTexture(cw, ch, 0, RenderTextureFormat.RFloat);
                    mipsTex[i].enableRandomWrite = true;
                    mipsTex[i].Create();

                    cw = Math.Max(1, cw >> 1);
                    ch = Math.Max(1, ch >> 1);
                }
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var renderer = renderingData.cameraData.renderer;
            var cmd = CommandBufferPool.Get("Loy_HiZ Build");
            Camera cam = renderingData.cameraData.camera;

            // 第一次 CopyDepth
            {
                hizKernel_CopyDepth = cs.FindKernel("KCopyDepth");
                var depthRT = renderer.cameraDepthTargetHandle;

                cmd.SetComputeTextureParam(cs, hizKernel_CopyDepth, kSrcDepth, depthRT);
                cmd.SetComputeTextureParam(cs, hizKernel_CopyDepth, kFirstMip, mipsTex[0]);

                cmd.DispatchCompute(cs, hizKernel_CopyDepth, mipsTex[0].width / 8, mipsTex[0].height / 8, 1);
            }

            // 2) Build subsequent mips: for each i from 1..mipCount-1, read from mips[i-1], write to mips[i]
            for (int i = 1; i < mipCount; ++i)
            {
                RenderTexture src = mipsTex[i - 1];
                RenderTexture dst = mipsTex[i];

                cmd.SetComputeTextureParam(cs, hizKernel_BuildMip, "_SrcMip", src);
                cmd.SetComputeTextureParam(cs, hizKernel_BuildMip, "_DstMip", dst);
                cmd.SetComputeIntParam(cs, "SrcWidth", src.width);
                cmd.SetComputeIntParam(cs, "SrcHeight", src.height);
                cmd.DispatchCompute(cs, hizKernel_BuildMip, (dst.width + 7) / 8, (dst.height + 7) / 8, 1);
            }

            // 3) Expose as global textures (_HiZMip0, _HiZMip1, ..., _HiZMipN) and set mip count
            cmd.SetGlobalInt(kHiZMipCount, mipCount);
            for (int i = 0; i < mipCount; ++i)
            {
                cmd.SetGlobalTexture(kHiZNamePrefix + i, mipsTex[i]);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            CommandBufferPool.Release(cmd);
        }
#endif

        int mipCount;

        class PassData
        {
            public ComputeShader cs;
            public int copyKernel;
            public int buildKernel;
            public TextureHandle srcDepth;
            public TextureHandle[] mips;
            public int mipCount;
            public int copyDispatchX;
            public int copyDispatchY;
            public int[] srcWidths;
            public int[] srcHeights;
            public int[] dispatchXs;
            public int[] dispatchYs;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            if (cs == null) return;

            // 深度源：用 _CameraDepthTexture 的解析拷贝（resourcesData.cameraDepth）。
            // 它是已解析的 R32 普通纹理，保证能被 compute 当 SRV 读。
            // 直接读 activeDepthTexture（深度-stencil 活缓冲）在 RG 里绑 SRV 经常读到 0 → HiZ 金字塔全空。
            // （SSR/HBAO 用 SampleSceneDepth 读的也是 _CameraDepthTexture，深度一致）
            TextureHandle srcDepth = resourcesData.cameraDepth.IsValid() ? resourcesData.cameraDepth : resourcesData.activeDepthTexture;
            if (!srcDepth.IsValid()) return;

            mipCount = s.mipCount;
            if (mipCount < 1) mipCount = 1;

            // 在图中创建 RFloat 的 mip 纹理链（enableRandomWrite 供 RWTexture2D 写入）
            // 每个 mip 每帧都被 compute pass 完全写入，无需 clear，减少调试器里的 Clear 条目
            TextureDesc mipDesc = renderGraph.GetTextureDesc(srcDepth);
            mipDesc.format = GraphicsFormat.R32_SFloat;
            mipDesc.depthBufferBits = 0;
            mipDesc.msaaSamples = MSAASamples.None;
            mipDesc.enableRandomWrite = true;
            mipDesc.useMipMap = false;
            mipDesc.clearBuffer = false;

            TextureHandle[] mips = new TextureHandle[mipCount];
            int cw = mipDesc.width, ch = mipDesc.height;
            for (int i = 0; i < mipCount; ++i)
            {
                mipDesc.width = cw;
                mipDesc.height = ch;
                mipDesc.name = kHiZNamePrefix + i;
                mips[i] = renderGraph.CreateTexture(mipDesc);
                cw = Math.Max(1, cw >> 1);
                ch = Math.Max(1, ch >> 1);
            }

            HiZFrameData hiZData = frameData.Create<HiZFrameData>();
            hiZData.mips = mips;
            hiZData.mipCount = mipCount;

            // 单个 compute pass：先拷贝深度到 mips[0]，再逐级构建 mip。
            // 合并成一个 pass，保证调试器里属于同一组。
            // 消费者通过 HiZFrameData 取得句柄并调用 UseTexture 声明依赖，
            // RG 会根据依赖自然保留并正确排序本 pass（无消费者时按 RG 语义裁剪，属于正常优化）。
            using (var builder = renderGraph.AddComputePass<PassData>("HiZ Build", out var passData, m_ProfilingSampler))
            {
                passData.cs = cs;
                passData.copyKernel = hizKernel_CopyDepth;
                passData.buildKernel = hizKernel_BuildMip;
                passData.srcDepth = srcDepth;
                passData.mips = mips;
                passData.mipCount = mipCount;
                passData.copyDispatchX = Mathf.CeilToInt(renderGraph.GetTextureDesc(mips[0]).width / 8f);
                passData.copyDispatchY = Mathf.CeilToInt(renderGraph.GetTextureDesc(mips[0]).height / 8f);
                passData.srcWidths = new int[mipCount];
                passData.srcHeights = new int[mipCount];
                passData.dispatchXs = new int[mipCount];
                passData.dispatchYs = new int[mipCount];
                for (int i = 0; i < mipCount; ++i)
                {
                    TextureDesc d = renderGraph.GetTextureDesc(mips[i]);
                    if (i > 0)
                    {
                        TextureDesc s = renderGraph.GetTextureDesc(mips[i - 1]);
                        passData.srcWidths[i] = s.width;
                        passData.srcHeights[i] = s.height;
                    }
                    passData.dispatchXs[i] = Mathf.CeilToInt(d.width / 8f);
                    passData.dispatchYs[i] = Mathf.CeilToInt(d.height / 8f);
                }

                builder.UseTexture(srcDepth, AccessFlags.Read);
                for (int i = 0; i < mipCount; ++i)
                    builder.UseTexture(mips[i], AccessFlags.ReadWrite);
                // 注意：不能 AllowGlobalStateModification(true)——它在 RG 里会调用 AllowPassCulling(false)，
                // 让本 pass 永不剔除（即使 SSR 关掉、_HiZMip* 没消费者也照跑）。
                // render func 里已无全局改动（SetGlobalInt 已删），不需要它。

                builder.SetRenderFunc(static (PassData data, ComputeGraphContext ctx) =>
                {
                    // 不再 SetGlobalInt(_HiZMipCount)：全局状态副作用会阻止 RG 剔除本 pass。
                    // SSR 已通过 MaterialPropertyBlock 显式传 HizRenderFeature.MipCount，不需要这个全局。

                    // 拷贝深度 → mips[0]
                    ctx.cmd.SetComputeTextureParam(data.cs, data.copyKernel, kSrcDepth, data.srcDepth);
                    ctx.cmd.SetComputeTextureParam(data.cs, data.copyKernel, kFirstMip, data.mips[0]);
                    ctx.cmd.DispatchCompute(data.cs, data.copyKernel, data.copyDispatchX, data.copyDispatchY, 1);

                    // 逐级构建 mip：mips[i-1] → mips[i]
                    for (int i = 1; i < data.mipCount; ++i)
                    {
                        ctx.cmd.SetComputeTextureParam(data.cs, data.buildKernel, kSrcMip, data.mips[i - 1]);
                        ctx.cmd.SetComputeTextureParam(data.cs, data.buildKernel, kDstMip, data.mips[i]);
                        ctx.cmd.SetComputeIntParam(data.cs, kSrcWidth, data.srcWidths[i]);
                        ctx.cmd.SetComputeIntParam(data.cs, kSrcHeight, data.srcHeights[i]);
                        ctx.cmd.DispatchCompute(data.cs, data.buildKernel, data.dispatchXs[i], data.dispatchYs[i], 1);
                    }
                });
            }
        }
    }
}
