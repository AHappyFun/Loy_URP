using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;


public class SSGICombineRenderFeature : ScriptableRendererFeature
{
    public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingSkybox ;
    public Shader Shader;
    private Material _material;
    private SSGICombineRenderPass _renderPass;


    [Range(0, 1)]
    public float GIRange = 0.2f;

    public override void Create()
    {
        if(_renderPass == null)
            _renderPass = new SSGICombineRenderPass(this);

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

    class SSGICombineRenderPass : ScriptableRenderPass
    {
        private const string m_ProfilerTag = "Loy_SSGI Combine Pass";
        private const int kShaderPass = 3; // Loy_SSGI.shader 的 "SSGI Combine" Pass

        readonly ProfilingSampler m_ProfilingSampler;
        private readonly SSGICombineRenderFeature m_RenderFeature;

        public SSGICombineRenderPass(SSGICombineRenderFeature mRenderFeature)
        {
            this.m_RenderFeature = mRenderFeature;
            this.renderPassEvent = mRenderFeature.renderPassEvent;
            m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);
        }

#if URP_COMPATIBILITY_MODE
#pragma warning disable CS0672 // 覆盖已废弃的 Execute，仅兼容模式下使用
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                m_RenderFeature._material.SetFloat("_GIRange", m_RenderFeature.GIRange);
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
            public TextureHandle hbao;
            public TextureHandle ssgi;
            public float giRange;
        }

        static readonly MaterialPropertyBlock s_SharedPropertyBlock = new MaterialPropertyBlock();

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            if (m_RenderFeature._material == null) return;

            if (!frameData.Contains<HBAOFrameData>())
                return;

            if (!frameData.Contains<SSGIFrameData>())
                return;

            HBAOFrameData hbaoData = frameData.Get<HBAOFrameData>();
            SSGIFrameData ssgiData = frameData.Get<SSGIFrameData>();
            if (!hbaoData.result.IsValid() || !ssgiData.result.IsValid())
                return;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(m_ProfilerTag, out var passData, m_ProfilingSampler))
            {
                passData.material = m_RenderFeature._material;
                passData.hbao = hbaoData.result;
                passData.ssgi = ssgiData.result;
                passData.giRange = m_RenderFeature.GIRange;

                builder.UseTexture(hbaoData.result, AccessFlags.Read);
                builder.UseTexture(ssgiData.result, AccessFlags.Read);

                // 加法混合到当前颜色目标（Blend SrcAlpha One 需要读回目标）
                builder.SetRenderAttachment(resourcesData.activeColorTexture, 0, AccessFlags.ReadWrite);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = s_SharedPropertyBlock;
                    block.Clear();
                    block.SetFloat("_GIRange", data.giRange);
                    block.SetTexture(Shader.PropertyToID("_HBAOResultTex"), data.hbao);
                    block.SetTexture(Shader.PropertyToID("_SSGIResultTex"), data.ssgi);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, kShaderPass, MeshTopology.Triangles, 3, 1, block);
                });
            }
        }
    }
}
