using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[DisallowMultipleRendererFeature]
public sealed class OutlineRenderFeature : ScriptableRendererFeature
{
    [Serializable]
    public sealed class Settings
    {
        public RenderPassEvent passEvent = RenderPassEvent.BeforeRenderingPostProcessing;

        [Header("Outline")]
        public Color outlineColor = new Color(0.035f, 0.055f, 0.075f, 1f);
        [Range(0f, 1f)] public float opacity = 0.85f;
        [Range(0.5f, 5f)] public float thickness = 1.25f;

        [Header("Edge detection")]
        [Range(0f, 5f)] public float depthSensitivity = 1.25f;
        [Range(0f, 5f)] public float normalSensitivity = 1.6f;
        [Range(0f, 5f)] public float colorSensitivity = 0.15f;
        [Range(0f, 1f)] public float edgeThreshold = 0.12f;
        [Range(0.001f, 0.5f)] public float edgeSoftness = 0.08f;

        [Header("Distance control")]
        [Min(0f)] public float fadeStart = 35f;
        [Min(0.01f)] public float fadeEnd = 100f;

        [Header("Cameras")]
        public bool affectSceneView = true;
    }

    [SerializeField] private Settings settings = new Settings();
    private Material material;
    private OutlinePass pass;

    public override void Create()
    {
        Shader shader = Shader.Find("Loy/Feature/Outline");
        if (shader == null)
        {
            Debug.LogWarning("OutlineRenderFeature: shader 'Loy/Feature/Outline' was not found.");
            return;
        }

        if (material == null || material.shader != shader)
        {
            CoreUtils.Destroy(material);
            material = CoreUtils.CreateEngineMaterial(shader);
        }

        pass = new OutlinePass(material, settings)
        {
            renderPassEvent = settings.passEvent
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (pass == null || material == null || settings.opacity <= 0f)
            return;

        CameraData cameraData = renderingData.cameraData;
        if (cameraData.cameraType == CameraType.Preview ||
            cameraData.cameraType == CameraType.Reflection ||
            (!settings.affectSceneView && cameraData.isSceneViewCamera))
            return;

        renderer.EnqueuePass(pass);
    }

    public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
    {
        pass?.Setup(renderer.cameraColorTargetHandle);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(material);
    }

    private sealed class OutlinePass : ScriptableRenderPass
    {
        private static readonly int OutlineColorId = Shader.PropertyToID("_OutlineColor");
        private static readonly int OutlineParamsId = Shader.PropertyToID("_OutlineParams");
        private static readonly int EdgeParamsId = Shader.PropertyToID("_EdgeParams");
        private static readonly int FadeParamsId = Shader.PropertyToID("_FadeParams");

        private readonly Material material;
        private readonly Settings settings;
        private readonly ProfilingSampler outlineProfilingSampler = new ProfilingSampler("Loy Post Process Outline");
        private readonly RenderTargetHandle temporaryColor;
        private RTHandle source;

        public OutlinePass(Material material, Settings settings)
        {
            this.material = material;
            this.settings = settings;
            temporaryColor.Init("_OutlineColorTexture");
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
        }

        public void Setup(RTHandle cameraColor)
        {
            source = cameraColor;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
            descriptor.depthBufferBits = 0;
            descriptor.msaaSamples = 1;
            cmd.GetTemporaryRT(temporaryColor.id, descriptor, FilterMode.Bilinear);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (source == null)
                return;

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, outlineProfilingSampler))
            {
                material.SetColor(OutlineColorId, settings.outlineColor);
                material.SetVector(OutlineParamsId, new Vector4(
                    settings.thickness, settings.opacity, settings.edgeThreshold, settings.edgeSoftness));
                material.SetVector(EdgeParamsId, new Vector4(
                    settings.depthSensitivity, settings.normalSensitivity, settings.colorSensitivity, 0f));
                material.SetVector(FadeParamsId, new Vector4(
                    settings.fadeStart, Mathf.Max(settings.fadeStart + 0.01f, settings.fadeEnd), 0f, 0f));

                cmd.Blit(source, temporaryColor.Identifier(), material, 0);
                cmd.Blit(temporaryColor.Identifier(), source);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(temporaryColor.id);
        }
    }
}
