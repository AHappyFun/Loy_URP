using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[Serializable]
public class HiZSettings
{
    public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingGbuffer;
    public ComputeShader hizBuildCS = null;
    //public bool enableCopyDepth = false;
    public int minSize = 8; // stop mip when smaller than this
}

public class HizRenderFeature : ScriptableRendererFeature
{
    public HiZSettings settings = new HiZSettings();

    HiZPass hizPass;

    public override void Create()
    {
        hizPass = new HiZPass(settings)
        {
            renderPassEvent = settings.renderPassEvent
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.hizBuildCS == null)
        {
            Debug.LogWarning("HiZFeature: ComputeShader not assigned.");
            return;
        }
        hizPass.Setup();
        renderer.EnqueuePass(hizPass);
    }

    class HiZPass : ScriptableRenderPass
    {
        HiZSettings s;
        ComputeShader cs;

        // resources
        //private RTHandle[] mipsHandle;
        private RenderTexture[] mipsTex;

        int mipCount;
        int hizKernel_CopyDepth;
        int hizKernel_BuildMip;

        // shader property IDs
        static readonly int kSrcDepth = Shader.PropertyToID("_SrcDepthTexture");
        static readonly int kHiZMipCount = Shader.PropertyToID("_HiZMipCount");
        static readonly int kFirstMip = Shader.PropertyToID("_FirstMip");
        static readonly string kHiZNamePrefix = "_HiZMip"; // we will set _HiZMip0, _HiZMip1 ...

        public HiZPass(HiZSettings settings)
        {
            s = settings;
            cs = settings.hizBuildCS;
        }

        public void Setup()
        {
            hizKernel_CopyDepth = cs.FindKernel("KCopyDepth");
            hizKernel_BuildMip = cs.FindKernel("KBuildMip");
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            Camera cam = renderingData.cameraData.camera;
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            int w = desc.width;
            int h = desc.height;

            // compute mip count
            int maxDim = Math.Max(w, h);
            mipCount = 0;
            int dim = maxDim;
            while (dim >= s.minSize)
            {
                dim = dim >> 1;
                mipCount++;
            }

            if (mipCount < 1) mipCount = 1;
            mipCount = 8;

            if(mipsTex == null)
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
            var cmd = CommandBufferPool.Get("HiZ Build");
            Camera cam = renderingData.cameraData.camera;

            //第一次CopyDepth
            {
                hizKernel_CopyDepth = cs.FindKernel("KCopyDepth");
                var depthRT = renderer.cameraDepthTargetHandle;

                cmd.SetComputeTextureParam(cs, hizKernel_CopyDepth, kSrcDepth, depthRT);
                cmd.SetComputeTextureParam(cs, hizKernel_CopyDepth, kFirstMip, mipsTex[0]);

                cmd.DispatchCompute(cs, hizKernel_CopyDepth, mipsTex[0].width / 8, mipsTex[0].height/8, 1);

            }

            // 2) Build subsequent mips: for each i from 1..mipCount-1, read from mips[i-1], write to mips[i]
            for (int i = 1; i < mipCount; ++i)
            {
                RenderTexture src = mipsTex[i - 1];
                RenderTexture dst = mipsTex[i];
//
                cmd.SetComputeTextureParam(cs, hizKernel_BuildMip, "_SrcMip", src);
                cmd.SetComputeTextureParam(cs, hizKernel_BuildMip, "_DstMip", dst);
                cmd.SetComputeIntParam(cs, "SrcWidth", src.width);
                cmd.SetComputeIntParam(cs, "SrcHeight", src.height);
                cmd.DispatchCompute(cs, hizKernel_BuildMip, (dst.width + 7) / 8, (dst.height + 7) / 8, 1);
            }
//
            // 3) Expose as global textures (_HiZMip0, _HiZMip1, ..., _HiZMipN) and set mip count
            Shader.SetGlobalInt(kHiZMipCount, mipCount);
            for (int i = 0; i < mipCount; ++i)
            {
                Shader.SetGlobalTexture(kHiZNamePrefix + i, mipsTex[i]);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            CommandBufferPool.Release(cmd);
        }

        public override void FrameCleanup(CommandBuffer cmd)
        {
            //if (mipsTex != null)
            //{
            //    for (int i = 0; i < mipsTex.Length; ++i)
            //    {
            //        if (mipsTex[i] != null)
            //        {
            //            mipsTex[i].Release();
            //            mipsTex[i] = null;
            //        }
            //    }
            //    mipsTex = null;
            //}
        }
    }
}
