using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 草地运行时控制（挂在场景里用）。
/// 在 Inspector 里修改参数会实时刷新给 VegetationPass 渲染的草地。
///
/// 原理：把参数同时写进
///   ① VegetationData 的 group.prototype（VegetationPass 每帧用它构造 MaterialPropertyBlock）
///   ② prototype.material 的对应属性（兜底：万一 MPB 没生效，材质上的值也能被 shader 读到）
/// 并递增 group.version 触发渲染侧重建。
/// </summary>
public class GrassRuntimeControl : MonoBehaviour
{
    [Tooltip("VegetationData 资产（渲染器 feature 用的那份）")]
    public VegetationData data;

    [Header("环境光")]
    [Range(0f, 10f)] public float ambientStrength = 2f;

    [Header("颜色")]
    public Color tintMin = Color.white;
    public Color tintMax = Color.white;

    [Header("风")]
    public float windStrength = 1f;
    public float windSpeed = 1f;
    [Range(0f, 2f)] public float windFrequency = 0.15f;
    public Vector2 windDirection = new Vector2(1f, 0f);

    [Header("缩放")]
    public float globalScale = 1f;

    // Inspector 里任何参数变化都触发
    void OnValidate()
    {
        Apply();
    }

    /// <summary>把 Inspector 参数推给草地（组数据 + 材质双写）。</summary>
    [ContextMenu("应用参数到草地")]
    public void Apply()
    {
        if (data == null || data.groups == null || data.groups.Count == 0)
        {
            Debug.LogWarning("[GrassRuntimeControl] 没有指定 VegetationData 或 groups 为空");
            return;
        }

        var group = data.groups[0];
        var p = group.prototype;
        if (p == null)
            return;

        // ① 写 group.prototype（VegetationPass 每帧用它构造 MPB）
        p.ambientStrength = ambientStrength;
        p.tintMin = tintMin;
        p.tintMax = tintMax;
        p.windStrength = windStrength;
        p.windSpeed = windSpeed;
        p.windFrequency = windFrequency;
        p.windDirection = windDirection;
        p.globalScale = globalScale;

        // ② 兜底写材质属性（万一 MPB 未生效，shader 也能读到）
        if (p.material != null)
        {
            p.material.SetFloat("_AmbientStrength", ambientStrength);
            p.material.SetColor("_TintMin", tintMin);
            p.material.SetColor("_TintMax", tintMax);
            p.material.SetFloat("_WindStrength", windStrength);
            p.material.SetFloat("_WindSpeed", windSpeed);
            p.material.SetFloat("_WindFrequency", windFrequency);
            p.material.SetVector("_WindDirection", new Vector4(windDirection.x, 0f, windDirection.y, 0f));
            p.material.SetFloat("_GlobalScale", globalScale);
        }

        // 递增 version，让渲染侧重建对应 GPU buffer
        data.MarkGroupDirty(0);

        // ③ 设置 shader 全局覆盖值（最高优先级，保证实时生效）
        // 草的 GBuffer shader 里 bakedGI = 环境光 * (_GrassAmbientOverride>0 ? 它 : _AmbientStrength)
        Shader.SetGlobalFloat("_GrassAmbientOverride", ambientStrength);

#if UNITY_EDITOR
        EditorUtility.SetDirty(data);
        if (p.material != null)
            EditorUtility.SetDirty(p.material);
#endif
    }

    /// <summary>从 VegetationData 读取当前值到 Inspector（避免覆盖已有设置）。</summary>
    [ContextMenu("从 Data 读取当前值")]
    public void LoadFromData()
    {
        if (data == null || data.groups == null || data.groups.Count == 0)
            return;

        var p = data.groups[0].prototype;
        if (p == null)
            return;

        ambientStrength = p.ambientStrength;
        tintMin = p.tintMin;
        tintMax = p.tintMax;
        windStrength = p.windStrength;
        windSpeed = p.windSpeed;
        windFrequency = p.windFrequency;
        windDirection = p.windDirection;
        globalScale = p.globalScale;
    }
}
