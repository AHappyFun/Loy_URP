using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct VegetationInstance
{
    public const int Stride = sizeof(float) * 8; // 7 floats + 1 uint, 32 bytes total

    public float positionX;
    public float positionY;
    public float positionZ;
    public float scale;
    public float rotationY;
    public uint seed;
    public float radius;
    public float pad;
}
