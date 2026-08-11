using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public sealed class SSRFrameData : ContextItem
{
    public TextureHandle result;

    public override void Reset()
    {
        result = TextureHandle.nullHandle;
    }
}

public class SSRRenderFeature : ScriptableRendererFeature
{
    class SSRPass : ScriptableRenderPass
    {
        readonly Material ssrMaterial;
        readonly ProfilingSampler m_ProfilingSampler;

        // settings
        public int maxSteps = 64;
        public float stepSize = 0.5f;

#if URP_COMPATIBILITY_MODE
        RTHandle ssrHandle;
        RTHandle ssrHistoryHandle;
#endif

        public SSRPass(Material mat)
        {
            ssrMaterial = mat;
            m_ProfilingSampler = new ProfilingSampler("Loy_SSR");

            // 确保 _CameraDepthTexture 在 RG 模式下被生成
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

#if URP_COMPATIBILITY_MODE
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            var desc = cameraTextureDescriptor;
            desc.depthBufferBits = 0;
            desc.colorFormat = RenderTextureFormat.DefaultHDR;
            RenderingUtils.ReAllocateHandleIfNeeded(ref ssrHandle, desc, FilterMode.Bilinear, name: "_SSRResultTex");
            RenderingUtils.ReAllocateHandleIfNeeded(ref ssrHistoryHandle, desc, FilterMode.Bilinear, name: "_SSRHistoryTex");

            ConfigureTarget(ssrHandle);
            ConfigureClear(ClearFlag.All, Color.clear);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (ssrMaterial == null) return;

            var cmd = CommandBufferPool.Get("Loy_SSR Compute Pass");
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                Camera cam = renderingData.cameraData.camera;
                ssrMaterial.SetVector("_WorldSpaceViewForward", cam.transform.forward);

                ssrMaterial.SetInt("_SSRMaxSteps", maxSteps);
                ssrMaterial.SetFloat("_SSRStepSize", stepSize);
                ssrMaterial.SetInt("_Frame", Time.frameCount % 1024);

                cmd.DrawProcedural(Matrix4x4.identity, ssrMaterial, 0, MeshTopology.Triangles, 3, 1);
                cmd.SetGlobalTexture(ssrHandle.name, ssrHandle.nameID);
                cmd.Blit(ssrHandle, ssrHistoryHandle);
                cmd.SetGlobalTexture(ssrHistoryHandle.name, ssrHistoryHandle.nameID);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            // 只释放当前帧结果缓冲；历史缓冲跨帧持久保留，以维持时序重投影
            ssrHandle?.Release();
        }
#endif

        class PassData
        {
            public Material material;
            public TextureHandle gbuffer2;
            public TextureHandle[] hiZMips;
            public int hiZMipCount;
            public TextureHandle activeColor;   // 当前帧延迟光照结果，SSR 反射内容来源
            public TextureHandle depth;
            public int maxSteps;
            public float stepSize;
            public int frame;
            public Vector3 viewForward;
        }

        static readonly MaterialPropertyBlock s_SharedPropertyBlock = new MaterialPropertyBlock();
        static bool s_warnedHiz;

        static readonly int[] kHiZMipIds =
        {
            Shader.PropertyToID("_HiZMip0"), Shader.PropertyToID("_HiZMip1"), Shader.PropertyToID("_HiZMip2"), Shader.PropertyToID("_HiZMip3"),
            Shader.PropertyToID("_HiZMip4"), Shader.PropertyToID("_HiZMip5"), Shader.PropertyToID("_HiZMip6"), Shader.PropertyToID("_HiZMip7")
        };

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (ssrMaterial == null || cameraData.camera == null) return;

            if (!frameData.Contains<HiZFrameData>())
            {
                if (!s_warnedHiz)
                {
                    s_warnedHiz = true;
                    Debug.LogWarning("SSR requires HizRenderFeature to run before it.");
                }
                return;
            }

            HiZFrameData hiZData = frameData.Get<HiZFrameData>();
            int hiZMipCount = Mathf.Min(hiZData.mipCount, kHiZMipIds.Length);
            if (hiZData.mips == null || hiZMipCount == 0)
                return;

            // 当前帧 SSR 结果
            TextureDesc resultDesc = renderGraph.GetTextureDesc(resourcesData.activeColorTexture);
            resultDesc.depthBufferBits = 0;
            resultDesc.format = GraphicsFormat.R16G16B16A16_SFloat;
            resultDesc.msaaSamples = MSAASamples.None;
            resultDesc.clearBuffer = true;
            resultDesc.name = "_SSRResultTex";
            TextureHandle result = renderGraph.CreateTexture(resultDesc);
            frameData.GetOrCreate<SSRFrameData>().result = result;

            // 延迟 G-Buffer 法线（RG 模式不暴露 _GBuffer2 全局，直接取资源）
            TextureHandle gbuffer2 = default;
            if (resourcesData.gBuffer != null && resourcesData.gBuffer.Length > 2)
                gbuffer2 = resourcesData.gBuffer[2];

            // SSR 计算 Pass
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_SSR Compute Pass", out var passData, m_ProfilingSampler))
            {
                passData.material = ssrMaterial;
                passData.gbuffer2 = gbuffer2;
                passData.hiZMips = hiZData.mips;
                passData.hiZMipCount = hiZMipCount;
                passData.activeColor = resourcesData.activeColorTexture;
                passData.depth = resourcesData.cameraDepthTexture;
                passData.maxSteps = maxSteps;
                passData.stepSize = stepSize;
                passData.frame = Time.frameCount % 1024;
                passData.viewForward = cameraData.camera.transform.forward;

                builder.UseTexture(resourcesData.cameraDepthTexture, AccessFlags.Read);
                // SSR 在 240（延迟光照之后）运行，此时 activeColorTexture 已是当前帧的延迟光照结果。
                // 不能 UseGlobalTexture(_CameraOpaqueTexture)：那张拷贝要到不透明/天空盒之后才注册，
                // 240 时直接 UseGlobalTexture 会抛 "null resource index"。这里改为直接读 activeColorTexture，
                // 并在 render func 里把它绑成 _CameraOpaqueTexture 给 shader 采样。
                builder.UseTexture(resourcesData.activeColorTexture, AccessFlags.Read);
                if (gbuffer2.IsValid())
                    builder.UseTexture(gbuffer2, AccessFlags.Read);
                // 显式句柄依赖：SSR 不入图时，这些读取边不存在，HiZ Build 可自动剔除。
                for (int i = 0; i < hiZMipCount; i++)
                    builder.UseTexture(hiZData.mips[i], AccessFlags.Read);

                builder.SetRenderAttachment(result, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = s_SharedPropertyBlock;
                    block.Clear();
                    block.SetVector("_WorldSpaceViewForward", data.viewForward);
                    block.SetInt("_SSRMaxSteps", data.maxSteps);
                    block.SetFloat("_SSRStepSize", data.stepSize);
                    block.SetInt("_Frame", data.frame);
                    // RG 里 compute pass 的 SetGlobalInt 跨 pass 不可靠，这里显式把 HiZ mip 数传进 block
                    block.SetInt("_HiZMipCount", data.hiZMipCount);
                    for (int i = 0; i < data.hiZMipCount; ++i)
                        block.SetTexture(kHiZMipIds[i], data.hiZMips[i]);
                    if (data.gbuffer2.IsValid())
                        block.SetTexture(Shader.PropertyToID("_GBuffer2"), data.gbuffer2);
                    block.SetTexture(Shader.PropertyToID("_CameraOpaqueTexture"), data.activeColor);
                    block.SetTexture(Shader.PropertyToID("_CameraDepthTexture"), data.depth);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, block);
                });
            }
        }
    }

    [System.Serializable]
    public class SSRSettings
    {
        public Material ssrMaterial = null;
        public RenderPassEvent passEvent = RenderPassEvent.AfterRenderingDeferredLights;
        public int maxSteps = 64;
        public float stepSize = 0.5f;

    }

    public SSRSettings settings = new SSRSettings();
    SSRPass m_SSRPass;

    public override void Create()
    {
        if (settings.ssrMaterial == null)
        {
            Debug.LogWarning("SSRFeature: ssrMaterial is null.");
            return;
        }

        m_SSRPass = new SSRPass(settings.ssrMaterial)
        {
            renderPassEvent = settings.passEvent,
            maxSteps = settings.maxSteps,
            stepSize = settings.stepSize,
        };

    }

    // Inject the pass
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_SSRPass == null) return;

        renderer.EnqueuePass(m_SSRPass);
    }
}
