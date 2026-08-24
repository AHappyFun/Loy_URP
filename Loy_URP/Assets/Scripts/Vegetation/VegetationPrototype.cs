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

    public float windStrength = 1f;
    public float windSpeed = 1f;

    public Color tintMin = Color.white;
    public Color tintMax = Color.white;
}
