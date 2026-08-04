using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 自定义阳光 LensFlare 效果（支持 RenderGraph 与兼容模式双路径）
/// </summary>
public class CustomLensFlareRenderFeature : ScriptableRendererFeature
{
    private Material material;
    private CustomLensFlareRenderPass renderPass;

    public Shader shader;

    public List<float> FlareIntensity;
    public List<float> FlareScale;
    public List<float> FlareOffset;
    public int FlareLayerCount;

    public Texture2D[] FlareTexs;

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (material == null)
        {
            material = CoreUtils.CreateEngineMaterial(shader);
        }

        if (material == null) return;

        renderer.EnqueuePass(renderPass);
    }
    public override void Create()
    {
        if (renderPass == null)
            renderPass = new CustomLensFlareRenderPass(this);

        shader = Shader.Find("Loy/Feature/CustomLensFlare");
    }

    class CustomLensFlareRenderPass : ScriptableRenderPass
    {
        const string m_ProfilerTag = "Loy_CustomLensFlare";
        readonly ProfilingSampler m_ProfilingSampler;

        readonly CustomLensFlareRenderFeature renderFeature;
        readonly Vector4[] FlareParams = new Vector4[4]; // xy 位置, z 缩放, w 强度（Execute 路径复用）

        public CustomLensFlareRenderPass(CustomLensFlareRenderFeature renderFeature)
        {
            m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);
            this.renderFeature = renderFeature;
            this.renderPassEvent = RenderPassEvent.AfterRenderingTransparents;

            // shader 通过 SampleSceneDepth(_CameraDepthTexture) 做太阳遮挡判定
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        /// <summary>计算太阳屏幕位置与各层耀斑参数；太阳不可见或不在屏幕内时返回 false。</summary>
        bool ComputeFlareParams(Camera cam, Vector4[] flareParams, out Vector4 sunScreenPos)
        {
            sunScreenPos = Vector4.zero;
            Light sun = RenderSettings.sun;
            if (sun == null)
                return false;

            Vector3 worldPos = cam.transform.position + sun.transform.forward * -cam.farClipPlane * 0.99f;
            Vector3 viewPos = cam.worldToCameraMatrix.MultiplyPoint(worldPos);
            Vector3 screenPos = cam.WorldToViewportPoint(worldPos);

            // 太阳不在屏幕内
            if (screenPos.x < 0.0f || screenPos.y < 0.0f || screenPos.x > 1 || screenPos.y > 1)
                return false;
            // 太阳在摄像机后方
            if (viewPos.z > 0f)
                return false;

            Vector2 screenCenter = new Vector2(0.5f, 0.5f);
            Vector2 dir = screenCenter - new Vector2(screenPos.x, screenPos.y);

            for (int i = 0; i < 4; i++)
            {
                float offsetV = renderFeature.FlareOffset[i];
                Vector2 viewport = new Vector2(screenPos.x, screenPos.y) + dir * offsetV;
                flareParams[i] = new Vector4(viewport.x, viewport.y, renderFeature.FlareScale[i], renderFeature.FlareIntensity[i]);
            }

            sunScreenPos = screenPos;
            return true;
        }

        void ApplyMaterialParams(Camera cam, Vector4 sunScreenPos)
        {
            for (int i = 0; i < renderFeature.FlareLayerCount; i++)
                renderFeature.material.SetTexture("_FlareTex" + i, renderFeature.FlareTexs[i]);
            renderFeature.material.SetVectorArray("_FlareParams", FlareParams);
            renderFeature.material.SetInt("_FlareLayerCount", renderFeature.FlareLayerCount);
            renderFeature.material.SetVector("_FlareSunPos", sunScreenPos);
            renderFeature.material.SetFloat("_ScreenAspect", (float)cam.pixelHeight / (float)cam.pixelWidth);
        }

#if URP_COMPATIBILITY_MODE
#pragma warning disable CS0672 // 覆盖已废弃的 Execute，仅兼容模式下使用
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            Camera cam = renderingData.cameraData.camera;
            if (cam == null) return;
            if (!ComputeFlareParams(cam, FlareParams, out Vector4 sunScreenPos)) return;
            if (renderFeature.material == null) return;

            CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                ApplyMaterialParams(cam, sunScreenPos);
                cmd.DrawProcedural(Matrix4x4.identity, renderFeature.material, 0, MeshTopology.Triangles, 3, 1);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
#pragma warning restore CS0672
#endif

        class PassData
        {
            public Material material;
            public Texture2D[] flareTexs;
            public Vector4[] flareParams;
            public Vector4 sunScreenPos;
            public int flareLayerCount;
            public float screenAspect;
        }

        static readonly MaterialPropertyBlock s_SharedPropertyBlock = new MaterialPropertyBlock();

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            if (cameraData.camera == null) return;
            Vector4[] flareParams = new Vector4[4]; // 每相机独立数组，避免多相机共享字段被覆盖
            if (!ComputeFlareParams(cameraData.camera, flareParams, out Vector4 sunScreenPos)) return;
            if (renderFeature.material == null) return;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(m_ProfilerTag, out var passData, m_ProfilingSampler))
            {
                passData.material = renderFeature.material;
                passData.flareTexs = renderFeature.FlareTexs;
                passData.flareParams = flareParams;
                passData.sunScreenPos = sunScreenPos;
                passData.flareLayerCount = renderFeature.FlareLayerCount;
                passData.screenAspect = cameraData.camera.pixelHeight / (float)cameraData.camera.pixelWidth;

                // 读取 _CameraDepthTexture 做太阳遮挡（声明读依赖，RG 会正确排序深度拷贝）
                if (resourcesData.cameraDepthTexture.IsValid())
                    builder.UseTexture(resourcesData.cameraDepthTexture, AccessFlags.Read);

                // 加法混合叠加到当前颜色目标
                builder.SetRenderAttachment(resourcesData.activeColorTexture, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = s_SharedPropertyBlock;
                    block.Clear();
                    if (data.flareTexs != null)
                    {
                        int layers = Mathf.Min(data.flareLayerCount, 4);
                        for (int i = 0; i < layers; i++)
                            block.SetTexture("_FlareTex" + i, data.flareTexs[i]);
                    }
                    block.SetVectorArray("_FlareParams", data.flareParams);
                    block.SetInt("_FlareLayerCount", data.flareLayerCount);
                    block.SetVector("_FlareSunPos", data.sunScreenPos);
                    block.SetFloat("_ScreenAspect", data.screenAspect);

                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, block);
                });
            }
        }
    }
}
