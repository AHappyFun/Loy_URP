using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

public class SSRCombineRenderFeature : ScriptableRendererFeature
{
    public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingSkybox;
    public Shader Shader;

    Material m_Material;
    SSRCombineRenderPass m_RenderPass;

    public override void Create()
    {
        if (Shader != null && m_Material == null)
            m_Material = CoreUtils.CreateEngineMaterial(Shader);

        if (m_RenderPass == null)
            m_RenderPass = new SSRCombineRenderPass(this);
    }

    protected override void Dispose(bool disposing)
    {
        m_RenderPass?.Dispose();
        m_RenderPass = null;
        CoreUtils.Destroy(m_Material);
        m_Material = null;
        base.Dispose(disposing);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_Material != null && m_RenderPass != null)
            renderer.EnqueuePass(m_RenderPass);
    }

    sealed class SSRCombineRenderPass : ScriptableRenderPass
    {
        const string kProfilerTag = "Loy_SSR Combine Pass";
        const int kTemporalResolvePass = 1;
        const int kCompositePass = 2;

        static readonly int kSSRResultId = Shader.PropertyToID("_SSRResultTex");
        static readonly int kSSRHistoryId = Shader.PropertyToID("_SSRHistoryTex");
        static readonly int kSSRResolvedId = Shader.PropertyToID("_SSRResolvedTex");
        static readonly int kMotionVectorsId = Shader.PropertyToID("_MotionVectorTexture");
        static readonly int kCameraDepthId = Shader.PropertyToID("_CameraDepthTexture");
        static readonly int kGBuffer1Id = Shader.PropertyToID("_GBuffer1");
        static readonly int kGBuffer2Id = Shader.PropertyToID("_GBuffer2");
        static readonly int kHistoryValidId = Shader.PropertyToID("_SSRHistoryValid");

        readonly ProfilingSampler m_TemporalSampler = new ProfilingSampler("Loy_SSR Temporal Resolve");
        readonly ProfilingSampler m_CompositeSampler = new ProfilingSampler(kProfilerTag);
        readonly SSRCombineRenderFeature m_RenderFeature;
        readonly Dictionary<int, CameraHistory> m_Histories = new Dictionary<int, CameraHistory>();

        sealed class CameraHistory
        {
            public readonly RTHandle[] buffers = new RTHandle[2];
            public int readIndex;
            public bool valid;
            public int lastFrameUsed = -1;

            public void Release()
            {
                buffers[0]?.Release();
                buffers[1]?.Release();
                buffers[0] = null;
                buffers[1] = null;
                valid = false;
                readIndex = 0;
                lastFrameUsed = -1;
            }
        }

        sealed class TemporalPassData
        {
            public Material material;
            public MaterialPropertyBlock block;
            public TextureHandle current;
            public TextureHandle history;
            public TextureHandle motionVectors;
            public TextureHandle gbuffer1;
            public bool historyValid;
        }

        sealed class CompositePassData
        {
            public Material material;
            public MaterialPropertyBlock block;
            public TextureHandle resolved;
            public TextureHandle gbuffer1;
            public TextureHandle gbuffer2;
            public TextureHandle depth;
        }

        public SSRCombineRenderPass(SSRCombineRenderFeature renderFeature)
        {
            m_RenderFeature = renderFeature;
            renderPassEvent = renderFeature.renderPassEvent;
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Motion);
        }

        public void Dispose()
        {
            foreach (CameraHistory history in m_Histories.Values)
                history.Release();
            m_Histories.Clear();
        }

#if URP_COMPATIBILITY_MODE
#pragma warning disable CS0672
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get(kProfilerTag);
            cmd.DrawProcedural(Matrix4x4.identity, m_RenderFeature.m_Material, kCompositePass, MeshTopology.Triangles, 3, 1);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
#pragma warning restore CS0672
#endif

        CameraHistory GetHistory(Camera camera)
        {
            int cameraId = camera.GetInstanceID();
            if (!m_Histories.TryGetValue(cameraId, out CameraHistory history))
            {
                history = new CameraHistory();
                m_Histories.Add(cameraId, history);
            }
            return history;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_RenderFeature.m_Material == null || !frameData.Contains<SSRFrameData>())
                return;

            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            SSRFrameData ssrData = frameData.Get<SSRFrameData>();
            if (!ssrData.result.IsValid() || cameraData.camera == null)
                return;

            TextureHandle gbuffer1 = default;
            TextureHandle gbuffer2 = default;
            if (resourcesData.gBuffer != null && resourcesData.gBuffer.Length > 2)
            {
                gbuffer1 = resourcesData.gBuffer[1];
                gbuffer2 = resourcesData.gBuffer[2];
            }
            if (!gbuffer1.IsValid() || !gbuffer2.IsValid() || !resourcesData.motionVectorColor.IsValid())
                return;

            CameraHistory cameraHistory = GetHistory(cameraData.camera);
            if (cameraHistory.lastFrameUsed + 1 != Time.frameCount)
                cameraHistory.valid = false;
            RenderTextureDescriptor historyDesc = cameraData.cameraTargetDescriptor;
            historyDesc.depthBufferBits = 0;
            historyDesc.msaaSamples = 1;
            historyDesc.colorFormat = RenderTextureFormat.DefaultHDR;

            bool reallocated = RenderingUtils.ReAllocateHandleIfNeeded(
                ref cameraHistory.buffers[0], historyDesc, FilterMode.Bilinear,
                TextureWrapMode.Clamp, name: "_SSRHistoryA");
            reallocated |= RenderingUtils.ReAllocateHandleIfNeeded(
                ref cameraHistory.buffers[1], historyDesc, FilterMode.Bilinear,
                TextureWrapMode.Clamp, name: "_SSRHistoryB");
            if (reallocated)
            {
                cameraHistory.valid = false;
                cameraHistory.readIndex = 0;
            }

            int writeIndex = 1 - cameraHistory.readIndex;
            TextureHandle previousHistory = renderGraph.ImportTexture(cameraHistory.buffers[cameraHistory.readIndex]);
            TextureHandle nextHistory = renderGraph.ImportTexture(cameraHistory.buffers[writeIndex]);

            TextureDesc resolvedDesc = renderGraph.GetTextureDesc(ssrData.result);
            resolvedDesc.format = GraphicsFormat.R16G16B16A16_SFloat;
            resolvedDesc.clearBuffer = true;
            resolvedDesc.name = "_SSRResolvedTex";
            TextureHandle resolved = renderGraph.CreateTexture(resolvedDesc);

            using (var builder = renderGraph.AddRasterRenderPass<TemporalPassData>(
                       "Loy_SSR Temporal Resolve", out TemporalPassData passData, m_TemporalSampler))
            {
                passData.material = m_RenderFeature.m_Material;
                passData.block = new MaterialPropertyBlock();
                passData.current = ssrData.result;
                passData.history = previousHistory;
                passData.motionVectors = resourcesData.motionVectorColor;
                passData.gbuffer1 = gbuffer1;
                passData.historyValid = cameraHistory.valid;

                builder.UseTexture(ssrData.result, AccessFlags.Read);
                builder.UseTexture(previousHistory, AccessFlags.Read);
                builder.UseTexture(resourcesData.motionVectorColor, AccessFlags.Read);
                builder.UseTexture(gbuffer1, AccessFlags.Read);
                builder.SetRenderAttachment(resolved, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (TemporalPassData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetTexture(kSSRResultId, data.current);
                    block.SetTexture(kSSRHistoryId, data.history);
                    block.SetTexture(kMotionVectorsId, data.motionVectors);
                    block.SetTexture(kGBuffer1Id, data.gbuffer1);
                    block.SetFloat(kHistoryValidId, data.historyValid ? 1.0f : 0.0f);
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.material, kTemporalResolvePass,
                        MeshTopology.Triangles, 3, 1, block);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<CompositePassData>(
                       kProfilerTag, out CompositePassData passData, m_CompositeSampler))
            {
                passData.material = m_RenderFeature.m_Material;
                passData.block = new MaterialPropertyBlock();
                passData.resolved = resolved;
                passData.gbuffer1 = gbuffer1;
                passData.gbuffer2 = gbuffer2;
                passData.depth = resourcesData.cameraDepthTexture;

                builder.UseTexture(resolved, AccessFlags.Read);
                builder.UseTexture(gbuffer1, AccessFlags.Read);
                builder.UseTexture(gbuffer2, AccessFlags.Read);
                builder.UseTexture(resourcesData.cameraDepthTexture, AccessFlags.Read);
                builder.SetRenderAttachment(resourcesData.activeColorTexture, 0, AccessFlags.ReadWrite);

                builder.SetRenderFunc(static (CompositePassData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock block = data.block;
                    block.Clear();
                    block.SetTexture(kSSRResolvedId, data.resolved);
                    block.SetTexture(kGBuffer1Id, data.gbuffer1);
                    block.SetTexture(kGBuffer2Id, data.gbuffer2);
                    block.SetTexture(kCameraDepthId, data.depth);
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.material, kCompositePass,
                        MeshTopology.Triangles, 3, 1, block);
                });
            }

            renderGraph.AddBlitPass(resolved, nextHistory, Vector2.one, Vector2.zero, passName: "SSR Update History");
            cameraHistory.readIndex = writeIndex;
            cameraHistory.valid = true;
            cameraHistory.lastFrameUsed = Time.frameCount;
        }
    }
}
