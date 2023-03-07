using System;
using System.Diagnostics;

namespace UnityEngine.Rendering.Universal
{
    public class SSRConfig : VolumeComponent, IPostProcessComponent
    {
        public bool IsActive()
        {
            return true;
        }
        
        public bool IsTileCompatible()
        {
            return false;
        }
    }
}