using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;

/// <summary>
/// HBAO resources for the current render graph frame. Consumers use the
/// texture handle directly instead of depending on a global texture slot.
/// </summary>
public sealed class HBAOFrameData : ContextItem
{
    public TextureHandle result;

    public override void Reset()
    {
        result = TextureHandle.nullHandle;
    }
}

public class HBAORenderFeature : ScriptableRendererFeature
{
    class HBAOPass : ScriptableRenderPass
    {
        readonly Material hbaoMaterial;
        readonly ProfilingSampler m_ProfilingSamplerCompute;
        readonly ProfilingSampler m_ProfilingSamplerBlurV;
        readonly ProfilingSampler m_ProfilingSamplerBlurH;
        readonly ProfilingSampler m_ProfilingSamplerApply;
        readonly ProfilingSampler m_ProfilingSamplerGroup;

        public float AOIntensity = 1.0f;
        public bool applyToGI = true;
        public float Radius = 1.0f;
        public float Bias = 0.02f;
        public int NumDirs = 8;
        public int NumSteps = 12;
        public float StepScale = 1.4f;
        public bool isHalfSize = true;

#if URP_COMPATIBILITY_MODE
        RTHandle hbaoHandle;
        RTHandle tempHanle;
#endif

        public HBAOPass(Material mat)
        {
            hbaoMaterial = mat;
            m_ProfilingSamplerGroup = new ProfilingSampler("Loy_HBAO");
            m_ProfilingSamplerCompute = new ProfilingSampler("Loy_HBAO Compute");
            m_ProfilingSamplerBlurV = new ProfilingSampler("Loy_HBAO Blur V");
            m_ProfilingSamplerBlurH = new ProfilingSampler("Loy_HBAO Blur H");
            m_ProfilingSamplerApply = new ProfilingSampler("Loy_HBAO ApplyToGI");

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
            desc.colorFormat = RenderTextureFormat.R8;

            RenderingUtils.ReAllocateHandleIfNeeded(ref hbaoHandle, desc, FilterMode.Bilinear, name: "_HBAOResultTex");
            RenderingUtils.ReAllocateHandleIfNeeded(ref tempHanle, desc, FilterMode.Bilinear, name: "_TempTex");

            ConfigureTarget(hbaoHandle);
            ConfigureClear(ClearFlag.All, Color.clear);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (hbaoMaterial == null) return;

            var cmd = CommandBufferPool.Get("Loy_HBAO Compute Pass");
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                hbaoMaterial.SetFloat("_AOIntensity", AOIntensity);
                hbaoMaterial.SetFloat("_Radius", Radius);
                hbaoMaterial.SetFloat("_Bias", Bias);
                hbaoMaterial.SetInt("_NumDirs", NumDirs);
                hbaoMaterial.SetInt("_NumSteps", NumSteps);
                hbaoMaterial.SetFloat("_StepScale", StepScale);
                hbaoMaterial.SetFloat("_AOTexRes", isHalfSize ? 0.5f : 1.0f);

                cmd.DrawProcedural(Matrix4x4.identity, hbaoMaterial, 0, MeshTopology.Triangles, 3, 1);
                cmd.Blit(hbaoHandle, tempHanle, hbaoMaterial, 1);
                cmd.Blit(tempHanle, hbaoHandle, hbaoMaterial, 2);
                cmd.SetGlobalTexture(hbaoHandle.name, hbaoHandle.nameID);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            hbaoHandle?.Release();
            tempHanle?.Release();
        }
#endif

        class PassData
        {
            public Material material;
            public TextureHandle source;   // 显式传入的模糊源（hbao 或 temp）
            public TextureHandle gbuffer2;
            public MaterialPropertyBlock block;   // 每个 pass 独立的参数块，避免跨 pass 共享状态
            public float aoIntensity;
            public float radius;
            public float bias;
            public int numDirs;
            public int numSteps;
            public float stepScale;
            public float aoTexRes;
            public Vector4 aoTexSize;   // 实际 AO RT 尺寸：x=宽, y=高, z=1/宽, w=1/高
        }

        static void SetComputeParams(PassData data)
        {
            MaterialPropertyBlock block = data.block;
            block.Clear();
            block.SetFloat("_AOIntensity", data.aoIntensity);
            block.SetFloat("_Radius", data.radius);
            block.SetFloat("_Bias", data.bias);
            block.SetInt("_NumDirs", data.numDirs);
            block.SetInt("_NumSteps", data.numSteps);
            block.SetFloat("_StepScale", data.stepScale);
            block.SetFloat("_AOTexRes", data.aoTexRes);
            block.SetVector("_AOTexSize", data.aoTexSize);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            if (hbaoMaterial == null) return;

            float scale = isHalfSize ? 0.5f : 1.0f;

            TextureDesc desc = renderGraph.GetTextureDesc(resourcesData.activeColorTexture);
            desc.width = Mathf.Max(1, (int)(desc.width * scale));
            desc.height = Mathf.Max(1, (int)(desc.height * scale));
            desc.format = GraphicsFormat.R8_UNorm;
            desc.dimension = TextureDimension.Tex2D;
            desc.depthBufferBits = 0;
            desc.msaaSamples = MSAASamples.None;
            desc.clearBuffer = true;
            desc.clearColor = Color.white;
            desc.name = "_HBAOResultTex";

            // 显式把 AO RT 的尺寸传给 shader，避免 shader 里靠 _ScaledScreenParams 猜尺寸
            Vector4 aoTexSize = new Vector4(desc.width, desc.height, 1.0f / desc.width, 1.0f / desc.height);

            TextureHandle hbao = renderGraph.CreateTexture(desc);

            HBAOFrameData hbaoData = frameData.Create<HBAOFrameData>();
            hbaoData.result = hbao;

            desc.name = "_HBAOTemp"; // 中间缓冲用独立名字，避免和 _HBAOResultTex 混淆
            TextureHandle temp = renderGraph.CreateTexture(desc);

            // 延迟 G-Buffer 法线（RG 模式不暴露 _GBuffer2 全局，直接取资源）
            TextureHandle gbuffer2 = default;
            if (resourcesData.gBuffer != null && resourcesData.gBuffer.Length > 2)
                gbuffer2 = resourcesData.gBuffer[2];

            // 记录阶段（主线程）直接设置材质参数，确保可靠到达 shader
            hbaoMaterial.SetFloat("_AOIntensity", AOIntensity);
            hbaoMaterial.SetFloat("_Radius", Radius);
            hbaoMaterial.SetFloat("_Bias", Bias);
            hbaoMaterial.SetInt("_NumDirs", NumDirs);
            hbaoMaterial.SetInt("_NumSteps", NumSteps);
            hbaoMaterial.SetFloat("_StepScale", StepScale);
            hbaoMaterial.SetFloat("_AOTexRes", isHalfSize ? 0.5f : 1.0f);

            renderGraph.BeginProfilingSampler(m_ProfilingSamplerGroup);

            // Pass 0: 计算 AO → hbao
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_HBAO Compute", out var computeData, m_ProfilingSamplerCompute))
            {
                computeData.material = hbaoMaterial;
                computeData.aoIntensity = AOIntensity;
                computeData.radius = Radius;
                computeData.bias = Bias;
                computeData.numDirs = NumDirs;
                computeData.numSteps = NumSteps;
                computeData.stepScale = StepScale;
                computeData.aoTexRes = isHalfSize ? 0.5f : 1.0f;
                computeData.aoTexSize = aoTexSize;
                computeData.gbuffer2 = gbuffer2;
                computeData.block = new MaterialPropertyBlock();

                // shader 用 _CameraDepthTexture 直接采样深度
                builder.UseGlobalTexture(Shader.PropertyToID("_CameraDepthTexture"), AccessFlags.Read);
                if (gbuffer2.IsValid())
                {
                    builder.UseTexture(gbuffer2, AccessFlags.Read);
                    builder.AllowGlobalStateModification(true);
                }

                builder.SetRenderAttachment(hbao, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    SetComputeParams(data);
                    if (data.gbuffer2.IsValid())
                        rgContext.cmd.SetGlobalTexture(Shader.PropertyToID("_GBuffer2"), data.gbuffer2);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, data.block);
                });
            }

            // Pass 1: 垂直模糊 → temp（显式传入 hbao）
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_HBAO Blur V", out var blurVData, m_ProfilingSamplerBlurV))
            {
                blurVData.material = hbaoMaterial;
                blurVData.source = hbao;
                blurVData.aoTexRes = isHalfSize ? 0.5f : 1.0f;
                blurVData.aoTexSize = aoTexSize;
                blurVData.block = new MaterialPropertyBlock();

                // 显式声明读取 hbao（不靠全局，生命周期精确到本 pass）
                builder.UseTexture(hbao, AccessFlags.Read);
                builder.UseGlobalTexture(Shader.PropertyToID("_CameraDepthTexture"), AccessFlags.Read);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderAttachment(temp, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetFloat("_AOTexRes", data.aoTexRes);
                    block.SetVector("_AOTexSize", data.aoTexSize);
                    rgContext.cmd.SetGlobalTexture(Shader.PropertyToID("_HBAOBlurSource"), data.source);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 1, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // Pass 2: 水平模糊 → hbao；结果通过 HBAOFrameData 传给后续消费者。
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_HBAO Blur H", out var blurHData, m_ProfilingSamplerBlurH))
            {
                blurHData.material = hbaoMaterial;
                blurHData.source = temp;
                blurHData.aoTexRes = isHalfSize ? 0.5f : 1.0f;
                blurHData.aoTexSize = aoTexSize;
                blurHData.block = new MaterialPropertyBlock();

                builder.UseTexture(temp, AccessFlags.Read);
                builder.UseGlobalTexture(Shader.PropertyToID("_CameraDepthTexture"), AccessFlags.Read);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderAttachment(hbao, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetFloat("_AOTexRes", data.aoTexRes);
                    block.SetVector("_AOTexSize", data.aoTexSize);
                    rgContext.cmd.SetGlobalTexture(Shader.PropertyToID("_HBAOBlurSource"), data.source);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 2, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // Pass 3: 把 AO 乘到 GBuffer3（GI+自发光）上。
            // 时机在延迟光照(230)之前：延迟光照是 Blend One One 加性叠加直接光，
            // 所以这里先乘 AO 只压间接光，直接光不受影响。
            if (applyToGI)
            {
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_HBAO ApplyToGI", out var applyData, m_ProfilingSamplerApply))
                {
                    applyData.material = hbaoMaterial;
                    applyData.source = hbao;
                    applyData.block = new MaterialPropertyBlock();

                    builder.UseTexture(hbao, AccessFlags.Read);
                    builder.AllowGlobalStateModification(true);

                    // 关键：在 URP 延迟渲染里，GBuffer3（lighting buffer）不是一个独立纹理，
                    // 它直接就是相机的颜色缓冲 activeColorTexture。证据链：
                    //   DeferredLights.cs:557              GbufferAttachments[GBufferLightingIndex=3] = colorAttachment
                    //   UniversalRendererRenderGraph.cs:1193  传给 DeferredPass 的 color = resourceData.activeColorTexture
                    //   DeferredPass.cs:83                SetRenderAttachment(color)  ← 延迟光照也写这张
                    //   GBufferPass.cs:92-93              GBuffer pass 写 MRT 时跳过它（它已 = 相机颜色）
                    // 所以这里 SetRenderAttachment(activeColorTexture) = 写 GBuffer3（GBuffer pass 存入的 GI+自发光）。
                    builder.SetRenderAttachment(resourcesData.activeColorTexture, 0, AccessFlags.Write);

                    builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                    {
                        rgContext.cmd.SetGlobalTexture(Shader.PropertyToID("_HBAOResultTex"), data.source);
                        rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 3, MeshTopology.Triangles, 3, 1, data.block);
                    });
                }
            }

            renderGraph.EndProfilingSampler(m_ProfilingSamplerGroup);
        }
    }

    [System.Serializable]
    public class HBAOSettings
    {
        public Material hbaoMaterial = null;
        public RenderPassEvent passEvent = RenderPassEvent.AfterRenderingGbuffer;
        public float AOIntensity = 1.0f;
        public float Radius = 1.0f;
        public float Bias = 0.02f;
        public int NumDirs = 8;
        public int NumSteps = 12;
        public float StepScale = 1.4f;
        public bool isHalfSize = true;
        public bool applyToGI = true;   // 把 AO 乘进 GBuffer3（GI+自发光），延迟光照前生效

    }

    public HBAOSettings settings = new HBAOSettings();
    HBAOPass m_HBAOPass;

    public override void Create()
    {
        if (settings.hbaoMaterial == null)
        {
            Debug.LogWarning("HBAOFeature: ssrMaterial is null.");
            return;
        }

        m_HBAOPass = new HBAOPass(settings.hbaoMaterial)
        {
            renderPassEvent = settings.passEvent,
            AOIntensity = settings.AOIntensity,
            Radius = settings.Radius,
            Bias = settings.Bias,
            NumDirs = settings.NumDirs,
            NumSteps = settings.NumSteps,
            StepScale = settings.StepScale,
            isHalfSize = settings.isHalfSize,
            applyToGI = settings.applyToGI
        };
    }

    // Inject the pass
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_HBAOPass == null) return;

        // shader 用 SampleSceneDepth 读取 _CameraDepthTexture
        renderingData.cameraData.requiresDepthTexture = true;

        renderer.EnqueuePass(m_HBAOPass);
    }
}
