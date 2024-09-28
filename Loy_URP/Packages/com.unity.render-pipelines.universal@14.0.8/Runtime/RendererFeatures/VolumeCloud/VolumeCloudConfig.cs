using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEngine.Rendering.Universal
{

    [Serializable, VolumeComponentMenu("ALeyCustom/VolumeCloud")]
    public class VolumeCloudConfig : VolumeComponent, IPostProcessComponent
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