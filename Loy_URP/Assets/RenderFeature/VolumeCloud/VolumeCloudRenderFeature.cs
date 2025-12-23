using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class VolumeCloudRenderFeature : ScriptableRendererFeature
{
    class VolumeCloudRenderPass : ScriptableRenderPass
    {
        Material vcMaterial;

        public VolumeCloudRenderPass(Material mat)
        {
            vcMaterial = mat;
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            base.Configure(cmd, cameraTextureDescriptor);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (vcMaterial == null) return;

            var cmd = CommandBufferPool.Get("Loy_VolumeCloud Pass");

            cmd.DrawProcedural(Matrix4x4.identity, vcMaterial, 0, MeshTopology.Triangles, 3, 1);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    [System.Serializable]
    public class VolumeCloudSettings
    {
        public Material vcMaterial = null;
        public RenderPassEvent passEvent = RenderPassEvent.AfterRenderingDeferredLights;
    }

    public VolumeCloudSettings Settings = new VolumeCloudSettings();
    private VolumeCloudRenderPass m_vcPass;

    public override void Create()
    {
        if (Settings.vcMaterial == null)
        {
            Debug.LogWarning("VolumeCloudFeature: vcMaterial is null.");
            return;
        }

        m_vcPass = new VolumeCloudRenderPass(Settings.vcMaterial)
        {
            renderPassEvent = Settings.passEvent,
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if(m_vcPass == null)
            return;

        renderer.EnqueuePass(m_vcPass);
    }
}
