using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class SSRRenderFeature : ScriptableRendererFeature
{
    public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;

    private SSRRenderPass ssrPass;

    [HideInInspector]
    public SSRConfig m_ssrConfig;

    public ComputeShader SSRComputeShader;
    
    public float SSRMaxRayMarchStep;
    public float SSRMaxRayMarchDistance;
    public float SSRMaxRayMarchStepSize;
    public float SSRDepthThickness;
    
    public override void Create()
    {
        ssrPass = new SSRRenderPass(renderPassEvent, this, SSRComputeShader);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        m_ssrConfig = VolumeManager.instance.stack.GetComponent<SSRConfig>();
        if(!m_ssrConfig.IsActive())
            return;
        renderer.EnqueuePass(ssrPass);
    }
}

public class SSRRenderPass : ScriptableRenderPass
{
    private string m_SSRProfileTag = "ScreenSpaceReflect";
    private ProfilingSampler m_ssrProfile;
    
    private ComputeShader m_ComputeShader;
    
    private int m_SSRKernel;
        
    private RenderTexture m_ssrResult;

    private SSRRenderFeature m_RenderFeature;
    
    public static readonly int st_MaxStepID = Shader.PropertyToID("_SSRMaxRayMarchStep");
    public static readonly int st_MaxDistanceID = Shader.PropertyToID("_SSRMaxRayMarchDistance");
    public static readonly int st_MaxStepSizeID = Shader.PropertyToID("_SSRMaxRayMarchStepSize");
    public static readonly int st_DepthThicknessID = Shader.PropertyToID("_SSRDepthThickness");

    public SSRRenderPass(RenderPassEvent rpe, SSRRenderFeature feature, ComputeShader shader)
    {
        renderPassEvent = rpe;
        m_RenderFeature = feature;
        m_ComputeShader = shader;

        m_SSRKernel = m_ComputeShader.FindKernel("SSRMain");
        m_ssrProfile = new ProfilingSampler(m_SSRProfileTag);
    }

    public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
    {
        
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        CommandBuffer cmd = CommandBufferPool.Get(m_SSRProfileTag);
        
        var camera = renderingData.cameraData.camera;

        if (m_ssrResult == null || m_ssrResult.height != camera.pixelHeight || m_ssrResult.width != camera.pixelWidth)
        {
            if (m_ssrResult != null)
                m_ssrResult.Release();
                
            m_ssrResult = new RenderTexture(camera.pixelWidth, camera.pixelHeight, 0, GraphicsFormat.B10G11R11_UFloatPack32);
            m_ssrResult.name = "_SSR_Result";
            m_ssrResult.useMipMap = false;
            m_ssrResult.autoGenerateMips = false;
            m_ssrResult.enableRandomWrite = true;
            m_ssrResult.wrapMode = TextureWrapMode.Clamp;
            m_ssrResult.filterMode = FilterMode.Point;
            m_ssrResult.Create();
                
            Shader.SetGlobalTexture("_ScreenSpaceReflectMap", m_ssrResult);
        }
        
        using (new ProfilingScope(cmd, m_ssrProfile))
        {
            cmd.SetComputeFloatParam(m_ComputeShader, st_MaxStepID, m_RenderFeature.SSRMaxRayMarchStep);
            cmd.SetComputeFloatParam(m_ComputeShader, st_MaxDistanceID, m_RenderFeature.SSRMaxRayMarchDistance);
            cmd.SetComputeFloatParam(m_ComputeShader, st_MaxStepSizeID, m_RenderFeature.SSRMaxRayMarchStepSize);
            cmd.SetComputeFloatParam(m_ComputeShader, st_DepthThicknessID, m_RenderFeature.SSRDepthThickness);
            
            cmd.SetComputeTextureParam(m_ComputeShader, m_SSRKernel, "_SSRTextureUAV", m_ssrResult);

            cmd.DispatchCompute(m_ComputeShader, m_SSRKernel, Mathf.CeilToInt(camera.pixelWidth / 8), Mathf.CeilToInt(camera.pixelHeight / 8), 1);
        }
        
        CoreUtils.SetKeyword(cmd, "_SSRREFLECT", true);
        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }
    
    public override void FrameCleanup(CommandBuffer cmd)
    {
        CoreUtils.SetKeyword(cmd, "_SSRREFLECT", false);
    }
}
