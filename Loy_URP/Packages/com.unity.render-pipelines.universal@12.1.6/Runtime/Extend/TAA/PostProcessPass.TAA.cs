namespace UnityEngine.Rendering.Universal.Internal
{
    public partial class PostProcessPass
    {
        bool EnsureTAATexture(ref RenderTexture rt)
        {
            if (rt != null && (rt.width != m_Descriptor.width || rt.height != m_Descriptor.height))
            {
                RenderTexture.ReleaseTemporary(rt);
                rt = null;
            }

            if (rt == null)
            {
                var desc = m_Descriptor;
                // OpenglES3不支持可读写的R11G11B10格式
                if (!SystemInfo.usesReversedZBuffer) desc.colorFormat = RenderTextureFormat.ARGBHalf;
                desc.depthBufferBits = 0;
                desc.enableRandomWrite = true;
                rt = RenderTexture.GetTemporary(desc);
                if (!rt.IsCreated()) rt.Create();return true;
            }

            return false;
        }

        RenderTargetIdentifier DoTAA(ref CameraData cameraData, CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier destination)
        {
            bool reset = EnsureTAATexture(ref m_TAAHistory[0]) | EnsureTAATexture(ref m_TAAHistory[1]);
            int indexRead = m_TAAIndexWrite;
            m_TAAIndexWrite = (++m_TAAIndexWrite) % 2;
            var history = m_TAAHistory[indexRead];
            var write = m_TAAHistory[m_TAAIndexWrite];

            cmd.SetGlobalTexture("_InputTexture", source);
            cmd.SetGlobalTexture("_InputHistoryTexture", history);
            var offset = cameraData.GetJitterParams();
            Vector4 p = new Vector4(offset.x / cameraData.pixelWidth, offset.y / cameraData.pixelHeight, reset ? 1 : 0);

            if (cameraData.antialiasingQuality <= AntialiasingQuality.Medium)
            {
                var material = m_Materials.taaPS;
                if (cameraData.antialiasingQuality == AntialiasingQuality.Medium)
                {
                    material.EnableKeyword("_USE_MOTION_VECTOR_BUFFER");
                }
                else
                {
                    material.DisableKeyword("_USE_MOTION_VECTOR_BUFFER");
                }
                cmd.SetRenderTarget(write, destination, 0, CubemapFace.Unknown, 0);
                material.SetVector("_Params", p);
                cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles,3);
            }
            else
            {
                var cs = m_Materials.taaCS;
                cs.SetTexture(0, "_Result", write);
                cs.SetVector("_Params", p);
                cmd.DispatchCompute(cs, 0, (cameraData.pixelWidth - 1) / 8 + 1, (cameraData.pixelHeight - 1) / 8 + 1, 1);
            }
            return write;
        }
    }
}