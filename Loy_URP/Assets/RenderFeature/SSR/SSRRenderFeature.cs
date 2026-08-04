using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class SSRRenderFeature : ScriptableRendererFeature
{
    class SSRPass : ScriptableRenderPass
    {
      //  static readonly int kSSRTexture = Shader.PropertyToID("_SSRResultTex");
        //static readonly int kTemp = Shader.PropertyToID("_SSRTemp");

        Material ssrMaterial;
        //RenderTargetIdentifier cameraColor;
        RTHandle ssrHandle;
        RTHandle ssrHistoryHandle;

        // settings
        public int maxSteps = 64;
        public float stepSize = 0.5f;
        //public float thickness = 0.1f;
        //public int binarySearchSteps = 3;
        public RenderPassEvent passEventToUse = RenderPassEvent.AfterRenderingDeferredLights;

        public SSRPass(Material mat)
        {
            ssrMaterial = mat;
        }

        public void Setup()
        {
            //this.cameraColor = color;
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            var desc = cameraTextureDescriptor;
            desc.depthBufferBits = 0;
            desc.colorFormat = RenderTextureFormat.DefaultHDR;
            RenderingUtils.ReAllocateHandleIfNeeded(ref ssrHandle, desc, FilterMode.Bilinear, name: "_SSRResultTex");
            RenderingUtils.ReAllocateHandleIfNeeded(ref ssrHistoryHandle, desc, FilterMode.Bilinear, name: "_SSRHistoryTex");

            ConfigureTarget(ssrHandle);
            ConfigureClear(ClearFlag.All, Color.clear);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (ssrMaterial == null) return;

            var cmd = CommandBufferPool.Get("Loy_SSR Compute Pass");

            // pass parameters
            Camera cam = renderingData.cameraData.camera;
            Matrix4x4 proj = renderingData.cameraData.GetGPUProjectionMatrix();
            Matrix4x4 invProj = proj.inverse;
            Matrix4x4 view = renderingData.cameraData.GetViewMatrix();
            Matrix4x4 invView = view.inverse;

            ssrMaterial.SetMatrix("_CameraProjection", proj);
            ssrMaterial.SetMatrix("_CameraInvProjection", invProj);
            ssrMaterial.SetMatrix("_CameraView", view);
            ssrMaterial.SetMatrix("_CameraInvView", invView);
            ssrMaterial.SetVector("_WorldSpaceViewForward", cam.transform.forward);

            ssrMaterial.SetInt("_SSRMaxSteps", maxSteps);
            ssrMaterial.SetFloat("_SSRStepSize", stepSize);
            //ssrMaterial.SetFloat("_SSRThickness", thickness);
            //ssrMaterial.SetInt("_SSRBinarySearch", binarySearchSteps);

            ssrMaterial.SetInt("_Frame", Time.frameCount % 1024);


            cmd.DrawProcedural(Matrix4x4.identity, ssrMaterial, 0, MeshTopology.Triangles, 3, 1);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();


            cmd.SetGlobalTexture(ssrHandle.name, ssrHandle.nameID);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            cmd.Blit(ssrHandle, ssrHistoryHandle);
            cmd.SetGlobalTexture(ssrHistoryHandle.name, ssrHistoryHandle.nameID);


            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            // 只释放当前帧结果缓冲；历史缓冲跨帧持久保留，以维持时序重投影
            ssrHandle?.Release();
        }
    }

    [System.Serializable]
    public class SSRSettings
    {
        public Material ssrMaterial = null;
        public RenderPassEvent passEvent = RenderPassEvent.AfterRenderingDeferredLights;
        public int maxSteps = 64;
        public float stepSize = 0.5f;

    }

    public SSRSettings settings = new SSRSettings();
    SSRPass m_SSRPass;

    public override void Create()
    {
        if (settings.ssrMaterial == null)
        {
            Debug.LogWarning("SSRFeature: ssrMaterial is null.");
            return;
        }

        m_SSRPass = new SSRPass(settings.ssrMaterial)
        {
            renderPassEvent = settings.passEvent,
            maxSteps = settings.maxSteps,
            stepSize = settings.stepSize,
            //thickness = settings.thickness,
            //binarySearchSteps = settings.binarySearch
        };
    }

    // Inject the pass
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_SSRPass == null) return;
        // camera color RT
        //var cameraColor = renderer.cameraColorTarget;
        m_SSRPass.Setup();
        renderer.EnqueuePass(m_SSRPass);
    }
}