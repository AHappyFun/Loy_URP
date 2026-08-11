using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;

public sealed class SSGIFrameData : ContextItem
{
    public TextureHandle result;

    public override void Reset()
    {
        result = TextureHandle.nullHandle;
    }
}

public class SSGIRenderFeature : ScriptableRendererFeature
{
    class SSGIPass : ScriptableRenderPass
    {
        readonly Material ssgiMaterial;
        readonly ProfilingSampler m_ProfilingSampler;
        readonly ProfilingSampler m_ProfilingSamplerCompute;
        readonly ProfilingSampler m_ProfilingSamplerBlurV;
        readonly ProfilingSampler m_ProfilingSamplerBlurH;

        public int NumDir = 8;
        public float MaxRayDistance = 200;
        public int NumSteps = 30;

        public bool isHalfSize = true;
        public float DepthBias = 0.1f;
        public float Thickness = 0.2f;

#if URP_COMPATIBILITY_MODE
        RTHandle ssgiHandle;
        RTHandle tempHanle;
#endif

        public SSGIPass(Material mat)
        {
            ssgiMaterial = mat;
            m_ProfilingSampler = new ProfilingSampler("Loy_SSGI");
            m_ProfilingSamplerCompute = new ProfilingSampler("Loy_SSGI Compute");
            m_ProfilingSamplerBlurV = new ProfilingSampler("Loy_SSGI Blur V");
            m_ProfilingSamplerBlurH = new ProfilingSampler("Loy_SSGI Blur H");

            // 确保 _CameraDepthTexture 在 RG 模式下被生成（深度拷贝 pass）
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

#if URP_COMPATIBILITY_MODE
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            var desc = cameraTextureDescriptor;

            float scale = isHalfSize ? 0.5f : 1.0f;

            desc.width = (int)(desc.width * scale);
            desc.height = (int)(desc.height * scale);
            desc.depthBufferBits = 0;

            RenderingUtils.ReAllocateHandleIfNeeded(ref ssgiHandle, desc, FilterMode.Bilinear, name: "_SSGIResultTex");
            RenderingUtils.ReAllocateHandleIfNeeded(ref tempHanle, desc, FilterMode.Bilinear, name: "_TempTex");

            ConfigureTarget(ssgiHandle);
            ConfigureClear(ClearFlag.All, Color.clear);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (ssgiMaterial == null) return;

            var cmd = CommandBufferPool.Get("Loy_SSGI Compute Pass");
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                ssgiMaterial.SetFloat("_NumDirs", NumDir);
                ssgiMaterial.SetFloat("_MaxRayDistance", MaxRayDistance);
                ssgiMaterial.SetInt("_NumSteps", NumSteps);
                ssgiMaterial.SetFloat("_DepthBias", DepthBias);
                ssgiMaterial.SetFloat("_Thickness", Thickness);
                ssgiMaterial.SetFloat("_GITexRes", isHalfSize ? 0.5f : 1.0f);

                cmd.DrawProcedural(Matrix4x4.identity, ssgiMaterial, 0, MeshTopology.Triangles, 3, 1);
                cmd.Blit(ssgiHandle, tempHanle, ssgiMaterial, 1);
                cmd.Blit(tempHanle, ssgiHandle, ssgiMaterial, 2);
                cmd.SetGlobalTexture(ssgiHandle.name, ssgiHandle.nameID);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            ssgiHandle?.Release();
            tempHanle?.Release();
        }
#endif

        class PassData
        {
            public Material material;
            public TextureHandle source;   // 显式传入的模糊源（ssgi 或 temp）
            public TextureHandle activeColor;
            public TextureHandle gbuffer2;
            public MaterialPropertyBlock block;   // 每个 pass 独立的参数块，避免跨 pass 共享状态
            public int numDirs;
            public float maxRayDistance;
            public int numSteps;
            public float depthBias;
            public float thickness;
            public float giTexRes;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            if (ssgiMaterial == null) return;

            float scale = isHalfSize ? 0.5f : 1.0f;

            TextureDesc desc = renderGraph.GetTextureDesc(resourcesData.activeColorTexture);
            desc.width = Mathf.Max(1, (int)(desc.width * scale));
            desc.height = Mathf.Max(1, (int)(desc.height * scale));
            desc.depthBufferBits = 0;
            desc.msaaSamples = MSAASamples.None;
            desc.clearBuffer = true;
            desc.name = "_SSGIResultTex";

            TextureHandle ssgi = renderGraph.CreateTexture(desc);
            frameData.GetOrCreate<SSGIFrameData>().result = ssgi;

            desc.name = "_SSGITemp"; // 中间缓冲用独立名字，避免和 _SSGIResultTex 混淆
            TextureHandle temp = renderGraph.CreateTexture(desc);

            // 延迟 G-Buffer 法线（RG 模式不暴露 _GBuffer2 全局，直接取资源）
            TextureHandle gbuffer2 = default;
            if (resourcesData.gBuffer != null && resourcesData.gBuffer.Length > 2)
                gbuffer2 = resourcesData.gBuffer[2];

            // 记录阶段（主线程）直接设置材质参数，确保可靠到达 shader
            ssgiMaterial.SetInt("_NumDirs", NumDir);
            ssgiMaterial.SetFloat("_MaxRayDistance", MaxRayDistance);
            ssgiMaterial.SetInt("_NumSteps", NumSteps);
            ssgiMaterial.SetFloat("_DepthBias", DepthBias);
            ssgiMaterial.SetFloat("_Thickness", Thickness);
            ssgiMaterial.SetFloat("_GITexRes", isHalfSize ? 0.5f : 1.0f);

            // Pass 0: 计算 SSGI → ssgi
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_SSGI Compute", out var computeData, m_ProfilingSamplerCompute))
            {
                computeData.material = ssgiMaterial;
                computeData.numDirs = NumDir;
                computeData.maxRayDistance = MaxRayDistance;
                computeData.numSteps = NumSteps;
                computeData.depthBias = DepthBias;
                computeData.thickness = Thickness;
                computeData.giTexRes = isHalfSize ? 0.5f : 1.0f;
                computeData.activeColor = resourcesData.activeColorTexture;
                computeData.gbuffer2 = gbuffer2;
                computeData.block = new MaterialPropertyBlock();

                // shader 用 _CameraDepthTexture 直接采样深度
                builder.UseGlobalTexture(Shader.PropertyToID("_CameraDepthTexture"), AccessFlags.Read);
                if (gbuffer2.IsValid())
                    builder.UseTexture(gbuffer2, AccessFlags.Read);
                builder.UseTexture(resourcesData.activeColorTexture, AccessFlags.Read);

                builder.SetRenderAttachment(ssgi, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetInt("_NumDirs", data.numDirs);
                    block.SetFloat("_MaxRayDistance", data.maxRayDistance);
                    block.SetInt("_NumSteps", data.numSteps);
                    block.SetFloat("_DepthBias", data.depthBias);
                    block.SetFloat("_Thickness", data.thickness);
                    block.SetFloat("_GITexRes", data.giTexRes);
                    block.SetTexture(Shader.PropertyToID("_CameraOpaqueTexture"), data.activeColor);
                    if (data.gbuffer2.IsValid())
                        block.SetTexture(Shader.PropertyToID("_GBuffer2"), data.gbuffer2);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // Pass 1: 垂直模糊 → temp（显式传入 ssgi）
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_SSGI Blur V", out var blurVData, m_ProfilingSamplerBlurV))
            {
                blurVData.material = ssgiMaterial;
                blurVData.source = ssgi;
                blurVData.giTexRes = isHalfSize ? 0.5f : 1.0f;
                blurVData.block = new MaterialPropertyBlock();

                // 显式声明读取 ssgi（不靠全局，生命周期精确到本 pass）
                builder.UseTexture(ssgi, AccessFlags.Read);

                builder.SetRenderAttachment(temp, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetFloat("_GITexRes", data.giTexRes);
                    block.SetTexture(Shader.PropertyToID("_SSGIBlurSource"), data.source);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 1, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // Pass 2: 水平模糊 → ssgi；SSGICombine 通过 SSGIFrameData 显式读取最终结果。
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_SSGI Blur H", out var blurHData, m_ProfilingSamplerBlurH))
            {
                blurHData.material = ssgiMaterial;
                blurHData.source = temp;
                blurHData.giTexRes = isHalfSize ? 0.5f : 1.0f;
                blurHData.block = new MaterialPropertyBlock();

                builder.UseTexture(temp, AccessFlags.Read);

                builder.SetRenderAttachment(ssgi, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetFloat("_GITexRes", data.giTexRes);
                    block.SetTexture(Shader.PropertyToID("_SSGIBlurSource"), data.source);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 2, MeshTopology.Triangles, 3, 1, block);
                });
            }
        }
    }

    [System.Serializable]
    public class SSGISettngs
    {
        public Material ssgiMaterial = null;
        public RenderPassEvent passEvent = RenderPassEvent.AfterRenderingDeferredLights;
        [Range(4, 16)]
        public int NumDir = 8;

        public float MaxRayDistance = 200;

        public int NumSteps = 30;


        public bool isHalfSize = true;

        public float DepthBias = 0.1f;
        [Min(0.001f)]
        public float Thickness = 0.2f;
    }

    public SSGISettngs settings = new SSGISettngs();
    SSGIPass m_ssgiPass;

    public override void Create()
    {
        if (settings.ssgiMaterial == null)
        {
            Debug.LogWarning("SSGIFeature: ssgiMaterial is null.");
            return;
        }

        RenderPassEvent safePassEvent = settings.passEvent < RenderPassEvent.AfterRenderingDeferredLights
            ? RenderPassEvent.AfterRenderingDeferredLights
            : settings.passEvent;

        m_ssgiPass = new SSGIPass(settings.ssgiMaterial)
        {
            renderPassEvent = safePassEvent,
            NumDir = settings.NumDir,
            MaxRayDistance = settings.MaxRayDistance,
            NumSteps = settings.NumSteps,
            isHalfSize = settings.isHalfSize,
            DepthBias = settings.DepthBias,
            Thickness = settings.Thickness

        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_ssgiPass == null) return;

        renderingData.cameraData.requiresDepthTexture = true;

        renderer.EnqueuePass(m_ssgiPass);
    }
}
