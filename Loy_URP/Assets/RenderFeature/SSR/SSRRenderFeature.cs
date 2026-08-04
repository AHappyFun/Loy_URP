using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

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
#endif
        // 时序历史缓冲（跨帧持久）
        RTHandle ssrHistoryHandle;

        public SSRPass(Material mat)
        {
            ssrMaterial = mat;
            m_ProfilingSampler = new ProfilingSampler("Loy_SSR");
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
                Matrix4x4 proj = renderingData.cameraData.GetGPUProjectionMatrix();
                Matrix4x4 invProj = proj.inverse;
                Matrix4x4 view = renderingData.cameraData.GetViewMatrix();
                Matrix4x4 invView = view.inverse;

                ssrMaterial.SetMatrix("_CameraProjection", proj);
                ssrMaterial.SetMatrix("_CameraInvProjection", invProj);
                ssrMaterial.SetMatrix("_CameraView", view);
                ssrMaterial.SetMatrix("_CameraInvView", invView);
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
            public int maxSteps;
            public float stepSize;
            public int frame;
            public Matrix4x4 projection;
            public Matrix4x4 invProjection;
            public Matrix4x4 view;
            public Matrix4x4 invView;
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

            // 历史缓冲：持久 RTHandle，跨帧导入 RG，保证时序重投影内容连续
            RenderTextureDescriptor histDesc = cameraData.cameraTargetDescriptor;
            histDesc.depthBufferBits = 0;
            histDesc.colorFormat = RenderTextureFormat.DefaultHDR;
            RenderingUtils.ReAllocateHandleIfNeeded(ref ssrHistoryHandle, histDesc, FilterMode.Bilinear, name: "_SSRHistoryTex");
            TextureHandle history = renderGraph.ImportTexture(ssrHistoryHandle);

            // 当前帧 SSR 结果
            TextureDesc resultDesc = renderGraph.GetTextureDesc(resourcesData.activeColorTexture);
            resultDesc.depthBufferBits = 0;
            resultDesc.format = GraphicsFormat.R16G16B16A16_SFloat;
            resultDesc.msaaSamples = MSAASamples.None;
            resultDesc.clearBuffer = true;
            resultDesc.name = "_SSRResultTex";
            TextureHandle result = renderGraph.CreateTexture(resultDesc);

            // 延迟 G-Buffer 法线（RG 模式不暴露 _GBuffer2 全局，直接取资源）
            TextureHandle gbuffer2 = default;
            if (resourcesData.gBuffer != null && resourcesData.gBuffer.Length > 2)
                gbuffer2 = resourcesData.gBuffer[2];

            // SSR 计算 Pass
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_SSR Compute Pass", out var passData, m_ProfilingSampler))
            {
                passData.material = ssrMaterial;
                passData.gbuffer2 = gbuffer2;
                passData.maxSteps = maxSteps;
                passData.stepSize = stepSize;
                passData.frame = Time.frameCount % 1024;
                passData.projection = cameraData.GetGPUProjectionMatrix();
                passData.invProjection = passData.projection.inverse;
                passData.view = cameraData.GetViewMatrix();
                passData.invView = passData.view.inverse;
                passData.viewForward = cameraData.camera.transform.forward;

                builder.UseGlobalTexture(Shader.PropertyToID("_CameraDepthTexture"), AccessFlags.Read);
                builder.UseGlobalTexture(Shader.PropertyToID("_CameraOpaqueTexture"), AccessFlags.Read);
                if (gbuffer2.IsValid())
                {
                    builder.UseTexture(gbuffer2, AccessFlags.Read);
                    builder.AllowGlobalStateModification(true);
                }
                if (HizRenderFeature.IsActive)
                {
                    for (int i = 0; i < kHiZMipIds.Length; i++)
                        builder.UseGlobalTexture(kHiZMipIds[i], AccessFlags.Read);
                }
                else if (!s_warnedHiz)
                {
                    s_warnedHiz = true;
                    Debug.LogWarning("SSR 需要启用 HizRenderFeature（shader 使用 SampleHIZ），否则 _HiZMip* 不存在，SSR 将不可用。");
                }

                builder.SetRenderAttachment(result, 0, AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(result, Shader.PropertyToID("_SSRResultTex"));

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = s_SharedPropertyBlock;
                    block.Clear();
                    block.SetMatrix("_CameraProjection", data.projection);
                    block.SetMatrix("_CameraInvProjection", data.invProjection);
                    block.SetMatrix("_CameraView", data.view);
                    block.SetMatrix("_CameraInvView", data.invView);
                    block.SetVector("_WorldSpaceViewForward", data.viewForward);
                    block.SetInt("_SSRMaxSteps", data.maxSteps);
                    block.SetFloat("_SSRStepSize", data.stepSize);
                    block.SetInt("_Frame", data.frame);
                    if (data.gbuffer2.IsValid())
                        rgContext.cmd.SetGlobalTexture(Shader.PropertyToID("_GBuffer2"), data.gbuffer2);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // 拷贝 result → history，并暴露 _SSRHistoryTex 全局给 Combine Pass
            using (var builder = renderGraph.AddBlitPass(result, history, Vector2.one, Vector2.zero, returnBuilder: true, passName: "SSR Copy History"))
            {
                builder.SetGlobalTextureAfterPass(history, Shader.PropertyToID("_SSRHistoryTex"));
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

        // shader 用 SampleSceneColor 读取 _CameraOpaqueTexture
        renderingData.cameraData.requiresOpaqueTexture = true;

        renderer.EnqueuePass(m_SSRPass);
    }
}
