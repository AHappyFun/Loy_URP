using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class VolumeCloudRenderFeature : ScriptableRendererFeature
{
    class VolumeCloudRenderPass : ScriptableRenderPass
    {
        const string m_ProfilerTag = "Loy_VolumeCloud Pass";
        readonly ProfilingSampler m_ProfilingSampler;
        readonly Material vcMaterial;

        public VolumeCloudRenderPass(Material mat)
        {
            vcMaterial = mat;
            m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);
        }

#if URP_COMPATIBILITY_MODE
#pragma warning disable CS0672 // 覆盖已废弃的 Execute，仅兼容模式下使用
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (vcMaterial == null) return;

            var cmd = CommandBufferPool.Get(m_ProfilerTag);
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                cmd.DrawProcedural(Matrix4x4.identity, vcMaterial, 0, MeshTopology.Triangles, 3, 1);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
#pragma warning restore CS0672
#endif

        class PassData
        {
            public Material material;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            if (vcMaterial == null) return;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(m_ProfilerTag, out var passData, m_ProfilingSampler))
            {
                passData.material = vcMaterial;

                // shader 用 ZTest LEqual 与场景深度比较，需要绑定深度
                builder.SetRenderAttachmentDepth(resourcesData.activeDepthTexture, AccessFlags.Read);

                // 混合写入当前颜色目标
                builder.SetRenderAttachment(resourcesData.activeColorTexture, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1);
                });
            }
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
