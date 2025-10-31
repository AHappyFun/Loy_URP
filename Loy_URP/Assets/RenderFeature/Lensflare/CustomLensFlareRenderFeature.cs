using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;

/// <summary>
/// 自定义阳光LensFlare效果
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
        if(renderPass == null)
            renderPass = new CustomLensFlareRenderPass(this);

        shader = Shader.Find("Loy/Feature/CustomLensFlare");

    }

    class CustomLensFlareRenderPass : ScriptableRenderPass
    {
        const string m_ProfilerTag = "Loy_CustomLensFlare";
        ProfilingSampler m_ProfilingSampler;

        private CustomLensFlareRenderFeature renderFeature;
        public Vector4 FlarePos0;
        public Vector4 FlarePos1;
        public Vector4 FlarePos2;
        public Vector4 FlarePos3;

        public Vector4[] FlareParams; //xy pos z scale w intensity


        public CustomLensFlareRenderPass(CustomLensFlareRenderFeature renderFeature)
        {
            m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);
            this.renderFeature = renderFeature;
            this.renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
            FlareParams = new Vector4[4];
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {

            //主光
            Light sun = RenderSettings.sun;
            //
            if(sun == null)
                return;

            //自定义光源
            //var lens = DistortManager.Instance.LensFlareInstances;
            //var len = lens[0];

            Camera cam = renderingData.cameraData.camera;
            Vector3 worldPos = cam.transform.position + sun.transform.forward * -cam.farClipPlane * 0.99f;

            Vector3 viewPos = cam.worldToCameraMatrix.MultiplyPoint(worldPos);

            Vector3 sunScreenPos = cam.WorldToViewportPoint(worldPos);

            if (sunScreenPos.x < 0.0f || sunScreenPos.y < 0.0f || sunScreenPos.x > 1 || sunScreenPos.y > 1)
            {
                return;
            }

            // 👇 检查是否在摄像机前方（z > 0）
            if (viewPos.z > 0f)
            {
                return;
            }

            Vector3 screenPos = cam.WorldToViewportPoint(worldPos);
            Vector2 screenCenter = new Vector2(0.5f, 0.5f);
            Vector2 dir = screenCenter - new Vector2(screenPos.x, screenPos.y);


            for (int i = 0; i < 4; i++)
            {
                float offsetV = renderFeature.FlareOffset[i];
                Vector2 viewport = new Vector2(screenPos.x, screenPos.y) + dir * offsetV;
                FlareParams[i] = new Vector4(viewport.x, viewport.y, renderFeature.FlareScale[i], renderFeature.FlareIntensity[i]);
            }

            CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                for (int i = 0; i < renderFeature.FlareLayerCount; i++)
                {
                    renderFeature.material.SetTexture("_FlareTex" + i, renderFeature.FlareTexs[i]);
                }

                renderFeature.material.SetVectorArray("_FlareParams", FlareParams);
                renderFeature.material.SetInt("_FlareLayerCount", renderFeature.FlareLayerCount);
                renderFeature.material.SetVector("_FlareSunPos", sunScreenPos);
                renderFeature.material.SetFloat("_ScreenAspect", (float)cam.pixelHeight / (float)cam.pixelWidth);

                cmd.DrawProcedural(Matrix4x4.identity, renderFeature.material, 0, MeshTopology.Triangles, 3, 1);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
