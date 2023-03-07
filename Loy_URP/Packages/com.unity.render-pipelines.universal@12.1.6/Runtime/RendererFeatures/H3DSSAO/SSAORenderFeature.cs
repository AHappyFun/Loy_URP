using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class SSAORenderFeature : ScriptableRendererFeature
{
    public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
  
    private SSAOPass m_renderPass;
    private Material m_material;
    public Shader shader;
    private SSAO m_ssao;

    private const string k_ShaderName = "Aley/Feature/SSAO";
    
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
 
        m_ssao = VolumeManager.instance.stack.GetComponent<SSAO>();
    
        if (!m_ssao.IsActive()) return;

        if (!GetMaterial()) return;
        
        m_renderPass.Setup(renderer);
        renderer.EnqueuePass(m_renderPass);
    }
    
    public override void Create()
    {
        if (m_renderPass == null)
        {
            m_renderPass = new SSAOPass(this);
        }
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(m_material);
    }

    private bool GetMaterial()
    {
        if (m_material != null)
        {
            return true;
        }

        if (shader == null)
        {
            shader = Shader.Find(k_ShaderName);
            if (shader == null)
                return false;
        }

        m_material = CoreUtils.CreateEngineMaterial(shader);

        return m_material != null;
    }
    

    public class SSAOPass : ScriptableRenderPass
    {
        const string m_ProfilerTag = "H3D_SSAO";
        SSAORenderFeature m_RenderFeature;
        private RenderTargetHandle m_temp, m_temp2;
        private const string k_SSAOTextureName = "_ScreenSpaceOcclusionTexture";
        private ScriptableRenderer m_Renderer;
        
        private bool downSample => m_RenderFeature.m_ssao.DownSample.value;

        private bool afterOpaque => false; // m_RenderFeature.m_ssao.AfterOpaque.value;
        
        public SSAOPass(SSAORenderFeature mRenderFeature)
        {
            m_RenderFeature = mRenderFeature;
            m_temp.Init("_SSAOTex");
            m_temp2.Init("_AOParams");
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            cameraTextureDescriptor.depthBufferBits = 0;
            cameraTextureDescriptor.msaaSamples = 1;
            if (downSample)
            {
                cameraTextureDescriptor.width /= 2;
                cameraTextureDescriptor.height /= 2;
            }
            cameraTextureDescriptor.graphicsFormat = GraphicsFormat.R8_UNorm;
            cmd.GetTemporaryRT(m_temp.id, cameraTextureDescriptor, FilterMode.Bilinear);
            cmd.GetTemporaryRT(m_temp2.id, cameraTextureDescriptor, FilterMode.Bilinear);
            ConfigureTarget(afterOpaque ? m_Renderer.cameraColorTarget : m_temp.Identifier());
            ConfigureInput(ScriptableRenderPassInput.Normal); // Require depth
        }

        public void Setup(ScriptableRenderer renderer)
        {
            m_Renderer = renderer;
            renderPassEvent = afterOpaque ? RenderPassEvent.AfterRenderingOpaques : m_RenderFeature.renderPassEvent;
        }
        

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            
            CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
            
            cmd.SetGlobalVector("_AmbientOcclusionParams", new Vector4(
                m_RenderFeature.m_ssao.Radius.value, m_RenderFeature.m_ssao.Samples.value, m_RenderFeature.m_ssao.Intensity.value, m_RenderFeature.m_ssao.DirectLightStrength.value
                )
            );

            CoreUtils.SetKeyword(cmd, "_H3DSSAO", !afterOpaque);
            //cmd.SetGlobalFloat("_H3DSSAO", 1);

            // ConfigureTarget(m_temp.Identifier());
            cmd.SetRenderTarget(m_temp.Identifier());
            cmd.DrawProcedural(Matrix4x4.identity, m_RenderFeature.m_material, 0, MeshTopology.Triangles, 3);
            cmd.Blit(m_temp.id, m_temp2.Identifier(), m_RenderFeature.m_material, 1);
            cmd.Blit(m_temp2.Identifier(), m_temp.Identifier(), m_RenderFeature.m_material, 2);
            cmd.SetGlobalTexture(k_SSAOTextureName, m_temp.Identifier());

            if (afterOpaque)
            {
                // This implicitly also bind depth attachment. Explicitly binding m_Renderer.cameraDepthTarget does not work.
                cmd.SetRenderTarget(
                    m_Renderer.cameraColorTarget,
                    RenderBufferLoadAction.Load,
                    RenderBufferStoreAction.Store
                );
                cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_RenderFeature.m_material, 0,3);
            }
            
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void FrameCleanup(CommandBuffer cmd)
        {
            CoreUtils.SetKeyword(cmd, "_H3DSSAO", false);
            //cmd.SetGlobalFloat("_H3DSSAO", 0);
        }
    }
}
