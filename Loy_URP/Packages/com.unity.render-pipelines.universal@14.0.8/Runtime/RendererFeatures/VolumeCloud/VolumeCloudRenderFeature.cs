using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class VolumeCloudRenderFeature : ScriptableRendererFeature
{

    public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;

    private VolumeCloudRenderPass cloudPass;
    private Material m_Material;
    public Shader shader;
    private const string k_ShaderName = "Aley/Feature/VolumeCloud";

    [HideInInspector]
    public VolumeCloudConfig m_VolumeCloudConfig;

    public override void Create()
    {
        if (cloudPass == null)
        {
            cloudPass = new VolumeCloudRenderPass(renderPassEvent, this);
        }
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        m_VolumeCloudConfig = VolumeManager.instance.stack.GetComponent<VolumeCloudConfig>();
        if(!m_VolumeCloudConfig.IsActive())
            return;
        
        if(!GetMaterial()) return;
        
        cloudPass.Setup(renderer);
        
        renderer.EnqueuePass(cloudPass);
    }
    
    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(m_Material);
    }

    private bool GetMaterial()
    {
        if (m_Material != null)
        {
            return true;
        }

        if (shader == null)
        {
            shader = Shader.Find(k_ShaderName);
            if (shader == null)
                return false;
        }

        m_Material = CoreUtils.CreateEngineMaterial(shader);

        return m_Material != null;
    }
}

public class VolumeCloudRenderPass : ScriptableRenderPass
{
    const string m_ProfilerTag = "Loy_VolumeCloud";
    VolumeCloudRenderFeature m_RenderFeature;
    private ScriptableRenderer m_Renderer;
    
    public VolumeCloudRenderPass(RenderPassEvent rpe, VolumeCloudRenderFeature feature)
    {
        renderPassEvent = rpe;
        m_RenderFeature = feature;
    }
    
    public void Setup(ScriptableRenderer renderer)
    {
        m_Renderer = renderer;
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }
}
