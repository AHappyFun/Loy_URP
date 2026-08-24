using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class VegetationScatterWindow : EditorWindow
{
    Terrain terrain;
    VegetationData data;
    int groupIndex;

    int scatterCount = 2000;
    float minScale = 0.8f;
    float maxScale = 1.2f;
    float radius = 0.5f;

    bool useLayerMask = true;
    int layerIndex = 0;
    float layerWeightThreshold = 0.1f;

    [MenuItem("Tools/Vegetation/Scatter Window")]
    static void Open()
    {
        GetWindow<VegetationScatterWindow>("Vegetation Scatter");
    }

    void OnGUI()
    {
        terrain = (Terrain)EditorGUILayout.ObjectField("Terrain", terrain, typeof(Terrain), true);
        data = (VegetationData)EditorGUILayout.ObjectField("Vegetation Data", data, typeof(VegetationData), false);

        if (data == null || data.groups.Count == 0)
        {
            EditorGUILayout.HelpBox("Assign a VegetationData asset with at least one group.", MessageType.Info);
            return;
        }

        var names = new string[data.groups.Count];
        for (int i = 0; i < names.Length; i++)
            names[i] = string.IsNullOrEmpty(data.groups[i].prototype.name) ? $"Group {i}" : data.groups[i].prototype.name;
        groupIndex = EditorGUILayout.Popup("Group", Mathf.Clamp(groupIndex, 0, names.Length - 1), names);

        EditorGUILayout.Space();
        scatterCount = EditorGUILayout.IntField("Scatter Count", scatterCount);
        minScale = EditorGUILayout.FloatField("Min Scale", minScale);
        maxScale = EditorGUILayout.FloatField("Max Scale", maxScale);
        radius = EditorGUILayout.FloatField("Instance Radius", radius);

        EditorGUILayout.Space();
        useLayerMask = EditorGUILayout.Toggle("Use Terrain Layer Mask", useLayerMask);
        using (new EditorGUI.DisabledScope(!useLayerMask))
        {
            layerIndex = EditorGUILayout.IntField("Layer Index", layerIndex);
            layerWeightThreshold = EditorGUILayout.Slider("Min Layer Weight", layerWeightThreshold, 0f, 1f);
        }

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(terrain == null))
        {
            if (GUILayout.Button("Scatter"))
                Scatter();
        }

        if (GUILayout.Button("Clear Group"))
            ClearGroup();
    }

    void Scatter()
    {
        var group = data.groups[groupIndex];
        var terrainData = terrain.terrainData;
        Vector3 origin = terrain.transform.position;
        float sizeX = terrainData.size.x;
        float sizeZ = terrainData.size.z;

        float[,,] alphamaps = null;
        int alphaW = 0, alphaH = 0, layerCount = 0;
        if (useLayerMask)
        {
            alphamaps = terrainData.GetAlphamaps(0, 0, terrainData.alphamapWidth, terrainData.alphamapHeight);
            alphaW = terrainData.alphamapWidth;
            alphaH = terrainData.alphamapHeight;
            layerCount = terrainData.alphamapLayers;
        }

        var newInstances = new List<VegetationInstanceData>(scatterCount);
        int maxAttempts = scatterCount * (useLayerMask ? 50 : 1);
        int attempts = 0;

        while (newInstances.Count < scatterCount && attempts < maxAttempts)
        {
            attempts++;

            float nx = Random.value;
            float nz = Random.value;

            if (useLayerMask && layerIndex >= 0 && layerIndex < layerCount)
            {
                int ax = Mathf.Clamp(Mathf.FloorToInt(nx * alphaW), 0, alphaW - 1);
                int az = Mathf.Clamp(Mathf.FloorToInt(nz * alphaH), 0, alphaH - 1);
                float weight = alphamaps[az, ax, layerIndex];

                if (weight < layerWeightThreshold)
                    continue;
                if (Random.value > weight)
                    continue;
            }

            float worldX = origin.x + nx * sizeX;
            float worldZ = origin.z + nz * sizeZ;
            float height = terrainData.GetInterpolatedHeight(nx, nz) + origin.y;

            newInstances.Add(new VegetationInstanceData
            {
                position = new Vector3(worldX, height, worldZ),
                scale = Random.Range(minScale, maxScale),
                rotationY = Random.Range(0f, Mathf.PI * 2f),
                seed = (uint)Random.Range(0, int.MaxValue),
                radius = radius
            });
        }

        Undo.RecordObject(data, "Scatter Vegetation");
        group.instances.AddRange(newInstances);
        data.MarkGroupDirty(groupIndex);
        EditorUtility.SetDirty(data);
        AssetDatabase.SaveAssets();

        Debug.Log($"Vegetation scatter: placed {newInstances.Count}/{scatterCount} after {attempts} attempts.");
    }

    void ClearGroup()
    {
        var group = data.groups[groupIndex];
        Undo.RecordObject(data, "Clear Vegetation Group");
        group.instances.Clear();
        data.MarkGroupDirty(groupIndex);
        EditorUtility.SetDirty(data);
        AssetDatabase.SaveAssets();
    }
}
