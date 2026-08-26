using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// 体积光（丁达尔效应）：半分辨率屏幕空间体光线步进 + 高斯模糊 + 加性合成。
// 沿视线在雾里采 3D Worley 噪声做密度，主光阴影图集做光束遮挡，阴影区不散射 → 光束只出现在光照到达的雾里。
// RenderGraph 写法，全部纹理依赖用显式 UseTexture 句柄（不依赖全局槽生命周期）。
public class VolumetricLightRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public Material volumetricMaterial = null;
        // 在透明物之后合成（450），SSRCombine(350) 之后、后处理(550) 之前
        public RenderPassEvent passEvent = RenderPassEvent.AfterRenderingTransparents;
        [Range(0.25f, 1f)] public float resolutionScale = 0.5f;    // raymarch/模糊分辨率（1 = 全屏）
        [Range(1, 128)] public int steps = 32;                      // 光线步进步数
        public float maxDistance = 60f;                             // 体积雾有效距离（相机起算）
        public float fogDensity = 0.08f;                            // 体积雾基准密度
        public float fogHeightStart = 0f;                           // 高度雾起始高度
        public float fogHeightFalloff = 0.04f;                      // 高度雾指数衰减
        public float noiseScale = 0.1f;                             // 3D 噪声平铺频率
        public float noiseIntensity = 0.35f;                        // 噪声对密度的影响（0 = 均匀雾）
        public Vector3 noiseScroll = new Vector3(0.01f, 0.01f, 0.02f); // 噪声流动速度
        [Range(-0.9f, 0.9f)] public float phaseG = 0.35f;           // HG 前向散射
        [Range(0f, 1f)] public float shadowStrength = 0.85f;        // 阴影对光束的衰减强度
        public float intensity = 1.2f;                              // 整体强度
        public Color tint = Color.white;                            // 光色（乘主光颜色）
    }

    public Settings settings = new Settings();

    class VolumetricLightPass : ScriptableRenderPass
    {
        readonly ProfilingSampler m_SamplerRayMarch = new ProfilingSampler("Loy_VolumetricLight RayMarch");
        readonly ProfilingSampler m_SamplerBlurV = new ProfilingSampler("Loy_VolumetricLight BlurV");
        readonly ProfilingSampler m_SamplerBlurH = new ProfilingSampler("Loy_VolumetricLight BlurH");
        readonly ProfilingSampler m_SamplerComposite = new ProfilingSampler("Loy_VolumetricLight Composite");

        readonly Material material;

        public float resolutionScale = 0.5f;
        public int steps = 32;
        public float maxDistance = 60f;
        public float fogDensity = 0.08f;
        public float fogHeightStart = 0f;
        public float fogHeightFalloff = 0.04f;
        public float noiseScale = 0.1f;
        public float noiseIntensity = 0.35f;
        public Vector3 noiseScroll = new Vector3(0.01f, 0.01f, 0.02f);
        public float phaseG = 0.35f;
        public float shadowStrength = 0.85f;
        public float intensity = 1.2f;
        public Color tint = Color.white;

        public VolumetricLightPass(Material mat)
        {
            material = mat;
        }

        class PassData
        {
            public Material material;
            public MaterialPropertyBlock block;
            public TextureHandle depth;
            public TextureHandle shadowMap;      // 主光阴影图集（可能无效）
            public TextureHandle blurSource;     // 模糊源
            public TextureHandle volumetricTex;  // 合成时采样的体积光纹理
            public Vector2 blurDir;              // 预乘 texel size 的方向
            // raymarch 参数
            public float fogDensity, fogHeightStart, fogHeightFalloff;
            public float noiseScale, noiseIntensity;
            public Vector3 noiseScroll;
            public float phaseG, shadowStrength, intensity, maxDistance;
            public int steps;
            public Color tint;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (material == null) return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData.camera == null) return;
            if (cameraData.camera.cameraType == CameraType.Preview) return;
            if (!resources.cameraDepthTexture.IsValid()) return;

            // 半分辨率体积光缓冲（R16G16B16A16，避免低分辨率下色带）
            TextureDesc desc = renderGraph.GetTextureDesc(resources.activeColorTexture);
            desc.width = Mathf.Max(1, Mathf.RoundToInt(desc.width * resolutionScale));
            desc.height = Mathf.Max(1, Mathf.RoundToInt(desc.height * resolutionScale));
            desc.depthBufferBits = 0;
            desc.format = GraphicsFormat.R16G16B16A16_SFloat;
            desc.msaaSamples = MSAASamples.None;
            desc.clearBuffer = true;
            desc.name = "_VolumetricLightRT";
            TextureHandle volRT = renderGraph.CreateTexture(desc);

            desc.name = "_VolumetricLightTemp";
            TextureHandle temp = renderGraph.CreateTexture(desc);

            Vector2 texel = new Vector2(1f / desc.width, 1f / desc.height);

            TextureHandle mainShadow = resources.mainShadowsTexture; // 显式引用主光阴影图集

            // Pass 0：体积光 raymarch
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_VolumetricLight RayMarch", out var rmData, m_SamplerRayMarch))
            {
                rmData.material = material;
                rmData.block = new MaterialPropertyBlock();
                rmData.depth = resources.cameraDepthTexture;
                rmData.shadowMap = mainShadow;
                rmData.fogDensity = fogDensity;
                rmData.fogHeightStart = fogHeightStart;
                rmData.fogHeightFalloff = fogHeightFalloff;
                rmData.noiseScale = noiseScale;
                rmData.noiseIntensity = noiseIntensity;
                rmData.noiseScroll = noiseScroll;
                rmData.phaseG = phaseG;
                rmData.shadowStrength = shadowStrength;
                rmData.intensity = intensity;
                rmData.maxDistance = maxDistance;
                rmData.steps = steps;
                rmData.tint = tint;

                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                if (mainShadow.IsValid())
                    builder.UseTexture(mainShadow, AccessFlags.Read);
                builder.SetRenderAttachment(volRT, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetTexture(Shader.PropertyToID("_CameraDepthTexture"), data.depth);
                    if (data.shadowMap.IsValid())
                        block.SetTexture(Shader.PropertyToID("_MainLightShadowmapTexture"), data.shadowMap);
                    block.SetInt(Shader.PropertyToID("_HasShadowMap"), data.shadowMap.IsValid() ? 1 : 0);
                    block.SetFloat(Shader.PropertyToID("_FogDensity"), data.fogDensity);
                    block.SetFloat(Shader.PropertyToID("_FogHeightStart"), data.fogHeightStart);
                    block.SetFloat(Shader.PropertyToID("_FogHeightFalloff"), data.fogHeightFalloff);
                    block.SetFloat(Shader.PropertyToID("_NoiseScale"), data.noiseScale);
                    block.SetFloat(Shader.PropertyToID("_NoiseIntensity"), data.noiseIntensity);
                    block.SetVector(Shader.PropertyToID("_NoiseScroll"), data.noiseScroll);
                    block.SetFloat(Shader.PropertyToID("_PhaseG"), data.phaseG);
                    block.SetFloat(Shader.PropertyToID("_ShadowStrength"), data.shadowStrength);
                    block.SetFloat(Shader.PropertyToID("_Intensity"), data.intensity);
                    block.SetFloat(Shader.PropertyToID("_MaxDistance"), data.maxDistance);
                    block.SetInt(Shader.PropertyToID("_Steps"), data.steps);
                    block.SetColor(Shader.PropertyToID("_Tint"), data.tint);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // Pass 1：垂直模糊 volRT → temp
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_VolumetricLight BlurV", out var blurVData, m_SamplerBlurV))
            {
                blurVData.material = material;
                blurVData.block = new MaterialPropertyBlock();
                blurVData.blurSource = volRT;
                blurVData.blurDir = new Vector2(0f, texel.y);

                builder.UseTexture(volRT, AccessFlags.Read);
                builder.SetRenderAttachment(temp, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetTexture(Shader.PropertyToID("_BlurSource"), data.blurSource);
                    block.SetVector(Shader.PropertyToID("_BlurDir"), data.blurDir);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, data.material, 1, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // Pass 2：水平模糊 temp → volRT
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_VolumetricLight BlurH", out var blurHData, m_SamplerBlurH))
            {
                blurHData.material = material;
                blurHData.block = new MaterialPropertyBlock();
                blurHData.blurSource = temp;
                blurHData.blurDir = new Vector2(texel.x, 0f);

                builder.UseTexture(temp, AccessFlags.Read);
                builder.SetRenderAttachment(volRT, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetTexture(Shader.PropertyToID("_BlurSource"), data.blurSource);
                    block.SetVector(Shader.PropertyToID("_BlurDir"), data.blurDir);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, data.material, 2, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // Pass 3：加性合成回场景颜色（需要读回目标才能混合）
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_VolumetricLight Composite", out var compData, m_SamplerComposite))
            {
                compData.material = material;
                compData.block = new MaterialPropertyBlock();
                compData.volumetricTex = volRT;

                builder.UseTexture(volRT, AccessFlags.Read);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetTexture(Shader.PropertyToID("_VolumetricLightTex"), data.volumetricTex);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, data.material, 3, MeshTopology.Triangles, 3, 1, block);
                });
            }
        }
    }

    VolumetricLightPass m_Pass;

    public override void Create()
    {
        if (settings.volumetricMaterial == null)
        {
            Debug.LogWarning("VolumetricLightFeature: volumetricMaterial is null.");
            return;
        }

        m_Pass = new VolumetricLightPass(settings.volumetricMaterial)
        {
            renderPassEvent = settings.passEvent,
            resolutionScale = settings.resolutionScale,
            steps = settings.steps,
            maxDistance = settings.maxDistance,
            fogDensity = settings.fogDensity,
            fogHeightStart = settings.fogHeightStart,
            fogHeightFalloff = settings.fogHeightFalloff,
            noiseScale = settings.noiseScale,
            noiseIntensity = settings.noiseIntensity,
            noiseScroll = settings.noiseScroll,
            phaseG = settings.phaseG,
            shadowStrength = settings.shadowStrength,
            intensity = settings.intensity,
            tint = settings.tint,
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_Pass == null) return;

        // 需要 _CameraDepthTexture 拷贝供 shader 采样场景深度
        renderingData.cameraData.requiresDepthTexture = true;

        renderer.EnqueuePass(m_Pass);
    }
}
