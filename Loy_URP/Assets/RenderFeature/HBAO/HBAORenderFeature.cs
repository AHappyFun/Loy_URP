using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;

public class HBAORenderFeature : ScriptableRendererFeature
{
    class HBAOPass : ScriptableRenderPass
    {
        readonly Material hbaoMaterial;
        readonly ProfilingSampler m_ProfilingSampler;

        public float AOIntensity = 1.0f;
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
            m_ProfilingSampler = new ProfilingSampler("Loy_HBAO");
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
            public TextureHandle source;
            public TextureHandle gbuffer2;
            public float aoIntensity;
            public float radius;
            public float bias;
            public int numDirs;
            public int numSteps;
            public float stepScale;
            public float aoTexRes;
        }

        static readonly MaterialPropertyBlock s_SharedPropertyBlock = new MaterialPropertyBlock();
        static bool s_warnedHiz;

        // shader 通过 SampleHIZ 读取的 mip 全局
        static readonly int[] kHiZMipIds =
        {
            Shader.PropertyToID("_HiZMip0"), Shader.PropertyToID("_HiZMip1"), Shader.PropertyToID("_HiZMip2"), Shader.PropertyToID("_HiZMip3"),
            Shader.PropertyToID("_HiZMip4"), Shader.PropertyToID("_HiZMip5"), Shader.PropertyToID("_HiZMip6"), Shader.PropertyToID("_HiZMip7")
        };

        static void SetComputeParams(PassData data)
        {
            MaterialPropertyBlock block = s_SharedPropertyBlock;
            block.Clear();
            block.SetFloat("_AOIntensity", data.aoIntensity);
            block.SetFloat("_Radius", data.radius);
            block.SetFloat("_Bias", data.bias);
            block.SetInt("_NumDirs", data.numDirs);
            block.SetInt("_NumSteps", data.numSteps);
            block.SetFloat("_StepScale", data.stepScale);
            block.SetFloat("_AOTexRes", data.aoTexRes);
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

            TextureHandle hbao = renderGraph.CreateTexture(desc);
            TextureHandle temp = renderGraph.CreateTexture(desc);

            // 延迟 G-Buffer 法线（RG 模式不暴露 _GBuffer2 全局，直接取资源）
            TextureHandle gbuffer2 = default;
            if (resourcesData.gBuffer != null && resourcesData.gBuffer.Length > 2)
                gbuffer2 = resourcesData.gBuffer[2];

            // Pass 0: 计算 AO → hbao
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_HBAO Compute", out var computeData, m_ProfilingSampler))
            {
                computeData.material = hbaoMaterial;
                computeData.aoIntensity = AOIntensity;
                computeData.radius = Radius;
                computeData.bias = Bias;
                computeData.numDirs = NumDirs;
                computeData.numSteps = NumSteps;
                computeData.stepScale = StepScale;
                computeData.aoTexRes = isHalfSize ? 0.5f : 1.0f;
                computeData.gbuffer2 = gbuffer2;

                if (HizRenderFeature.IsActive)
                {
                    for (int i = 0; i < kHiZMipIds.Length; i++)
                        builder.UseGlobalTexture(kHiZMipIds[i], AccessFlags.Read);
                }
                else if (!s_warnedHiz)
                {
                    s_warnedHiz = true;
                    Debug.LogWarning("HBAO 需要启用 HizRenderFeature（shader 使用 SampleHIZ），否则 _HiZMip* 不存在，AO 将不可用。");
                }
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
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, s_SharedPropertyBlock);
                });
            }

            // Pass 1: 垂直模糊 → temp
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_HBAO Blur V", out var blurVData, m_ProfilingSampler))
            {
                blurVData.material = hbaoMaterial;
                blurVData.source = hbao;
                blurVData.aoTexRes = isHalfSize ? 0.5f : 1.0f;

                builder.UseTexture(hbao, AccessFlags.Read);
                builder.UseGlobalTexture(Shader.PropertyToID("_CameraDepthTexture"), AccessFlags.Read);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderAttachment(temp, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = s_SharedPropertyBlock;
                    block.Clear();
                    block.SetFloat("_AOTexRes", data.aoTexRes);
                    rgContext.cmd.SetGlobalTexture(Shader.PropertyToID("_MainTex"), data.source);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 1, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // Pass 2: 水平模糊 → hbao，并暴露 _HBAOResultTex 全局
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_HBAO Blur H", out var blurHData, m_ProfilingSampler))
            {
                blurHData.material = hbaoMaterial;
                blurHData.source = temp;
                blurHData.aoTexRes = isHalfSize ? 0.5f : 1.0f;

                builder.UseTexture(temp, AccessFlags.Read);
                builder.UseGlobalTexture(Shader.PropertyToID("_CameraDepthTexture"), AccessFlags.Read);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderAttachment(hbao, 0, AccessFlags.Write);

                builder.SetGlobalTextureAfterPass(hbao, Shader.PropertyToID("_HBAOResultTex"));

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = s_SharedPropertyBlock;
                    block.Clear();
                    block.SetFloat("_AOTexRes", data.aoTexRes);
                    rgContext.cmd.SetGlobalTexture(Shader.PropertyToID("_MainTex"), data.source);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 2, MeshTopology.Triangles, 3, 1, block);
                });
            }
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
            isHalfSize = settings.isHalfSize
        };
    }

    // Inject the pass
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_HBAOPass == null) return;

        renderer.EnqueuePass(m_HBAOPass);
    }
}
