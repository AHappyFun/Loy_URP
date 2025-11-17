using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;

public class HBAORenderFeature : ScriptableRendererFeature
{
    class HBAOPass : ScriptableRenderPass
    {
        Material hbaoMaterial;
        RenderTargetHandle hbaoHandle;
        private RenderTargetIdentifier hbaoTex;

        RenderTargetHandle tempHanle;
        private RenderTargetIdentifier tempTex;

        public float AOIntensity = 1.0f;
        public float Radius = 1.0f;
        public float Bias = 0.02f;
        public int NumDirs = 8;
        public int NumSteps = 12;
        public float StepScale = 1.4f;

        public HBAOPass(Material mat)
        {
            hbaoMaterial = mat;
            hbaoHandle.Init("_HBAOResultTex");
            tempHanle.Init("_TempTex");
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            var desc = cameraTextureDescriptor;

            cmd.GetTemporaryRT(hbaoHandle.id, desc.width , desc.height, 0 ,FilterMode.Bilinear, RenderTextureFormat.R8);
            cmd.GetTemporaryRT(tempHanle.id, desc.width , desc.height, 0 ,FilterMode.Bilinear, RenderTextureFormat.R8);

            hbaoTex = new RenderTargetIdentifier(hbaoHandle.id);
            tempTex = new RenderTargetIdentifier(tempHanle.id);

            ConfigureTarget(hbaoTex);
            ConfigureClear(ClearFlag.All, Color.clear);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (hbaoMaterial == null) return;

            var cmd = CommandBufferPool.Get("Loy_HBAO Compute Pass");

            //set params
            hbaoMaterial.SetFloat("_AOIntensity", AOIntensity);
            hbaoMaterial.SetFloat("_Radius", Radius);
            hbaoMaterial.SetFloat("_Bias", Bias);
            hbaoMaterial.SetInt("_NumDirs", NumDirs);
            hbaoMaterial.SetInt("_NumSteps", NumSteps);
            hbaoMaterial.SetFloat("_StepScale", StepScale);

            cmd.DrawProcedural(Matrix4x4.identity, hbaoMaterial, 0, MeshTopology.Triangles, 3, 1);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            cmd.Blit(hbaoTex, tempTex, hbaoMaterial, 1);
            cmd.Blit(tempTex, hbaoTex, hbaoMaterial, 2);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            cmd.SetGlobalTexture(hbaoHandle.id, hbaoTex);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            cmd.ReleaseTemporaryRT(tempHanle.id);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

    }

    [System.Serializable]
    public class HBAOSettings
    {
        public Material hbaoMaterial = null;
        public RenderPassEvent passEvent = RenderPassEvent.AfterRenderingGbuffer;
        public float AOIntensity = 1.0f;
        public float Radius = 1.0f;
        public float Bias = 0.02f;
        public int NumDirs = 8;
        public int NumSteps = 12;
        public float StepScale = 1.4f;


    }

    public HBAOSettings settings = new HBAOSettings();
    HBAOPass m_HBAOPass;

    public override void Create()
    {
        if (settings.hbaoMaterial == null)
        {
            Debug.LogWarning("SSRFeature: ssrMaterial is null.");
            return;
        }

        m_HBAOPass = new HBAOPass(settings.hbaoMaterial)
        {
            renderPassEvent = settings.passEvent,
            AOIntensity = settings.AOIntensity,
            Radius = settings.Radius,
            Bias = settings.Bias,
            NumDirs = settings.NumDirs,
            NumSteps = settings.NumSteps,
            StepScale = settings.StepScale
        };
    }

    // Inject the pass
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_HBAOPass == null) return;

        renderer.EnqueuePass(m_HBAOPass);
    }
}
