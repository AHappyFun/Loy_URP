using System;
using System.Diagnostics;

namespace UnityEngine.Rendering.Universal
{
    [Serializable, VolumeComponentMenu("ALeyCustom/SSR")]
    public class SSRConfig : VolumeComponent, IPostProcessComponent
    {
        public BoolParameter enable = new BoolParameter(false, true);
        
        public ClampedIntParameter MaxRayStep = new ClampedIntParameter(100, 0, 200);

        public ClampedFloatParameter MaxRayDistance = new ClampedFloatParameter(200, 0, 300);
        
        public ClampedFloatParameter StepSize = new ClampedFloatParameter(0.001f, 0, 0.2f);

        public ClampedFloatParameter DepthThickness = new ClampedFloatParameter(1, 0, 2);
        
        public bool IsActive()
        {
            return enable.value && MaxRayStep.value > 0 && MaxRayDistance.value > 0 && StepSize.value > 0;
        }
        
        public bool IsTileCompatible()
        {
            return false;
        }
    }
}