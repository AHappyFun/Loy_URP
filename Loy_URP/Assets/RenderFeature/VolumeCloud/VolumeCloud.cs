using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class VolumeCloud : MonoBehaviour
{
    public Vector3 Size = new Vector3(100, 50, 100);
    public float density = 1.0f;
    public float noiseScale = 0.01f;
    public Texture3D noiseTex; // 3D Perlin / Worley


}
