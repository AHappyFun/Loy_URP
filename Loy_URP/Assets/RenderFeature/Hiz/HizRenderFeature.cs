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

public class HizRenderFeature : ScriptableRendererFeature
{
    /// <summary>Hiz 是否在启用状态，供依赖 _HiZMip* 的特性（HBAO/SSGI/SSR）检查。</summary>
    public static bool IsActive { get; private set; }

    public HiZSettings settings = new HiZSettings();

    HiZPass hizPass;

    public override void Create()
    {
        IsActive = false; // 特性重建/切换时重置，由 AddRenderPasses 实际启用后再置 true
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
        IsActive = true;
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

        class CopyPassData
        {
            public ComputeShader cs;
            public int kernel;
            public TextureHandle srcDepth;
            public TextureHandle dstMip;
            public int dispatchX;
            public int dispatchY;
            public int mipCount;
        }

        class BuildPassData
        {
            public ComputeShader cs;
            public int kernel;
            public TextureHandle srcMip;
            public TextureHandle dstMip;
            public int srcWidth;
            public int srcHeight;
            public int dispatchX;
            public int dispatchY;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            if (cs == null || !resourcesData.activeDepthTexture.IsValid()) return;

            mipCount = s.mipCount;
            if (mipCount < 1) mipCount = 1;

            // 在图中创建 RFloat 的 mip 纹理链（enableRandomWrite 供 RWTexture2D 写入）
            TextureDesc mipDesc = renderGraph.GetTextureDesc(resourcesData.activeDepthTexture);
            mipDesc.format = GraphicsFormat.R32_SFloat;
            mipDesc.depthBufferBits = 0;
            mipDesc.msaaSamples = MSAASamples.None;
            mipDesc.enableRandomWrite = true;
            mipDesc.useMipMap = false;
            mipDesc.clearBuffer = true;

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

            // Pass 1: 拷贝深度 → mips[0]
            using (var builder = renderGraph.AddComputePass<CopyPassData>("HiZ Copy Depth", out var copyData, m_ProfilingSampler))
            {
                copyData.cs = cs;
                copyData.kernel = hizKernel_CopyDepth;
                copyData.srcDepth = resourcesData.activeDepthTexture;
                copyData.dstMip = mips[0];
                copyData.dispatchX = Mathf.CeilToInt(renderGraph.GetTextureDesc(mips[0]).width / 8f);
                copyData.dispatchY = Mathf.CeilToInt(renderGraph.GetTextureDesc(mips[0]).height / 8f);
                copyData.mipCount = mipCount;

                builder.UseTexture(resourcesData.activeDepthTexture, AccessFlags.Read);
                builder.UseTexture(mips[0], AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(mips[0], Shader.PropertyToID(kHiZNamePrefix + 0));
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (CopyPassData data, ComputeGraphContext ctx) =>
                {
                    // 在最早的 pass 设置 mip 总数，供所有 SampleHIZ 使用者读取
                    ctx.cmd.SetGlobalInt(kHiZMipCount, data.mipCount);
                    ctx.cmd.SetComputeTextureParam(data.cs, data.kernel, kSrcDepth, data.srcDepth);
                    ctx.cmd.SetComputeTextureParam(data.cs, data.kernel, kFirstMip, data.dstMip);
                    ctx.cmd.DispatchCompute(data.cs, data.kernel, data.dispatchX, data.dispatchY, 1);
                });
            }

            // Pass 2..N: 逐级构建 mip
            for (int i = 1; i < mipCount; ++i)
            {
                TextureHandle src = mips[i - 1];
                TextureHandle dst = mips[i];
                TextureDesc dstDesc = renderGraph.GetTextureDesc(dst);
                TextureDesc srcDesc = renderGraph.GetTextureDesc(src);

                using (var builder = renderGraph.AddComputePass<BuildPassData>("HiZ Build Mip " + i, out var buildData, m_ProfilingSampler))
                {
                    buildData.cs = cs;
                    buildData.kernel = hizKernel_BuildMip;
                    buildData.srcMip = src;
                    buildData.dstMip = dst;
                    buildData.srcWidth = srcDesc.width;
                    buildData.srcHeight = srcDesc.height;
                    buildData.dispatchX = Mathf.CeilToInt(dstDesc.width / 8f);
                    buildData.dispatchY = Mathf.CeilToInt(dstDesc.height / 8f);

                    builder.UseTexture(src, AccessFlags.Read);
                    builder.UseTexture(dst, AccessFlags.Write);
                    builder.SetGlobalTextureAfterPass(dst, Shader.PropertyToID(kHiZNamePrefix + i));

                    builder.SetRenderFunc(static (BuildPassData data, ComputeGraphContext ctx) =>
                    {
                        ctx.cmd.SetComputeTextureParam(data.cs, data.kernel, kSrcMip, data.srcMip);
                        ctx.cmd.SetComputeTextureParam(data.cs, data.kernel, kDstMip, data.dstMip);
                        ctx.cmd.SetComputeIntParam(data.cs, kSrcWidth, data.srcWidth);
                        ctx.cmd.SetComputeIntParam(data.cs, kSrcHeight, data.srcHeight);
                        ctx.cmd.DispatchCompute(data.cs, data.kernel, data.dispatchX, data.dispatchY, 1);
                    });
                }
            }

        }
    }
}
