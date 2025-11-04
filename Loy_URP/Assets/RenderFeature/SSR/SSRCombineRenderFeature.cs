using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
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

        ProfilingSampler m_ProfilingSampler;

        private SSRCombineRenderFeature m_RenderFeature = null;


        public SSRCombineRenderPass(SSRCombineRenderFeature mRenderFeature)
        {
            this.m_RenderFeature = mRenderFeature;
            this.renderPassEvent = mRenderFeature.renderPassEvent;
            m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);
            ConfigureInput(ScriptableRenderPassInput.Color);

        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {

            CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                cmd.DrawProcedural(Matrix4x4.identity, m_RenderFeature._material, 1, MeshTopology.Triangles, 3, 1);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
