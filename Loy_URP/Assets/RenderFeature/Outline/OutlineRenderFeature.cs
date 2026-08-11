using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
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
    [SerializeField] private Shader outlineShader;
    private Material material;
    private OutlinePass pass;

    public override void Create()
    {
        Shader shader = outlineShader != null ? outlineShader : Shader.Find("Loy/Feature/Outline");
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

#if URP_COMPATIBILITY_MODE
#pragma warning disable CS0672
    public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
    {
        pass?.Setup(renderer.cameraColorTargetHandle);
    }
#pragma warning restore CS0672
#endif

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(material);
    }

    private sealed class OutlinePass : ScriptableRenderPass
    {
        private const int GBufferNormalSmoothnessIndex = 2;

        private static readonly int OutlineColorId = Shader.PropertyToID("_OutlineColor");
        private static readonly int OutlineParamsId = Shader.PropertyToID("_OutlineParams");
        private static readonly int EdgeParamsId = Shader.PropertyToID("_EdgeParams");
        private static readonly int FadeParamsId = Shader.PropertyToID("_FadeParams");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int MainTexTexelSizeId = Shader.PropertyToID("_MainTex_TexelSize");
        private static readonly int CameraDepthTextureId = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int CameraNormalsTextureId = Shader.PropertyToID("_CameraNormalsTexture");

        private readonly Material material;
        private readonly Settings settings;
        private readonly ProfilingSampler outlineProfilingSampler = new ProfilingSampler("Loy Post Process Outline");

#if URP_COMPATIBILITY_MODE
        private RTHandle temporaryColor;
        private RTHandle source;
#endif

        public OutlinePass(Material material, Settings settings)
        {
            this.material = material;
            this.settings = settings;
#if URP_COMPATIBILITY_MODE
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
#else
            ConfigureInput(ScriptableRenderPassInput.Depth);
#endif
            requiresIntermediateTexture = true;
        }

#if URP_COMPATIBILITY_MODE
        public void Setup(RTHandle cameraColor)
        {
            source = cameraColor;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
            descriptor.depthBufferBits = 0;
            descriptor.msaaSamples = 1;
            RenderingUtils.ReAllocateHandleIfNeeded(ref temporaryColor, descriptor, FilterMode.Bilinear, name: "_OutlineColorTexture");
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (source == null)
                return;

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, outlineProfilingSampler))
            {
                ApplyMaterialParams(material, settings);
                cmd.Blit(source, temporaryColor, material, 0);
                cmd.Blit(temporaryColor, source);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            temporaryColor?.Release();
        }
#endif

        private static void ApplyMaterialParams(Material mat, Settings s)
        {
            mat.SetColor(OutlineColorId, s.outlineColor);
            mat.SetVector(OutlineParamsId, new Vector4(s.thickness, s.opacity, s.edgeThreshold, s.edgeSoftness));
            mat.SetVector(EdgeParamsId, new Vector4(s.depthSensitivity, s.normalSensitivity, s.colorSensitivity, 0f));
            mat.SetVector(FadeParamsId, new Vector4(s.fadeStart, Mathf.Max(s.fadeStart + 0.01f, s.fadeEnd), 0f, 0f));
        }

        private sealed class PassData
        {
            public Material material;
            public MaterialPropertyBlock block;
            public TextureHandle source;
            public TextureHandle depth;
            public TextureHandle normals;
            public Vector4 outlineColor;
            public Vector4 outlineParams;
            public Vector4 edgeParams;
            public Vector4 fadeParams;
            public Vector4 sourceTexelSize;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            if (material == null)
                return;

            // The pass samples neighbouring color pixels, so source and destination must differ.
            // requiresIntermediateTexture keeps activeColor off the backbuffer at this injection point.
            Debug.Assert(!resourcesData.isActiveTargetBackBuffer);
            if (resourcesData.isActiveTargetBackBuffer)
                return;

            TextureHandle[] gBuffer = resourcesData.gBuffer;
            if (gBuffer == null ||
                gBuffer.Length <= GBufferNormalSmoothnessIndex ||
                !gBuffer[GBufferNormalSmoothnessIndex].IsValid())
                return;

            TextureHandle source = resourcesData.activeColorTexture;
            TextureHandle normals = gBuffer[GBufferNormalSmoothnessIndex];
            TextureDesc sourceDesc = renderGraph.GetTextureDesc(source);
            TextureDesc destinationDesc = sourceDesc;
            destinationDesc.depthBufferBits = 0;
            destinationDesc.msaaSamples = MSAASamples.None;
            destinationDesc.clearBuffer = false;
            destinationDesc.name = "_OutlineColor";
            TextureHandle destination = renderGraph.CreateTexture(destinationDesc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Loy_PostProcessOutline", out var passData, outlineProfilingSampler))
            {
                passData.material = material;
                passData.block = new MaterialPropertyBlock();
                passData.source = source;
                passData.depth = resourcesData.cameraDepthTexture;
                passData.normals = normals;
                passData.outlineColor = settings.outlineColor;
                passData.outlineParams = new Vector4(settings.thickness, settings.opacity, settings.edgeThreshold, settings.edgeSoftness);
                passData.edgeParams = new Vector4(settings.depthSensitivity, settings.normalSensitivity, settings.colorSensitivity, 0f);
                passData.fadeParams = new Vector4(settings.fadeStart, Mathf.Max(settings.fadeStart + 0.01f, settings.fadeEnd), 0f, 0f);
                passData.sourceTexelSize = new Vector4(1f / sourceDesc.width, 1f / sourceDesc.height, sourceDesc.width, sourceDesc.height);

                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(passData.depth, AccessFlags.Read);
                builder.UseTexture(passData.normals, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetColor(OutlineColorId, data.outlineColor);
                    block.SetVector(OutlineParamsId, data.outlineParams);
                    block.SetVector(EdgeParamsId, data.edgeParams);
                    block.SetVector(FadeParamsId, data.fadeParams);
                    block.SetVector(MainTexTexelSizeId, data.sourceTexelSize);
                    block.SetTexture(MainTexId, data.source);
                    block.SetTexture(CameraDepthTextureId, data.depth);
                    block.SetTexture(CameraNormalsTextureId, data.normals);
                    rgContext.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, block);
                });
            }

            // Make the ping-pong output the input of every later URP/custom pass.
            resourcesData.cameraColor = destination;
        }
    }
}
