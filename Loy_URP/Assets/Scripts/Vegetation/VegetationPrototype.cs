using System;
using UnityEngine;

[Serializable]
public class VegetationPrototype
{
    public string name = "Vegetation";
    public Mesh mesh;
    public Material material;

    public float maxDistance = 60f;
    public float minScale = 0.8f;
    public float maxScale = 1.2f;
    public float globalScale = 1f; // 整体缩放乘数（实时生效，影响所有实例）

    public float windStrength = 1f;
    public float windSpeed = 1f;
    public float windFrequency = 0.15f;
    public Vector2 windDirection = new Vector2(1f, 0f); // 风向（XZ 平面）

    public float ambientStrength = 1.5f; // 环境光强度（阴影区尤其明显，调大提亮阴影里的草）

    public Color tintMin = Color.white;
    public Color tintMax = Color.white;
}
