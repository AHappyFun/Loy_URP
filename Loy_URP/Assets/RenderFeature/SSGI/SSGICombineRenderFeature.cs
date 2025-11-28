using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
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

        ProfilingSampler m_ProfilingSampler;

        private SSGICombineRenderFeature m_RenderFeature = null;


        public SSGICombineRenderPass(SSGICombineRenderFeature mRenderFeature)
        {
            this.m_RenderFeature = mRenderFeature;
            this.renderPassEvent = mRenderFeature.renderPassEvent;
            m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);

        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {

            CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                m_RenderFeature._material.SetFloat("_GIRange", m_RenderFeature.GIRange);

                cmd.DrawProcedural(Matrix4x4.identity, m_RenderFeature._material, 3, MeshTopology.Triangles, 3, 1);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
