using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;


public class SSRCombineRenderFeature : ScriptableRendererFeature
{
    public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingSkybox ;
    public Shader Shader;
    private Material _material;
    private SSRCombineRenderPass _renderPass;


    public override void Create()
    {
        if(_renderPass == null)
            _renderPass = new SSRCombineRenderPass(this);

        if (Shader && _material == null)
        {
            _material = CoreUtils.CreateEngineMaterial(Shader);
        }
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_material)
        {
            renderer.EnqueuePass(_renderPass);
        }
    }

    class SSRCombineRenderPass : ScriptableRenderPass
    {
        private const string m_ProfilerTag = "Loy_SSR Combine Pass";
        private const int kShaderPass = 1; // Loy_SSR.shader 的 "SSR Combine" Pass

        readonly ProfilingSampler m_ProfilingSampler;
        private readonly SSRCombineRenderFeature m_RenderFeature;

        public SSRCombineRenderPass(SSRCombineRenderFeature mRenderFeature)
        {
            this.m_RenderFeature = mRenderFeature;
            this.renderPassEvent = mRenderFeature.renderPassEvent;
            m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);

            // shader 采样 _GBuffer0/1/2（延迟渲染的 G-Buffer）
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

#if URP_COMPATIBILITY_MODE
#pragma warning disable CS0672 // 覆盖已废弃的 Execute，仅兼容模式下使用
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                cmd.DrawProcedural(Matrix4x4.identity, m_RenderFeature._material, kShaderPass, MeshTopology.Triangles, 3, 1);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
#pragma warning restore CS0672
#endif

        class PassData
        {
            public Material material;
            public TextureHandle gbuffer0;
            public TextureHandle gbuffer1;
            public TextureHandle gbuffer2;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            if (m_RenderFeature._material == null) return;

            // 延迟 G-Buffer（RG 模式不暴露 _GBuffer* 全局，直接取资源）
            TextureHandle gbuffer0 = default, gbuffer1 = default, gbuffer2 = default;
            if (resourcesData.gBuffer != null && resourcesData.gBuffer.Length > 2)
            {
                gbuffer0 = resourcesData.gBuffer[0];
                gbuffer1 = resourcesData.gBuffer[1];
                gbuffer2 = resourcesData.gBuffer[2];
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(m_ProfilerTag, out var passData, m_ProfilingSampler))
            {
                passData.material = m_RenderFeature._material;
                passData.gbuffer0 = gbuffer0;
                passData.gbuffer1 = gbuffer1;
                passData.gbuffer2 = gbuffer2;

                // shader 读取的全局：SSR 结果/历史 + G-Buffer
                builder.UseGlobalTexture(Shader.PropertyToID("_SSRResultTex"), AccessFlags.Read);
                builder.UseGlobalTexture(Shader.PropertyToID("_SSRHistoryTex"), AccessFlags.Read);
                if (gbuffer0.IsValid()) { builder.UseTexture(gbuffer0, AccessFlags.Read); builder.UseTexture(gbuffer1, AccessFlags.Read); builder.UseTexture(gbuffer2, AccessFlags.Read); }
                builder.AllowGlobalStateModification(true);

                // 混合到当前颜色目标
                builder.SetRenderAttachment(resourcesData.activeColorTexture, 0, AccessFlags.ReadWrite);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    if (data.gbuffer0.IsValid())
                    {
                        rgContext.cmd.SetGlobalTexture(Shader.PropertyToID("_GBuffer0"), data.gbuffer0);
                        rgContext.cmd.SetGlobalTexture(Shader.PropertyToID("_GBuffer1"), data.gbuffer1);
                        rgContext.cmd.SetGlobalTexture(Shader.PropertyToID("_GBuffer2"), data.gbuffer2);
                    }
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, kShaderPass, MeshTopology.Triangles, 3, 1);
                });
            }
        }
    }
}
