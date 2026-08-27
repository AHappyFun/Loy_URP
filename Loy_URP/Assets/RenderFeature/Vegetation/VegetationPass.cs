using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class VegetationPass : ScriptableRenderPass, IDisposable
{
    const string ProfilerTag = "Loy_Vegetation";

    static readonly int kInstances = Shader.PropertyToID("_Instances");
    static readonly int kVisible = Shader.PropertyToID("_Visible");
    static readonly int kInstanceCount = Shader.PropertyToID("_InstanceCount");
    static readonly int kMaxDistance = Shader.PropertyToID("_MaxDistance");
    static readonly int kCamPos = Shader.PropertyToID("_CamPos");
    static readonly int kFrustumPlanes = Shader.PropertyToID("_FrustumPlanes");
    static readonly int kTintMin = Shader.PropertyToID("_TintMin");
    static readonly int kTintMax = Shader.PropertyToID("_TintMax");
    static readonly int kWindStrength = Shader.PropertyToID("_WindStrength");
    static readonly int kWindSpeed = Shader.PropertyToID("_WindSpeed");
    static readonly int kWindFrequency = Shader.PropertyToID("_WindFrequency");
    static readonly int kWindDirection = Shader.PropertyToID("_WindDirection");
    static readonly int kGlobalScale = Shader.PropertyToID("_GlobalScale");
    static readonly int kAmbientStrength = Shader.PropertyToID("_AmbientStrength");

    readonly VegetationSettings settings;
    readonly ProfilingSampler m_ProfilingSamplerGroup;
    readonly ProfilingSampler m_ProfilingSamplerCull;
    readonly ProfilingSampler m_ProfilingSamplerDraw;
    int cullKernel = -1;

    class GroupGpuState
    {
        public GraphicsBuffer instanceBuffer;
        public GraphicsBuffer visibleBuffer;
        public GraphicsBuffer argsBuffer;
        public Mesh mesh;
        public int cachedVersion = -1;
        public int cachedCount;

        public void Dispose()
        {
            instanceBuffer?.Dispose();
            visibleBuffer?.Dispose();
            argsBuffer?.Dispose();
            instanceBuffer = null;
            visibleBuffer = null;
            argsBuffer = null;
        }
    }

    readonly List<GroupGpuState> groupStates = new List<GroupGpuState>();
    readonly Vector4[] frustumPlanesScratch = new Vector4[6];

    public VegetationPass(VegetationSettings settings)
    {
        this.settings = settings;
        m_ProfilingSamplerGroup = new ProfilingSampler(ProfilerTag);
        m_ProfilingSamplerCull = new ProfilingSampler(ProfilerTag + " Cull");
        m_ProfilingSamplerDraw = new ProfilingSampler(ProfilerTag + " Draw");
    }

    void EnsureKernel()
    {
        if (cullKernel < 0 && settings.cullCompute != null)
            cullKernel = settings.cullCompute.FindKernel("KCull");
    }

    // Keeps `groupStates` in sync with `settings.data.groups`, (re)allocating and
    // re-uploading GPU buffers only when a group's instance list actually changed.
    void SyncGroups()
    {
        var groups = settings.data.groups;

        while (groupStates.Count < groups.Count)
            groupStates.Add(new GroupGpuState());
        while (groupStates.Count > groups.Count)
        {
            groupStates[groupStates.Count - 1].Dispose();
            groupStates.RemoveAt(groupStates.Count - 1);
        }

        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var state = groupStates[i];

            if (state.mesh == null)
                state.mesh = group.prototype.mesh != null ? group.prototype.mesh : VegetationMeshUtility.CreateCrossQuad();

            int count = group.instances.Count;
            bool needsRebuild = state.instanceBuffer == null || state.cachedVersion != group.version || state.cachedCount != count;
            if (!needsRebuild)
                continue;

            state.Dispose();
            state.cachedVersion = group.version;
            state.cachedCount = count;

            if (count == 0)
                continue;

            state.instanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, VegetationInstance.Stride);
            var gpuData = new VegetationInstance[count];
            for (int j = 0; j < count; j++)
            {
                var src = group.instances[j];
                gpuData[j] = new VegetationInstance
                {
                    positionX = src.position.x,
                    positionY = src.position.y,
                    positionZ = src.position.z,
                    scale = src.scale,
                    rotationY = src.rotationY,
                    seed = src.seed,
                    radius = src.radius,
                    pad = 0f
                };
            }
            state.instanceBuffer.SetData(gpuData);

            state.visibleBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Append, count, sizeof(float) * 8);

            state.argsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, sizeof(uint) * 5);
            var args = new uint[5];
            args[0] = state.mesh.GetIndexCount(0);
            args[1] = 0;
            args[2] = state.mesh.GetIndexStart(0);
            args[3] = state.mesh.GetBaseVertex(0);
            args[4] = 0;
            state.argsBuffer.SetData(args);
        }
    }

    void ComputeFrustumPlanes(Camera camera)
    {
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
        for (int i = 0; i < 6; i++)
            frustumPlanesScratch[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
    }

    void DispatchCull(ComputeCommandBuffer cmd, Camera camera)
    {
        EnsureKernel();
        if (cullKernel < 0)
            return;

        SyncGroups();
        ComputeFrustumPlanes(camera);

        var cs = settings.cullCompute;
        var groups = settings.data.groups;
        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var state = groupStates[i];
            if (state.instanceBuffer == null || state.cachedCount == 0 || group.prototype.material == null)
                continue;

            cmd.SetBufferCounterValue(state.visibleBuffer, 0);

            cmd.SetComputeBufferParam(cs, cullKernel, kInstances, state.instanceBuffer);
            cmd.SetComputeBufferParam(cs, cullKernel, kVisible, state.visibleBuffer);
            cmd.SetComputeIntParam(cs, kInstanceCount, state.cachedCount);
            cmd.SetComputeFloatParam(cs, kMaxDistance, group.prototype.maxDistance);
            cmd.SetComputeVectorParam(cs, kCamPos, camera.transform.position);
            cmd.SetComputeVectorArrayParam(cs, kFrustumPlanes, frustumPlanesScratch);

            int groupsX = Mathf.CeilToInt(state.cachedCount / 64f);
            cmd.DispatchCompute(cs, cullKernel, groupsX, 1, 1);

            cmd.CopyCounterValue(state.visibleBuffer, state.argsBuffer, sizeof(uint));
        }
    }

    void DrawIndirect(RasterCommandBuffer cmd, bool useGBuffer)
    {
        string passName = useGBuffer ? "GBuffer" : "ForwardLit";

        var groups = settings.data.groups;
        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var state = groupStates[i];
            if (state.instanceBuffer == null || state.cachedCount == 0 || group.prototype.material == null)
                continue;

            var mpb = new MaterialPropertyBlock();
            mpb.SetBuffer(kVisible, state.visibleBuffer);
            mpb.SetColor(kTintMin, group.prototype.tintMin);
            mpb.SetColor(kTintMax, group.prototype.tintMax);
            mpb.SetFloat(kWindStrength, group.prototype.windStrength);
            mpb.SetFloat(kWindSpeed, group.prototype.windSpeed);
            mpb.SetFloat(kWindFrequency, group.prototype.windFrequency);
            mpb.SetVector(kWindDirection, new Vector4(group.prototype.windDirection.x, 0f, group.prototype.windDirection.y, 0f));
            mpb.SetFloat(kGlobalScale, group.prototype.globalScale);
            mpb.SetFloat(kAmbientStrength, group.prototype.ambientStrength);

            int shaderPass = group.prototype.material.FindPass(passName);
            if (shaderPass < 0)
                shaderPass = 0;

            cmd.DrawMeshInstancedIndirect(state.mesh, 0, group.prototype.material, shaderPass, state.argsBuffer, 0, mpb);
        }
    }

#if URP_COMPATIBILITY_MODE
#pragma warning disable CS0672
    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (settings.data == null || settings.cullCompute == null)
            return;

        var cmd = CommandBufferPool.Get(ProfilerTag);
        using (new ProfilingScope(cmd, m_ProfilingSamplerGroup))
        {
            DispatchCull(CommandBufferHelpers.GetComputeCommandBuffer(cmd), renderingData.cameraData.camera);
            // Compatibility mode has no RenderGraph GBuffer handles to bind into, so
            // it always falls back to the manual-lighting Forward pass.
            DrawIndirect(CommandBufferHelpers.GetRasterCommandBuffer(cmd), useGBuffer: false);
        }
        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }
#pragma warning restore CS0672
#endif

    class CullPassData
    {
        public VegetationPass pass;
        public Camera camera;
    }

    class DrawPassData
    {
        public VegetationPass pass;
        public bool useGBuffer;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        if (settings.data == null || settings.cullCompute == null)
            return;

        UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

        // 外层分组：Frame Debugger 里 "Loy_Vegetation" 下嵌套 Cull / Draw
        renderGraph.BeginProfilingSampler(m_ProfilingSamplerGroup);

        using (var builder = renderGraph.AddComputePass<CullPassData>(ProfilerTag + " Cull", out var cullData, m_ProfilingSamplerCull))
        {
            cullData.pass = this;
            cullData.camera = cameraData.camera;
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((CullPassData data, ComputeGraphContext ctx) =>
            {
                data.pass.DispatchCull(ctx.cmd, data.camera);
            });
        }

        using (var builder = renderGraph.AddRasterRenderPass<DrawPassData>(ProfilerTag, out var drawData, m_ProfilingSamplerDraw))
        {
            drawData.pass = this;

            // Deferred renderer: write straight into the same GBuffer MRT the built-in
            // GBuffer pass uses, so the renderer's deferred lighting pass (shadows,
            // additional lights) resolves grass exactly like every other opaque.
            // Falls back to a manual-lighting Forward draw if no GBuffer exists
            // (e.g. a Forward renderer).
            var gbuffer = resourcesData.gBuffer;
            bool hasGBuffer = gbuffer != null && gbuffer.Length > 0 && gbuffer[0].IsValid();
            drawData.useGBuffer = hasGBuffer;

            if (hasGBuffer)
            {
                for (int i = 0; i < gbuffer.Length; i++)
                {
                    if (gbuffer[i].IsValid())
                        builder.SetRenderAttachment(gbuffer[i], i, AccessFlags.Write);
                }
                builder.SetRenderAttachmentDepth(resourcesData.activeDepthTexture, AccessFlags.ReadWrite);
            }
            else
            {
                builder.SetRenderAttachmentDepth(resourcesData.activeDepthTexture, AccessFlags.ReadWrite);
                builder.SetRenderAttachment(resourcesData.activeColorTexture, 0, AccessFlags.Write);
            }
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((DrawPassData data, RasterGraphContext ctx) =>
            {
                data.pass.DrawIndirect(ctx.cmd, data.useGBuffer);
            });
        }

        renderGraph.EndProfilingSampler(m_ProfilingSamplerGroup);
    }

    public void Dispose()
    {
        foreach (var state in groupStates)
            state.Dispose();
        groupStates.Clear();
    }
}
