using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;


    [Serializable, VolumeComponentMenu("ALeyCustom/ContactShadow")]
    public class ContactShadows : VolumeComponent, IPostProcessComponent
    {
        /// <summary>
        /// When enabled, HDRP processes Contact Shadows for this Volume.
        public BoolParameter enable = new BoolParameter(false, true);
        /// <summary>
        /// Controls the length of the rays HDRP uses to calculate Contact Shadows. It is in meters, but it gets scaled by a factor depending on Distance Scale Factor
        /// and the depth of the point from where the contact shadow ray is traced.
        /// </summary>
        public ClampedFloatParameter length = new ClampedFloatParameter(0.15f, 0f, 1f);
        /// <summary>
        /// Controls the opacity of the contact shadows.
        /// </summary>
        public ClampedFloatParameter opacity = new ClampedFloatParameter(1f, 0f, 1f);
        /// <summary>
        /// Scales the length of the contact shadow ray based on the linear depth value at the origin of the ray.
        /// </summary>
        //public ClampedFloatParameter distanceScaleFactor = new ClampedFloatParameter(0.5f, 0f, 1f);
        /// <summary>
        /// The distance from the camera, in meters, at which HDRP begins to fade out Contact Shadows.
        /// </summary>
        public ClampedFloatParameter maxDistance = new ClampedFloatParameter(20f, 0f, 100f);
        /// <summary>
        /// The distance from the camera, in meters, at which HDRP begins to fade in Contact Shadows.
        /// </summary>
        public ClampedFloatParameter minDistance = new ClampedFloatParameter(0f, 0f, 20f);
        /// <summary>
        /// The distance, in meters, over which HDRP fades Contact Shadows out when past the Max Distance.
        /// </summary>
        public ClampedFloatParameter fadeOutDistance = new ClampedFloatParameter(15f, 0f, 100f);
        /// <summary>
        /// The distance, in meters, over which HDRP fades Contact Shadows in when past the Min Distance.
        /// </summary>
        public ClampedFloatParameter fadeInDistance = new ClampedFloatParameter(1f, 0f, 20f);
        /// <summary>
        /// Controls the bias applied to the screen space ray cast to get contact shadows.
        /// </summary>
        public ClampedFloatParameter rayBias = new ClampedFloatParameter(0.2f, 0f, 1f);
        /// <summary>
        /// Controls the thickness of the objects found along the ray, essentially thickening the contact shadows.
        /// </summary>
        public ClampedFloatParameter thicknessScale = new ClampedFloatParameter(0.15f, 0.02f, 1f);
        /// <summary>
        /// Controls the numbers of samples taken during the ray-marching process for shadows. Increasing this might lead to higher quality at the expenses of performance.
        /// </summary>
        public ClampedIntParameter sampleCount = new ClampedIntParameter(8, 8, 16);

        public bool IsActive()
        {
            return enable.value && length.value > 0;
        }

        public bool IsTileCompatible()
        {
            return false;
        }
    }

