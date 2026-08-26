using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// 反射纹理通过 ContextItem 在 Feature 之间传递（显式依赖），不暴露全局纹理。
public sealed class WaterReflectionFrameData : ContextItem
{
    public TextureHandle reflection;

    public override void Reset()
    {
        reflection = TextureHandle.nullHandle;
    }
}

public class WaterReflectionRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public Material reflectionMaterial = null;
        public RenderPassEvent passEvent = RenderPassEvent.AfterRenderingSkybox;
        public float waterPlaneY = 0.0f;
        public float reflectionDistance = 50.0f;
    }

    public Settings settings = new Settings();

    class WaterReflectionPass : ScriptableRenderPass
    {
        readonly Material material;
        readonly float planeY;
        readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler("Loy_Water_SSPR");

        public WaterReflectionPass(Material mat, float planeY, float reflectionDistance)
        {
            material = mat;
            this.planeY = planeY;
            this.reflectionDistance = reflectionDistance;
        }

        readonly float reflectionDistance;

        class PassData
        {
            public Material material;
            public MaterialPropertyBlock block;
            public TextureHandle sceneColor;
            public TextureHandle depth;
            public float planeY;
            public float reflectionDistance;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (material == null) return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData.camera == null) return;

            // 反射源：场景颜色（含天空盒）。此时 activeColor 仍是 cameraColor（deferred 光照 + SSR + 天空盒）。
            TextureHandle sceneColor = resources.cameraColor;
            if (!sceneColor.IsValid() || !resources.cameraDepthTexture.IsValid()) return;

            // 反射结果纹理（仅本图内存在，通过 ContextItem 交给水面 Feature）
            TextureDesc desc = renderGraph.GetTextureDesc(sceneColor);
            desc.depthBufferBits = 0;
            desc.format = GraphicsFormat.R16G16B16A16_SFloat;
            desc.msaaSamples = MSAASamples.None;
            desc.clearBuffer = true;
            desc.name = "_WaterReflectionTex";
            TextureHandle reflection = renderGraph.CreateTexture(desc);

            frameData.GetOrCreate<WaterReflectionFrameData>().reflection = reflection;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_Water_SSPR", out var passData, m_ProfilingSampler))
            {
                passData.material = material;
                passData.block = new MaterialPropertyBlock();
                passData.sceneColor = sceneColor;
                passData.depth = resources.cameraDepthTexture;
                passData.planeY = planeY;
                passData.reflectionDistance = reflectionDistance;

                builder.UseTexture(sceneColor, AccessFlags.Read);
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                builder.SetRenderAttachment(reflection, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetTexture(Shader.PropertyToID("_CameraOpaqueTexture"), data.sceneColor);
                    block.SetTexture(Shader.PropertyToID("_CameraDepthTexture"), data.depth);
                    block.SetFloat(Shader.PropertyToID("_WaterPlaneY"), data.planeY);
                    block.SetFloat(Shader.PropertyToID("_ReflectionDistance"), data.reflectionDistance);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, block);
                });
            }
        }
    }

    WaterReflectionPass m_Pass;

    public override void Create()
    {
        if (settings.reflectionMaterial == null)
        {
            Debug.LogWarning("WaterReflectionRenderFeature: reflectionMaterial is null.");
            return;
        }

        m_Pass = new WaterReflectionPass(settings.reflectionMaterial, settings.waterPlaneY, settings.reflectionDistance)
        {
            renderPassEvent = settings.passEvent,
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_Pass == null) return;
        renderer.EnqueuePass(m_Pass);
    }
}
