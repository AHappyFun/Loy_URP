using System;

namespace UnityEngine.Rendering.Universal
{
    [Serializable, VolumeComponentMenuForRenderPipeline("Post-processing/GAEA/RadialBlur", typeof(UniversalRenderPipeline))]
    public sealed class GAEARadialBlur : VolumeComponent, IPostProcessComponent
    {

        public Vector2Parameter Center = new Vector2Parameter(new Vector2(0.5f, 0.5f));

        public ClampedFloatParameter  BlurRadius = new ClampedFloatParameter(0f, 0f, 1f);

        public ClampedFloatParameter  Iteration = new ClampedFloatParameter(10f, 2f, 30f);

        /// <inheritdoc/>
        public bool IsActive() => BlurRadius.value > 0f;

        /// <inheritdoc/>
        public bool IsTileCompatible() => true;
    }
}
