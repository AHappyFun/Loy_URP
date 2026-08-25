using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

[Serializable]
public class VegetationSettings
{
    public VegetationData data;
    public ComputeShader cullCompute;
    public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingGbuffer;
}

public class VegetationRenderFeature : ScriptableRendererFeature
{
    public VegetationSettings settings = new VegetationSettings();

    VegetationPass pass;

    public override void Create()
    {
        pass = new VegetationPass(settings)
        {
            renderPassEvent = settings.renderPassEvent
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.data == null || settings.cullCompute == null)
            return;

        // 只在 Game 视图和 Scene 视图渲染，跳过 Preview / Reflection 等窗口
        CameraType camType = renderingData.cameraData.cameraType;
        if (camType != CameraType.Game && camType != CameraType.SceneView)
            return;

        renderer.EnqueuePass(pass);
    }

    protected override void Dispose(bool disposing)
    {
        pass?.Dispose();
        pass = null;
    }
}
