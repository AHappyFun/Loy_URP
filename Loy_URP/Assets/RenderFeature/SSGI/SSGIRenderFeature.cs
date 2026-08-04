using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;

public class SSGIRenderFeature : ScriptableRendererFeature
{
    class SSGIPass : ScriptableRenderPass
    {
        Material ssgiMaterial;

        RTHandle ssgiHandle;
        RTHandle tempHanle;

        public int NumDir = 8;
        public float MaxRayDistance = 200;
        public int NumSteps = 30;

        public bool isHalfSize = true;
        public float DepthBias = 0.1f;

        public SSGIPass(Material mat)
        {
            ssgiMaterial = mat;
        }
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            var desc = cameraTextureDescriptor;

            float scale = isHalfSize ? 0.5f : 1.0f;

            desc.width = (int)(desc.width * scale);
            desc.height = (int)(desc.height * scale);
            desc.depthBufferBits = 0;

            RenderingUtils.ReAllocateHandleIfNeeded(ref ssgiHandle, desc, FilterMode.Bilinear, name: "_SSGIResultTex");
            RenderingUtils.ReAllocateHandleIfNeeded(ref tempHanle, desc, FilterMode.Bilinear, name: "_TempTex");

            ConfigureTarget(ssgiHandle);
            ConfigureClear(ClearFlag.All, Color.clear);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (ssgiMaterial == null) return;

            var cmd = CommandBufferPool.Get("Loy_SSGI Compute Pass");

            //set params
           ssgiMaterial.SetFloat("_NumDirs", NumDir);
           ssgiMaterial.SetFloat("_MaxRayDistance", MaxRayDistance);
           ssgiMaterial.SetInt("_NumSteps", NumSteps);
           ssgiMaterial.SetFloat("_DepthBias", DepthBias);
           ssgiMaterial.SetFloat("_GITexRes", isHalfSize ? 0.5f : 1.0f);

            cmd.DrawProcedural(Matrix4x4.identity, ssgiMaterial, 0, MeshTopology.Triangles, 3, 1);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            cmd.Blit(ssgiHandle, tempHanle, ssgiMaterial, 1);
            cmd.Blit(tempHanle, ssgiHandle, ssgiMaterial, 2);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            cmd.SetGlobalTexture(ssgiHandle.name, ssgiHandle.nameID);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            ssgiHandle?.Release();
            tempHanle?.Release();
        }
    }

    [System.Serializable]
    public class SSGISettngs
    {
        public Material ssgiMaterial = null;
        public RenderPassEvent passEvent = RenderPassEvent.AfterRenderingGbuffer;
        [Range(4, 16)]
        public int NumDir = 8;

        public float MaxRayDistance = 200;

        public int NumSteps = 30;


        public bool isHalfSize = true;

        public float DepthBias = 0.1f;
    }

    public SSGISettngs settings = new SSGISettngs();
    SSGIPass m_ssgiPass;

    public override void Create()
    {
        if (settings.ssgiMaterial == null)
        {
            Debug.LogWarning("SSGIFeature: ssgiMaterial is null.");
            return;
        }

        m_ssgiPass = new SSGIPass(settings.ssgiMaterial)
        {
            renderPassEvent = settings.passEvent,
            NumDir = settings.NumDir,
            MaxRayDistance = settings.MaxRayDistance,
            NumSteps = settings.NumSteps,
            isHalfSize = settings.isHalfSize,
            DepthBias = settings.DepthBias

        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_ssgiPass == null) return;

        renderer.EnqueuePass(m_ssgiPass);
    }
}
