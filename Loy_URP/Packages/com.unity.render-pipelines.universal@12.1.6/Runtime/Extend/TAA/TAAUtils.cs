namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Applies relevant settings before rendering transparent objects
    /// </summary>

    public class TAAUtils
    {
        private const int k_SampleCount = 8;
        public static int sampleIndex { get; private set; }
        public static float HaltonSeq(int index, int radix)
        {
            float result = 0f;
            float fraction = 1f / (float)radix;

            while (index > 0)
            {
                result += (float)(index % radix) * fraction;

                index /= radix;
                fraction /= (float)radix;
            }

            return result;
        }
        
        public static Vector2 GenerateRandomOffset()
        {
            // The variance between 0 and the actual halton sequence values reveals noticeable instability
            // in Unity's shadow maps, so we avoid index 0.
            var offset = new Vector2(
                HaltonSeq((sampleIndex & 1023) + 1, 2),
                HaltonSeq((sampleIndex & 1023) + 1, 3)
            );

            if (++sampleIndex >= k_SampleCount)
                sampleIndex = 0;

            return offset;
        }

        public static void GetJitteredPerspectiveProjectionMatrix(Camera camera, out Vector4 jitterPixels, out Matrix4x4 jitteredMatrix)
        {
            jitterPixels.z = sampleIndex;
            jitterPixels.w = k_SampleCount;
            var v = GenerateRandomOffset();
            
            //像素偏移的范围转换到(-0.5，0.5)   单位：像素
            v.x -= 0.5f;
            v.y -= 0.5f;
            
            jitterPixels.x = v.x;
            jitterPixels.y = v.y;
            //像素偏移转换为 屏幕空间UV的偏移   单位：UV偏移/PerPixel
            var offset = new Vector2(
                jitterPixels.x / camera.pixelWidth,
                jitterPixels.y / camera.pixelHeight
            );
            jitteredMatrix = camera.projectionMatrix;

            //屏幕空间UV的偏移 转换为 裁剪空间的偏移  (-0.5,0.5) *2 ——> (-1,1)
            offset.x *= 2;
            offset.y *= 2;
            
            //
            jitteredMatrix.m02 += offset.x;
            jitteredMatrix.m12 += offset.y;
        }
    }
}
