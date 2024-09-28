using System;
using System.Diagnostics;

namespace UnityEngine.Rendering.Universal
{
    [Serializable, VolumeComponentMenu("ALeyCustom/SSAO")]
    public class SSAO : VolumeComponent, IPostProcessComponent
    {  
        public ClampedFloatParameter Radius = new ClampedFloatParameter(0.3f, 0.01f, 5f);
        
        public ClampedFloatParameter Intensity = new ClampedFloatParameter(0f, 0, 5f);

        public ClampedIntParameter Samples = new ClampedIntParameter(5, 3, 20);
        
        [HideInInspector]
        public ClampedFloatParameter DirectLightStrength = new ClampedFloatParameter(0.2f, 0f, 1f);
        
        [HideInInspector]
        public BoolParameter AfterOpaque = new BoolParameter(false, true);
        
        public BoolParameter DownSample = new BoolParameter(true, true);
        
        public bool IsActive()
        {
            return  Intensity.value > 0 ;
        }
        
        public bool IsTileCompatible()
        {
            return false;
        }
    }
}