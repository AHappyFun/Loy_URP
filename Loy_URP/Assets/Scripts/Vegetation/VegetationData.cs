using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class VegetationGroup
{
    public VegetationPrototype prototype = new VegetationPrototype();
    public List<VegetationInstanceData> instances = new List<VegetationInstanceData>();

    // Bumped whenever `instances` changes so the render feature knows to re-upload the GPU buffer.
    [NonSerialized] public int version;
}

// Editor/CPU-side representation of a VegetationInstance (kept separate from the
// GPU struct so we don't couple serialization to GPU memory layout).
[Serializable]
public struct VegetationInstanceData
{
    public Vector3 position;
    public float scale;
    public float rotationY;
    public uint seed;
    public float radius;
}

[CreateAssetMenu(fileName = "VegetationData", menuName = "Loy/Vegetation Data")]
public class VegetationData : ScriptableObject
{
    public List<VegetationGroup> groups = new List<VegetationGroup>();

    public void MarkGroupDirty(int groupIndex)
    {
        if (groupIndex >= 0 && groupIndex < groups.Count)
            groups[groupIndex].version++;
    }

    // Inspector 里任何修改（实例 scale/位置、prototype 参数等）都会触发，
    // 递增 version 让渲染侧重建对应 GPU buffer。
    void OnValidate()
    {
        foreach (var g in groups)
            if (g != null)
                g.version++;
    }
}
