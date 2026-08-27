using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class SunShaftRenderFeature : ScriptableRendererFeature
{
    private Material material;
    private SunShaftRenderPass renderPass;

    public Shader shader;
    public Color TintColor = Color.white;
    [Range(0, 10)]
    public float BloomThreshold = 0.0f;
    [Range(0, 5)]
    public float BloomScale = 0.2f;
    [Range(0.1f, 100)]
    public float BloomMaxBrightness = 100.0f;
    public float BlurRadius = 1.0f;
    [Range(3, 16)]
    public int BlurSamples = 8;

    public float MaskDepth = 100;
    [Range(0, 1)]
    public float ScreenFade = 0.1f;

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (material == null)
        {
            material = CoreUtils.CreateEngineMaterial(shader);
        }

        if (material == null) return;
        if (BloomScale == 0) return;
        renderer.EnqueuePass(renderPass);
    }
    public override void Create()
    {
        if(renderPass == null)
            renderPass = new SunShaftRenderPass(this);

        shader = Shader.Find("Loy/Feature/SunShaft");
    }

    public class SunShaftRenderPass : ScriptableRenderPass
    {
        private readonly SunShaftRenderFeature renderFeature;
        const string m_ProfilerTag = "Loy_SunShaft";
        readonly ProfilingSampler m_ProfilingSamplerGroup;
        readonly ProfilingSampler m_ProfilingSamplerDownsample;
        readonly ProfilingSampler m_ProfilingSamplerBlur1;
        readonly ProfilingSampler m_ProfilingSamplerBlur2;
        readonly ProfilingSampler m_ProfilingSamplerBlur3;
        readonly ProfilingSampler m_ProfilingSamplerCombine;
        readonly Vector4[] Params = new Vector4[3];

#if URP_COMPATIBILITY_MODE
        private RTHandle _temp1, _temp2;
#endif

        public SunShaftRenderPass(SunShaftRenderFeature renderFeature)
        {
            this.renderFeature = renderFeature;
            this.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            m_ProfilingSamplerGroup = new ProfilingSampler(m_ProfilerTag);
            m_ProfilingSamplerDownsample = new ProfilingSampler("Loy_SunShaft Downsample");
            m_ProfilingSamplerBlur1 = new ProfilingSampler("Loy_SunShaft Blur1");
            m_ProfilingSamplerBlur2 = new ProfilingSampler("Loy_SunShaft Blur2");
            m_ProfilingSamplerBlur3 = new ProfilingSampler("Loy_SunShaft Blur3");
            m_ProfilingSamplerCombine = new ProfilingSampler("Loy_SunShaft Combine");

            // 确保 _CameraDepthTexture 在 RG 模式下被生成
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        /// <summary>计算太阳屏幕位置与材质参数；太阳被遮挡/不可见时返回 false。</summary>
        bool ComputeParams(Camera cam)
        {
            var sun = RenderSettings.sun;
            if (sun == null) return false;

            var vp = cam.WorldToViewportPoint(cam.transform.position - sun.transform.forward * 100);
            if (Vector3.Dot(sun.transform.forward, cam.transform.forward) > 0) return false;

            Params[0] = new Vector4(renderFeature.BloomThreshold, renderFeature.BloomScale,
                renderFeature.BloomMaxBrightness, Mathf.Max(renderFeature.MaskDepth, 0));
            Params[1] = new Vector4(vp.x, vp.y, renderFeature.BlurRadius, renderFeature.BlurSamples);
            Params[2] = renderFeature.TintColor;
            Params[2].w = renderFeature.ScreenFade;
            return true;
        }

#if URP_COMPATIBILITY_MODE
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            Camera cam = renderingData.cameraData.camera;
            if (cam == null) return;
            if (cam.cameraType == CameraType.Preview) return;
            if (!ComputeParams(cam)) return;

            CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
            using (new ProfilingScope(cmd, m_ProfilingSamplerGroup))
            {
                int width = (int)(cam.pixelWidth * renderingData.cameraData.renderScale);
                int height = (int)(cam.pixelHeight * renderingData.cameraData.renderScale);
                var desc = new RenderTextureDescriptor(width / 2, height / 2, renderingData.cameraData.cameraTargetDescriptor.colorFormat, 0);
                RenderingUtils.ReAllocateHandleIfNeeded(ref _temp1, desc, FilterMode.Point, name: "_SunShaftTemp1");
                RenderingUtils.ReAllocateHandleIfNeeded(ref _temp2, desc, FilterMode.Point, name: "_SunShaftTemp2");

                var source = renderingData.cameraData.renderer.cameraColorTargetHandle;
                renderFeature.material.SetVectorArray("SunShaftParams", Params);

                cmd.Blit(source, _temp1, renderFeature.material, 0);
                cmd.Blit(_temp1, _temp2, renderFeature.material, 1);
                cmd.Blit(_temp2, _temp1, renderFeature.material, 2);
                cmd.Blit(_temp1, _temp2, renderFeature.material, 3);
                cmd.Blit(_temp2, source, renderFeature.material, 4);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            _temp1?.Release();
            _temp2?.Release();
        }
#endif

        class PassData
        {
            public Material material;
            public TextureHandle source;
            public Vector4[] sunShaftParams;
        }

        static readonly MaterialPropertyBlock s_SharedPropertyBlock = new MaterialPropertyBlock();
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        static readonly int SunShaftParamsId = Shader.PropertyToID("SunShaftParams");

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (renderFeature.material == null || cameraData.camera == null) return;
            if (cameraData.camera.cameraType == CameraType.Preview) return;
            if (!ComputeParams(cameraData.camera)) return;

            TextureDesc halfDesc = renderGraph.GetTextureDesc(resourcesData.activeColorTexture);
            halfDesc.width = Mathf.Max(1, halfDesc.width / 2);
            halfDesc.height = Mathf.Max(1, halfDesc.height / 2);
            halfDesc.depthBufferBits = 0;
            halfDesc.name = "_SunShaftTemp";
            halfDesc.clearBuffer = true;

            TextureHandle temp1 = renderGraph.CreateTexture(halfDesc);

            halfDesc.name = "_SunShaftTemp2"; // 中间缓冲独立命名，避免调试器里重名
            TextureHandle temp2 = renderGraph.CreateTexture(halfDesc);

            TextureHandle activeColor = resourcesData.activeColorTexture;
            Vector4[] sunShaftParams = Params;

            // Pass 0: 降采样 → temp1（读活动颜色 + 深度）
            // 外层分组：Frame Debugger 里 "Loy_SunShaft" 下嵌套各阶段
            renderGraph.BeginProfilingSampler(m_ProfilingSamplerGroup);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_SunShaft Downsample", out var pass0, m_ProfilingSamplerDownsample))
            {
                pass0.material = renderFeature.material;
                pass0.source = activeColor;
                pass0.sunShaftParams = sunShaftParams;

                builder.UseTexture(activeColor, AccessFlags.Read);
                builder.UseGlobalTexture(Shader.PropertyToID("_CameraDepthTexture"), AccessFlags.Read);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderAttachment(temp1, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = s_SharedPropertyBlock;
                    block.Clear();
                    block.SetVectorArray(SunShaftParamsId, data.sunShaftParams);
                    rgContext.cmd.SetGlobalTexture(MainTexId, data.source);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // Pass 1: 模糊1 temp1 → temp2
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_SunShaft Blur1", out var pass1, m_ProfilingSamplerBlur1))
            {
                pass1.material = renderFeature.material;
                pass1.source = temp1;
                pass1.sunShaftParams = sunShaftParams;

                builder.UseTexture(temp1, AccessFlags.Read);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderAttachment(temp2, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = s_SharedPropertyBlock;
                    block.Clear();
                    block.SetVectorArray(SunShaftParamsId, data.sunShaftParams);
                    rgContext.cmd.SetGlobalTexture(MainTexId, data.source);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 1, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // Pass 2: 模糊2 temp2 → temp1
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_SunShaft Blur2", out var pass2, m_ProfilingSamplerBlur2))
            {
                pass2.material = renderFeature.material;
                pass2.source = temp2;
                pass2.sunShaftParams = sunShaftParams;

                builder.UseTexture(temp2, AccessFlags.Read);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderAttachment(temp1, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = s_SharedPropertyBlock;
                    block.Clear();
                    block.SetVectorArray(SunShaftParamsId, data.sunShaftParams);
                    rgContext.cmd.SetGlobalTexture(MainTexId, data.source);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 2, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // Pass 3: 模糊3 temp1 → temp2
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_SunShaft Blur3", out var pass3, m_ProfilingSamplerBlur3))
            {
                pass3.material = renderFeature.material;
                pass3.source = temp1;
                pass3.sunShaftParams = sunShaftParams;

                builder.UseTexture(temp1, AccessFlags.Read);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderAttachment(temp2, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = s_SharedPropertyBlock;
                    block.Clear();
                    block.SetVectorArray(SunShaftParamsId, data.sunShaftParams);
                    rgContext.cmd.SetGlobalTexture(MainTexId, data.source);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 3, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // Pass 4: 合成 temp2 → 活动颜色（加法混合，需读回目标）
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_SunShaft Combine", out var pass4, m_ProfilingSamplerCombine))
            {
                pass4.material = renderFeature.material;
                pass4.source = temp2;
                pass4.sunShaftParams = sunShaftParams;

                builder.UseTexture(temp2, AccessFlags.Read);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderAttachment(activeColor, 0, AccessFlags.ReadWrite);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = s_SharedPropertyBlock;
                    block.Clear();
                    block.SetVectorArray(SunShaftParamsId, data.sunShaftParams);
                    rgContext.cmd.SetGlobalTexture(MainTexId, data.source);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 4, MeshTopology.Triangles, 3, 1, block);
                });
            }

            renderGraph.EndProfilingSampler(m_ProfilingSamplerGroup);
        }
    }
}
