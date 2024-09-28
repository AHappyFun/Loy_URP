using System;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using ProfilingScope = UnityEngine.Rendering.ProfilingScope;


    public class ContactShadowFeature : ScriptableRendererFeature
    {
        private ContactShadowMapPass m_ContactShadowMapPass;
        
        public RenderPassEvent m_RenderPassEvent = RenderPassEvent.AfterRenderingPrePasses;

        [HideInInspector]
        public ContactShadows m_ContactShadows;
        
        public ComputeShader ContactShadowComputeShader;
        public override void Create()
        {
            if (m_ContactShadowMapPass == null)
            {
                m_ContactShadowMapPass = new ContactShadowMapPass(m_RenderPassEvent, this, ContactShadowComputeShader);
            }
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            m_ContactShadows = VolumeManager.instance.stack.GetComponent<ContactShadows>();
            if (!m_ContactShadows.IsActive()) 
                return;
            renderer.EnqueuePass(m_ContactShadowMapPass);
        }
    }

    public class ContactShadowMapPass : ScriptableRenderPass
    {
        private string m_ContactShadowMapProfileTag = "Loy_ContactShadowMap";
        private ProfilingSampler m_ContactShadowMapProfile;
        
        private ComputeShader m_ContactShadowComputeShader;
        private int m_DeferredContactShadowKernel;
        
        private RenderTexture m_ContactShadowMap;

        private ContactShadowFeature m_RenderFeature;

        public static readonly int st_ContactShadowParamsParametersID = Shader.PropertyToID("_ContactShadowParamsParameters");
        public static readonly int st_ContactShadowParamsParameters2ID = Shader.PropertyToID("_ContactShadowParamsParameters2");
        public static readonly int st_ContactShadowParamsParameters3ID = Shader.PropertyToID("_ContactShadowParamsParameters3");
        public static readonly int st_ContactShadowTextureUAVID = Shader.PropertyToID("_ContactShadowTextureUAV");

        public ContactShadowMapPass(RenderPassEvent rpe, ContactShadowFeature feature, ComputeShader shader)
        {
            renderPassEvent = rpe;
            m_RenderFeature = feature;
            m_ContactShadowComputeShader = shader;
            //m_DeferredContactShadowKernel = m_ContactShadowComputeShader.FindKernel("TestMap");
            m_DeferredContactShadowKernel = m_ContactShadowComputeShader.FindKernel("ContactShadowMap");
            m_ContactShadowMapProfile = new ProfilingSampler(m_ContactShadowMapProfileTag);
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get(m_ContactShadowMapProfileTag);
            if (!m_RenderFeature.m_ContactShadows.enable.value)
            {
                CoreUtils.SetKeyword(cmd, "_CONTACT_SHADOW", false);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
                return;
            }
            
            var camera = renderingData.cameraData.camera;

            if (m_ContactShadowMap == null || m_ContactShadowMap.height != camera.pixelHeight || m_ContactShadowMap.width != camera.pixelWidth)
            {
                if (m_ContactShadowMap != null)
                    m_ContactShadowMap.Release();
                
                m_ContactShadowMap = new RenderTexture(camera.pixelWidth, camera.pixelHeight, 0, GraphicsFormat.R8_UNorm);
                m_ContactShadowMap.name = "contactShadowMap";
                m_ContactShadowMap.useMipMap = false;
                m_ContactShadowMap.autoGenerateMips = false;
                m_ContactShadowMap.enableRandomWrite = true;
                m_ContactShadowMap.wrapMode = TextureWrapMode.Clamp;
                m_ContactShadowMap.filterMode = FilterMode.Point;
                m_ContactShadowMap.Create();
                
                Shader.SetGlobalTexture("_ContactShadowMap", m_ContactShadowMap);
            }
            
            float contactShadowRange = Mathf.Clamp(m_RenderFeature.m_ContactShadows.fadeOutDistance.value, 0.0f, m_RenderFeature.m_ContactShadows.maxDistance.value);
            float contactShadowFadeEnd = m_RenderFeature.m_ContactShadows.maxDistance.value;
            //float contactShadowOneOverFadeRange = 1.0f / Mathf.Max(1e-6f, contactShadowRange);

            float contactShadowMinDist = Mathf.Min(m_RenderFeature.m_ContactShadows.minDistance.value, contactShadowFadeEnd);
            float contactShadowFadeIn = Mathf.Clamp(m_RenderFeature.m_ContactShadows.fadeInDistance.value, 1e-6f, contactShadowFadeEnd);
            
            var params1 = new Vector4(m_RenderFeature.m_ContactShadows.length.value, 0, contactShadowFadeEnd, contactShadowRange);
            var params2 = new Vector4(camera.pixelHeight, contactShadowMinDist, contactShadowFadeIn, m_RenderFeature.m_ContactShadows.rayBias.value * 0.01f);
            var params3 = new Vector4(m_RenderFeature.m_ContactShadows.sampleCount.value, m_RenderFeature.m_ContactShadows.thicknessScale.value * 10.0f, Time.renderedFrameCount%8, 0.0f);
            

            using (new ProfilingScope(cmd, m_ContactShadowMapProfile))
            {
                cmd.SetComputeVectorParam(m_ContactShadowComputeShader, st_ContactShadowParamsParametersID, params1);
                cmd.SetComputeVectorParam(m_ContactShadowComputeShader, st_ContactShadowParamsParameters2ID, params2);
                cmd.SetComputeVectorParam(m_ContactShadowComputeShader, st_ContactShadowParamsParameters3ID, params3);
                cmd.SetComputeTextureParam(m_ContactShadowComputeShader, m_DeferredContactShadowKernel, st_ContactShadowTextureUAVID, m_ContactShadowMap);
                
                cmd.DispatchCompute(m_ContactShadowComputeShader, m_DeferredContactShadowKernel, Mathf.CeilToInt(camera.pixelWidth / 8), Mathf.CeilToInt(camera.pixelHeight / 8), 1);
            }
            cmd.SetGlobalFloat("_ContactOpacity", m_RenderFeature.m_ContactShadows.opacity.value);
            CoreUtils.SetKeyword(cmd, "_CONTACT_SHADOW", true);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
        
        public override void FrameCleanup(CommandBuffer cmd)
        {
            CoreUtils.SetKeyword(cmd, "_CONTACT_SHADOW", false);
        }
    }

