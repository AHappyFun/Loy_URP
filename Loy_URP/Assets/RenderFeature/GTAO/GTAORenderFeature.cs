using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;

/// <summary>
/// GTAO resources for the current render graph frame. Consumers use the
/// texture handle directly instead of depending on a global texture slot.
/// </summary>
public sealed class GTAOFrameData : ContextItem
{
    public TextureHandle result;

    public override void Reset()
    {
        result = TextureHandle.nullHandle;
    }
}

public class GTAORenderFeature : ScriptableRendererFeature
{
    class GTAOPass : ScriptableRenderPass
    {
        readonly Material gtaoMaterial;
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
        public int NumSteps = 2;          // GTAO 每方向 2 个 tap 就够（解析积分不需要多步 raymarch）
        public float StepScale = 1.5f;
        public bool isHalfSize = true;
        public float MultiBounce = 0.6f;  // 多弹射强度 [0,1]，0=关闭

#if URP_COMPATIBILITY_MODE
        RTHandle gtaoHandle;
        RTHandle tempHanle;
#endif

        public GTAOPass(Material mat)
        {
            gtaoMaterial = mat;
            m_ProfilingSamplerGroup = new ProfilingSampler("Loy_GTAO");
            m_ProfilingSamplerCompute = new ProfilingSampler("Loy_GTAO Compute");
            m_ProfilingSamplerBlurV = new ProfilingSampler("Loy_GTAO Blur V");
            m_ProfilingSamplerBlurH = new ProfilingSampler("Loy_GTAO Blur H");
            m_ProfilingSamplerApply = new ProfilingSampler("Loy_GTAO ApplyToGI");

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

            RenderingUtils.ReAllocateHandleIfNeeded(ref gtaoHandle, desc, FilterMode.Bilinear, name: "_GTAOResultTex");
            RenderingUtils.ReAllocateHandleIfNeeded(ref tempHanle, desc, FilterMode.Bilinear, name: "_TempTex");

            ConfigureTarget(gtaoHandle);
            ConfigureClear(ClearFlag.All, Color.clear);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (gtaoMaterial == null) return;

            var cmd = CommandBufferPool.Get("Loy_GTAO Compute Pass");
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                gtaoMaterial.SetFloat("_AOIntensity", AOIntensity);
                gtaoMaterial.SetFloat("_Radius", Radius);
                gtaoMaterial.SetFloat("_Bias", Bias);
                gtaoMaterial.SetInt("_NumDirs", NumDirs);
                gtaoMaterial.SetInt("_NumSteps", NumSteps);
                gtaoMaterial.SetFloat("_StepScale", StepScale);
                gtaoMaterial.SetFloat("_AOTexRes", isHalfSize ? 0.5f : 1.0f);
                gtaoMaterial.SetFloat("_MultiBounce", MultiBounce);

                cmd.DrawProcedural(Matrix4x4.identity, gtaoMaterial, 0, MeshTopology.Triangles, 3, 1);
                cmd.Blit(gtaoHandle, tempHanle, gtaoMaterial, 1);
                cmd.Blit(tempHanle, gtaoHandle, gtaoMaterial, 2);
                cmd.SetGlobalTexture(gtaoHandle.name, gtaoHandle.nameID);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            gtaoHandle?.Release();
            tempHanle?.Release();
        }
#endif

        class PassData
        {
            public Material material;
            public TextureHandle source;   // 显式传入的模糊源（gtao 或 temp）
            public TextureHandle gbuffer2;
            public TextureHandle gbuffer0; // 反照率，仅多弹射需要
            public MaterialPropertyBlock block;   // 每个 pass 独立的参数块，避免跨 pass 共享状态
            public float aoIntensity;
            public float radius;
            public float bias;
            public int numDirs;
            public int numSteps;
            public float stepScale;
            public float aoTexRes;
            public float multiBounce;
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
            block.SetFloat("_MultiBounce", data.multiBounce);
            block.SetVector("_AOTexSize", data.aoTexSize);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            if (gtaoMaterial == null) return;

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
            desc.name = "_GTAOResultTex";

            // 显式把 AO RT 的尺寸传给 shader，避免 shader 里靠 _ScaledScreenParams 猜尺寸
            Vector4 aoTexSize = new Vector4(desc.width, desc.height, 1.0f / desc.width, 1.0f / desc.height);

            TextureHandle gtao = renderGraph.CreateTexture(desc);

            GTAOFrameData gtaoData = frameData.Create<GTAOFrameData>();
            gtaoData.result = gtao;

            desc.name = "_GTAOTemp"; // 中间缓冲用独立名字，避免和 _GTAOResultTex 混淆
            TextureHandle temp = renderGraph.CreateTexture(desc);

            // 延迟 G-Buffer：法线（RG 模式不暴露全局，直接取资源）
            TextureHandle gbuffer2 = default;
            if (resourcesData.gBuffer != null && resourcesData.gBuffer.Length > 2)
                gbuffer2 = resourcesData.gBuffer[2];
            // 反照率（仅多弹射需要）
            TextureHandle gbuffer0 = default;
            if (resourcesData.gBuffer != null && resourcesData.gBuffer.Length > 0)
                gbuffer0 = resourcesData.gBuffer[0];

            // 记录阶段（主线程）直接设置材质参数，确保可靠到达 shader
            gtaoMaterial.SetFloat("_AOIntensity", AOIntensity);
            gtaoMaterial.SetFloat("_Radius", Radius);
            gtaoMaterial.SetFloat("_Bias", Bias);
            gtaoMaterial.SetInt("_NumDirs", NumDirs);
            gtaoMaterial.SetInt("_NumSteps", NumSteps);
            gtaoMaterial.SetFloat("_StepScale", StepScale);
            gtaoMaterial.SetFloat("_AOTexRes", isHalfSize ? 0.5f : 1.0f);
            gtaoMaterial.SetFloat("_MultiBounce", MultiBounce);

            renderGraph.BeginProfilingSampler(m_ProfilingSamplerGroup);

            // Pass 0: 计算 AO → gtao
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_GTAO Compute", out var computeData, m_ProfilingSamplerCompute))
            {
                computeData.material = gtaoMaterial;
                computeData.aoIntensity = AOIntensity;
                computeData.radius = Radius;
                computeData.bias = Bias;
                computeData.numDirs = NumDirs;
                computeData.numSteps = NumSteps;
                computeData.stepScale = StepScale;
                computeData.aoTexRes = isHalfSize ? 0.5f : 1.0f;
                computeData.aoTexSize = aoTexSize;
                computeData.multiBounce = MultiBounce;
                computeData.gbuffer2 = gbuffer2;
                computeData.gbuffer0 = gbuffer0;
                computeData.block = new MaterialPropertyBlock();

                // shader 用 _CameraDepthTexture 直接采样深度
                builder.UseGlobalTexture(Shader.PropertyToID("_CameraDepthTexture"), AccessFlags.Read);
                if (gbuffer2.IsValid())
                {
                    builder.UseTexture(gbuffer2, AccessFlags.Read);
                    builder.AllowGlobalStateModification(true);
                }
                if (gbuffer0.IsValid())
                {
                    builder.UseTexture(gbuffer0, AccessFlags.Read);
                    builder.AllowGlobalStateModification(true);
                }

                builder.SetRenderAttachment(gtao, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    SetComputeParams(data);
                    if (data.gbuffer2.IsValid())
                        rgContext.cmd.SetGlobalTexture(Shader.PropertyToID("_GBuffer2"), data.gbuffer2);
                    if (data.gbuffer0.IsValid())
                        rgContext.cmd.SetGlobalTexture(Shader.PropertyToID("_GBuffer0"), data.gbuffer0);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, data.block);
                });
            }

            // Pass 1: 垂直模糊 → temp（显式传入 gtao）
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_GTAO Blur V", out var blurVData, m_ProfilingSamplerBlurV))
            {
                blurVData.material = gtaoMaterial;
                blurVData.source = gtao;
                blurVData.aoTexRes = isHalfSize ? 0.5f : 1.0f;
                blurVData.aoTexSize = aoTexSize;
                blurVData.block = new MaterialPropertyBlock();

                // 显式声明读取 gtao（不靠全局，生命周期精确到本 pass）
                builder.UseTexture(gtao, AccessFlags.Read);
                builder.UseGlobalTexture(Shader.PropertyToID("_CameraDepthTexture"), AccessFlags.Read);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderAttachment(temp, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetFloat("_AOTexRes", data.aoTexRes);
                    block.SetVector("_AOTexSize", data.aoTexSize);
                    rgContext.cmd.SetGlobalTexture(Shader.PropertyToID("_GTAOBlurSource"), data.source);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 1, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // Pass 2: 水平模糊 → gtao；结果通过 GTAOFrameData 传给后续消费者。
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_GTAO Blur H", out var blurHData, m_ProfilingSamplerBlurH))
            {
                blurHData.material = gtaoMaterial;
                blurHData.source = temp;
                blurHData.aoTexRes = isHalfSize ? 0.5f : 1.0f;
                blurHData.aoTexSize = aoTexSize;
                blurHData.block = new MaterialPropertyBlock();

                builder.UseTexture(temp, AccessFlags.Read);
                builder.UseGlobalTexture(Shader.PropertyToID("_CameraDepthTexture"), AccessFlags.Read);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderAttachment(gtao, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetFloat("_AOTexRes", data.aoTexRes);
                    block.SetVector("_AOTexSize", data.aoTexSize);
                    rgContext.cmd.SetGlobalTexture(Shader.PropertyToID("_GTAOBlurSource"), data.source);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 2, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // Pass 3: 把 AO 乘到 GBuffer3（GI+自发光）上。
            // 时机在延迟光照之前：延迟光照是 Blend One One 加性叠加直接光，
            // 所以这里先乘 AO 只压间接光，直接光不受影响。
            if (applyToGI)
            {
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_GTAO ApplyToGI", out var applyData, m_ProfilingSamplerApply))
                {
                    applyData.material = gtaoMaterial;
                    applyData.source = gtao;
                    applyData.block = new MaterialPropertyBlock();

                    builder.UseTexture(gtao, AccessFlags.Read);
                    builder.AllowGlobalStateModification(true);

                    // 关键：在 URP 延迟渲染里，GBuffer3（lighting buffer）不是一个独立纹理，
                    // 它直接就是相机的颜色缓冲 activeColorTexture。
                    // 所以这里 SetRenderAttachment(activeColorTexture) = 写 GBuffer3（GBuffer pass 存入的 GI+自发光）。
                    builder.SetRenderAttachment(resourcesData.activeColorTexture, 0, AccessFlags.Write);

                    builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                    {
                        rgContext.cmd.SetGlobalTexture(Shader.PropertyToID("_GTAOResultTex"), data.source);
                        rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 3, MeshTopology.Triangles, 3, 1, data.block);
                    });
                }
            }

            renderGraph.EndProfilingSampler(m_ProfilingSamplerGroup);
        }
    }

    [System.Serializable]
    public class GTAOSettings
    {
        public Material gtaoMaterial = null;
        public RenderPassEvent passEvent = RenderPassEvent.AfterRenderingGbuffer;
        public float AOIntensity = 1.0f;
        public float Radius = 1.0f;
        public float Bias = 0.02f;
        public int NumDirs = 8;
        public int NumSteps = 2;
        public float StepScale = 1.5f;
        public bool isHalfSize = true;
        public bool applyToGI = true;   // 把 AO 乘进 GBuffer3（GI+自发光），延迟光照前生效

        [Range(0f, 1f)]
        public float MultiBounce = 0.6f; // 多弹射强度：0=关闭，>0 时按反照率把遮挡暗部提亮
    }

    public GTAOSettings settings = new GTAOSettings();
    GTAOPass m_GTAOPass;

    public override void Create()
    {
        if (settings.gtaoMaterial == null)
        {
            Debug.LogWarning("GTAOFeature: gtaoMaterial is null.");
            return;
        }

        m_GTAOPass = new GTAOPass(settings.gtaoMaterial)
        {
            renderPassEvent = settings.passEvent,
            AOIntensity = settings.AOIntensity,
            Radius = settings.Radius,
            Bias = settings.Bias,
            NumDirs = settings.NumDirs,
            NumSteps = settings.NumSteps,
            StepScale = settings.StepScale,
            isHalfSize = settings.isHalfSize,
            applyToGI = settings.applyToGI,
            MultiBounce = settings.MultiBounce
        };
    }

    // Inject the pass
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_GTAOPass == null) return;

        // shader 用 SampleSceneDepth 读取 _CameraDepthTexture
        renderingData.cameraData.requiresDepthTexture = true;

        renderer.EnqueuePass(m_GTAOPass);
    }
}
