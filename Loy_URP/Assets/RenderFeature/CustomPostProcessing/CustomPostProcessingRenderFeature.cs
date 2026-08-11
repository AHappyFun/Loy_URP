using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// One URP renderer feature for the project's custom post-processing stack.
/// Add future effects to CreateEffectRenderers; active effects are chained in registration order.
/// </summary>
[DisallowMultipleRendererFeature]
public sealed class CustomPostProcessingRenderFeature : ScriptableRendererFeature
{
    [Serializable]
    public sealed class Settings
    {
        public RenderPassEvent injectionPoint = RenderPassEvent.BeforeRenderingPostProcessing;
        public bool affectSceneView = true;

        public OutlineSettings outline = new OutlineSettings();
        public StreakSettings streak = new StreakSettings();
    }

    [Serializable]
    public sealed class OutlineSettings
    {
        public bool enabled = true;
        public Color outlineColor = Color.black;
        [Range(0f, 1f)] public float opacity = 0.85f;
        [Range(0.5f, 5f)] public float thickness = 1.25f;

        [Header("Edge Detection")]
        [Range(0f, 5f)] public float depthSensitivity = 1.25f;
        [Range(0f, 5f)] public float normalSensitivity = 1.6f;
        [Range(0f, 5f)] public float colorSensitivity = 0.15f;
        [Range(0f, 1f)] public float edgeThreshold = 0.12f;
        [Range(0.001f, 0.5f)] public float edgeSoftness = 0.08f;

        [Header("Distance Control")]
        [Min(0f)] public float fadeStart = 35f;
        [Min(0.01f)] public float fadeEnd = 100f;
    }

    [Serializable]
    public sealed class StreakSettings
    {
        public bool enabled = true;
        [Range(0f, 5f)] public float threshold = 0.8f;
        [Range(0f, 1f)] public float stretch = 0.75f;
        [Range(0f, 1f)] public float intensity = 0.35f;
        [ColorUsage(false, true)] public Color tint = new Color(0.55f, 0.55f, 1f, 1f);

        [Tooltip("Maximum horizontal pyramid levels. The actual count also depends on camera width.")]
        [Range(2, 16)] public int maxPyramidLevels = 16;
    }

    [SerializeField] private Settings settings = new Settings();
    [SerializeField] private Shader outlineShader;
    [SerializeField] private Shader streakShader;

    private CustomPostProcessingPass pass;
    private readonly List<ICustomPostProcessRenderer> effectRenderers = new List<ICustomPostProcessRenderer>();

    public override void Create()
    {
        DisposeEffectRenderers();

        // Register new custom post effects here. Their order is the execution order.
        Shader outline = outlineShader != null ? outlineShader : Shader.Find("Loy/Feature/Outline");
        if (outline != null)
            effectRenderers.Add(new OutlineRenderer(outline, settings.outline));
        else
            Debug.LogWarning("CustomPostProcessingRenderFeature: Outline shader was not found.");

        Shader streak = streakShader != null ? streakShader : Shader.Find("Hidden/Loy/PostProcessing/Streak");
        if (streak != null)
            effectRenderers.Add(new StreakRenderer(streak, settings.streak));
        else
            Debug.LogWarning("CustomPostProcessingRenderFeature: Streak shader was not found.");

        pass = new CustomPostProcessingPass(effectRenderers)
        {
            renderPassEvent = settings.injectionPoint
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (pass == null)
            return;

        CameraData cameraData = renderingData.cameraData;
        if (cameraData.cameraType == CameraType.Preview ||
            cameraData.cameraType == CameraType.Reflection ||
            (!settings.affectSceneView && cameraData.isSceneViewCamera) ||
            !pass.HasActiveEffects())
            return;

        renderer.EnqueuePass(pass);
    }

    protected override void Dispose(bool disposing)
    {
        pass = null;
        DisposeEffectRenderers();
        base.Dispose(disposing);
    }

    private void DisposeEffectRenderers()
    {
        foreach (ICustomPostProcessRenderer renderer in effectRenderers)
            renderer.Dispose();
        effectRenderers.Clear();
    }

    private interface ICustomPostProcessRenderer : IDisposable
    {
        bool IsActive();
        TextureHandle Record(RenderGraph renderGraph, ContextContainer frameData, TextureHandle source);
    }

    private sealed class CustomPostProcessingPass : ScriptableRenderPass
    {
        private readonly IReadOnlyList<ICustomPostProcessRenderer> renderers;

        public CustomPostProcessingPass(IReadOnlyList<ICustomPostProcessRenderer> renderers)
        {
            this.renderers = renderers;
            requiresIntermediateTexture = true;
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public bool HasActiveEffects()
        {
            foreach (ICustomPostProcessRenderer renderer in renderers)
            {
                if (renderer.IsActive())
                    return true;
            }

            return false;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (resourceData.isActiveTargetBackBuffer)
                return;

            TextureHandle source = resourceData.activeColorTexture;
            bool renderedAnyEffect = false;

            foreach (ICustomPostProcessRenderer renderer in renderers)
            {
                if (!renderer.IsActive())
                    continue;

                source = renderer.Record(renderGraph, frameData, source);
                renderedAnyEffect = true;
            }

            // Later URP and custom passes consume the last effect's output.
            if (renderedAnyEffect)
                resourceData.cameraColor = source;
        }
    }

    private sealed class OutlineRenderer : ICustomPostProcessRenderer
    {
        private const int GBufferNormalSmoothnessIndex = 2;

        private static readonly int MainTextureId = Shader.PropertyToID("_MainTex");
        private static readonly int MainTextureTexelSizeId = Shader.PropertyToID("_MainTex_TexelSize");
        private static readonly int CameraDepthTextureId = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int CameraNormalsTextureId = Shader.PropertyToID("_CameraNormalsTexture");
        private static readonly int OutlineColorId = Shader.PropertyToID("_OutlineColor");
        private static readonly int OutlineParamsId = Shader.PropertyToID("_OutlineParams");
        private static readonly int EdgeParamsId = Shader.PropertyToID("_EdgeParams");
        private static readonly int FadeParamsId = Shader.PropertyToID("_FadeParams");

        private readonly Material material;
        private readonly OutlineSettings settings;
        private readonly ProfilingSampler profilingSampler =
            new ProfilingSampler("Loy Custom Post Process - Outline");

        public OutlineRenderer(Shader shader, OutlineSettings settings)
        {
            material = CoreUtils.CreateEngineMaterial(shader);
            this.settings = settings;
        }

        public bool IsActive() => material != null && settings.enabled && settings.opacity > 0f;

        public TextureHandle Record(RenderGraph renderGraph, ContextContainer frameData, TextureHandle source)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            TextureHandle[] gBuffer = resourceData.gBuffer;

            if (!IsActive() ||
                gBuffer == null || gBuffer.Length <= GBufferNormalSmoothnessIndex ||
                !gBuffer[GBufferNormalSmoothnessIndex].IsValid() ||
                !resourceData.cameraDepthTexture.IsValid())
                return source;

            TextureHandle normals = gBuffer[GBufferNormalSmoothnessIndex];
            TextureHandle depth = resourceData.cameraDepthTexture;
            TextureDesc sourceDesc = renderGraph.GetTextureDesc(source);
            TextureDesc destinationDesc = sourceDesc;
            destinationDesc.depthBufferBits = 0;
            destinationDesc.msaaSamples = MSAASamples.None;
            destinationDesc.clearBuffer = false;
            destinationDesc.name = "_LoyCustomPostOutlineColor";
            TextureHandle destination = renderGraph.CreateTexture(destinationDesc);

            using (var builder = renderGraph.AddRasterRenderPass<OutlinePassData>(
                       "Loy Custom Post Process - Outline", out OutlinePassData passData, profilingSampler))
            {
                passData.material = material;
                passData.block = new MaterialPropertyBlock();
                passData.source = source;
                passData.depth = depth;
                passData.normals = normals;
                passData.outlineColor = settings.outlineColor;
                passData.outlineParams = new Vector4(
                    settings.thickness, settings.opacity,
                    settings.edgeThreshold, settings.edgeSoftness);
                passData.edgeParams = new Vector4(
                    settings.depthSensitivity, settings.normalSensitivity,
                    settings.colorSensitivity, 0f);
                passData.fadeParams = new Vector4(
                    settings.fadeStart,
                    Mathf.Max(settings.fadeStart + 0.01f, settings.fadeEnd), 0f, 0f);
                passData.sourceTexelSize = TexelSize(sourceDesc);

                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(depth, AccessFlags.Read);
                builder.UseTexture(normals, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (OutlinePassData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetTexture(MainTextureId, data.source);
                    block.SetVector(MainTextureTexelSizeId, data.sourceTexelSize);
                    block.SetTexture(CameraDepthTextureId, data.depth);
                    block.SetTexture(CameraNormalsTextureId, data.normals);
                    block.SetColor(OutlineColorId, data.outlineColor);
                    block.SetVector(OutlineParamsId, data.outlineParams);
                    block.SetVector(EdgeParamsId, data.edgeParams);
                    block.SetVector(FadeParamsId, data.fadeParams);
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0,
                        MeshTopology.Triangles, 3, 1, block);
                });
            }

            return destination;
        }

        public void Dispose()
        {
            CoreUtils.Destroy(material);
        }

        private static Vector4 TexelSize(TextureDesc desc)
        {
            float width = Mathf.Max(1, desc.width);
            float height = Mathf.Max(1, desc.height);
            return new Vector4(1f / width, 1f / height, width, height);
        }

        private sealed class OutlinePassData
        {
            public Material material;
            public MaterialPropertyBlock block;
            public TextureHandle source;
            public TextureHandle depth;
            public TextureHandle normals;
            public Color outlineColor;
            public Vector4 outlineParams;
            public Vector4 edgeParams;
            public Vector4 fadeParams;
            public Vector4 sourceTexelSize;
        }
    }

    private sealed class StreakRenderer : ICustomPostProcessRenderer
    {
        private const int PrefilterPass = 0;
        private const int DownsamplePass = 1;
        private const int UpsamplePass = 2;
        private const int CompositionPass = 3;

        private static readonly int SourceTextureId = Shader.PropertyToID("_SourceTexture");
        private static readonly int SourceTexelSizeId = Shader.PropertyToID("_SourceTexture_TexelSize");
        private static readonly int InputTextureId = Shader.PropertyToID("_InputTexture");
        private static readonly int InputTexelSizeId = Shader.PropertyToID("_InputTexture_TexelSize");
        private static readonly int HighTextureId = Shader.PropertyToID("_HighTexture");
        private static readonly int ThresholdId = Shader.PropertyToID("_Threshold");
        private static readonly int StretchId = Shader.PropertyToID("_Stretch");
        private static readonly int IntensityId = Shader.PropertyToID("_Intensity");
        private static readonly int TintId = Shader.PropertyToID("_Tint");

        private readonly Material material;
        private readonly StreakSettings settings;
        private readonly ProfilingSampler profilingSampler = new ProfilingSampler("Loy Custom Post Process - Streak");

        public StreakRenderer(Shader shader, StreakSettings settings)
        {
            material = CoreUtils.CreateEngineMaterial(shader);
            this.settings = settings;
        }

        public bool IsActive() => material != null && settings.enabled && settings.intensity > 0f;

        public TextureHandle Record(RenderGraph renderGraph, ContextContainer frameData, TextureHandle source)
        {
            if (!IsActive())
                return source;

            TextureDesc sourceDesc = renderGraph.GetTextureDesc(source);
            int levelCount = CalculateLevelCount(sourceDesc.width, settings.maxPyramidLevels);

            TextureHandle[] down = new TextureHandle[levelCount];
            TextureHandle[] up = new TextureHandle[levelCount];

            TextureDesc pyramidDesc = sourceDesc;
            pyramidDesc.width = Mathf.Max(1, sourceDesc.width);
            pyramidDesc.height = Mathf.Max(1, sourceDesc.height / 2);
            pyramidDesc.depthBufferBits = 0;
            pyramidDesc.msaaSamples = MSAASamples.None;
            pyramidDesc.format = GraphicsFormat.R16G16B16A16_SFloat;
            pyramidDesc.clearBuffer = false;
            pyramidDesc.name = "_LoyStreakDown0";
            down[0] = renderGraph.CreateTexture(pyramidDesc);

            renderGraph.BeginProfilingSampler(profilingSampler);

            AddPass(renderGraph, "Loy Streak Prefilter", PrefilterPass, source, default, default, down[0],
                TexelSize(sourceDesc), default);

            for (int level = 1; level < levelCount; level++)
            {
                TextureDesc inputDesc = renderGraph.GetTextureDesc(down[level - 1]);
                pyramidDesc.width = Mathf.Max(1, inputDesc.width / 2);
                pyramidDesc.name = "_LoyStreakDown" + level;
                down[level] = renderGraph.CreateTexture(pyramidDesc);

                AddPass(renderGraph, "Loy Streak Downsample " + level, DownsamplePass,
                    default, down[level - 1], default, down[level], default, TexelSize(inputDesc));
            }

            TextureHandle streakTexture = down[levelCount - 1];
            for (int level = levelCount - 2; level >= 1; level--)
            {
                TextureDesc lowDesc = renderGraph.GetTextureDesc(streakTexture);
                TextureDesc upDesc = renderGraph.GetTextureDesc(down[level]);
                upDesc.depthBufferBits = 0;
                upDesc.msaaSamples = MSAASamples.None;
                upDesc.clearBuffer = false;
                upDesc.name = "_LoyStreakUp" + level;
                up[level] = renderGraph.CreateTexture(upDesc);

                AddPass(renderGraph, "Loy Streak Upsample " + level, UpsamplePass,
                    default, streakTexture, down[level], up[level], default, TexelSize(lowDesc));
                streakTexture = up[level];
            }

            TextureDesc outputDesc = sourceDesc;
            outputDesc.depthBufferBits = 0;
            outputDesc.msaaSamples = MSAASamples.None;
            outputDesc.clearBuffer = false;
            outputDesc.name = "_LoyStreakColor";
            TextureHandle output = renderGraph.CreateTexture(outputDesc);
            TextureDesc streakDesc = renderGraph.GetTextureDesc(streakTexture);

            AddPass(renderGraph, "Loy Streak Composition", CompositionPass,
                source, streakTexture, default, output, TexelSize(sourceDesc), TexelSize(streakDesc));

            renderGraph.EndProfilingSampler(profilingSampler);

            return output;
        }

        public void Dispose()
        {
            CoreUtils.Destroy(material);
        }

        private static int CalculateLevelCount(int width, int maximum)
        {
            int count = 1;
            int currentWidth = Mathf.Max(1, width);
            int maxLevels = Mathf.Clamp(maximum, 2, 16);

            while (count < maxLevels && currentWidth / 2 >= 4)
            {
                currentWidth /= 2;
                count++;
            }

            return count;
        }

        private static Vector4 TexelSize(TextureDesc desc)
        {
            float width = Mathf.Max(1, desc.width);
            float height = Mathf.Max(1, desc.height);
            return new Vector4(1f / width, 1f / height, width, height);
        }

        private void AddPass(
            RenderGraph renderGraph,
            string passName,
            int shaderPass,
            TextureHandle source,
            TextureHandle input,
            TextureHandle high,
            TextureHandle destination,
            Vector4 sourceTexelSize,
            Vector4 inputTexelSize)
        {
            using (var builder = renderGraph.AddRasterRenderPass<StreakPassData>(
                       passName, out StreakPassData passData))
            {
                passData.material = material;
                passData.block = new MaterialPropertyBlock();
                passData.shaderPass = shaderPass;
                passData.source = source;
                passData.input = input;
                passData.high = high;
                passData.sourceTexelSize = sourceTexelSize;
                passData.inputTexelSize = inputTexelSize;
                passData.threshold = settings.threshold;
                passData.stretch = settings.stretch;
                passData.intensity = settings.intensity;
                passData.tint = settings.tint;

                if (source.IsValid())
                    builder.UseTexture(source, AccessFlags.Read);
                if (input.IsValid())
                    builder.UseTexture(input, AccessFlags.Read);
                if (high.IsValid())
                    builder.UseTexture(high, AccessFlags.Read);

                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (StreakPassData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();

                    if (data.source.IsValid())
                    {
                        block.SetTexture(SourceTextureId, data.source);
                        block.SetVector(SourceTexelSizeId, data.sourceTexelSize);
                    }

                    if (data.input.IsValid())
                    {
                        block.SetTexture(InputTextureId, data.input);
                        block.SetVector(InputTexelSizeId, data.inputTexelSize);
                    }

                    if (data.high.IsValid())
                        block.SetTexture(HighTextureId, data.high);

                    block.SetFloat(ThresholdId, data.threshold);
                    block.SetFloat(StretchId, data.stretch);
                    block.SetFloat(IntensityId, data.intensity);
                    block.SetColor(TintId, data.tint);

                    context.cmd.DrawProcedural(Matrix4x4.identity, data.material, data.shaderPass,
                        MeshTopology.Triangles, 3, 1, block);
                });
            }
        }

        private sealed class StreakPassData
        {
            public Material material;
            public MaterialPropertyBlock block;
            public int shaderPass;
            public TextureHandle source;
            public TextureHandle input;
            public TextureHandle high;
            public Vector4 sourceTexelSize;
            public Vector4 inputTexelSize;
            public float threshold;
            public float stretch;
            public float intensity;
            public Color tint;
        }
    }
}
