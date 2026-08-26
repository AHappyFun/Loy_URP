using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// 单独画水面的 Feature：通过 WaterReflectionFrameData 显式读取反射纹理，
// 用 RendererList 按自定义 LightMode ("LoyWaterSurface") 筛选水面渲染器绘制。
// 水面 shader 不再声明 "UniversalForward"，所以标准透明 pass 会自动跳过它，无需关闭 renderer。
public class WaterSurfaceRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public RenderPassEvent passEvent = RenderPassEvent.BeforeRenderingTransparents;
        public int layerMask = -1; // 默认全层，靠 LightMode 筛选
    }

    public Settings settings = new Settings();

    class WaterSurfacePass : ScriptableRenderPass
    {
        readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler("Loy_Water_Surface");
        readonly List<ShaderTagId> m_ShaderTagIds = new List<ShaderTagId> { new ShaderTagId("LoyWaterSurface") };
        readonly FilteringSettings m_FilteringSettings;

        public WaterSurfacePass(RenderPassEvent evt, int layerMask)
        {
            renderPassEvent = evt;
            m_FilteringSettings = new FilteringSettings(RenderQueueRange.transparent, layerMask);
        }

        class PassData
        {
            public RendererListHandle rendererList;
            public TextureHandle reflection;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!frameData.Contains<WaterReflectionFrameData>())
                return;

            WaterReflectionFrameData reflData = frameData.Get<WaterReflectionFrameData>();
            if (!reflData.reflection.IsValid())
                return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_Water_Surface", out var passData, m_ProfilingSampler))
            {
                passData.reflection = reflData.reflection;
                builder.UseTexture(reflData.reflection, AccessFlags.Read);   // 显式依赖反射纹理

                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);

                DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(
                    m_ShaderTagIds, renderingData, cameraData, lightData, SortingCriteria.CommonTransparent);
                var param = new RendererListParams(renderingData.cullResults, drawingSettings, m_FilteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(param);
                builder.UseRendererList(passData.rendererList);

                // 需要设置全局纹理(_WaterReflectionTex)供 RendererList 内的水面材质采样
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    ctx.cmd.SetGlobalTexture(Shader.PropertyToID("_WaterReflectionTex"), data.reflection);
                    ctx.cmd.DrawRendererList(data.rendererList);
                });
            }
        }
    }

    WaterSurfacePass m_Pass;

    public override void Create()
    {
        m_Pass = new WaterSurfacePass(settings.passEvent, settings.layerMask);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_Pass == null) return;
        renderer.EnqueuePass(m_Pass);
    }
}
