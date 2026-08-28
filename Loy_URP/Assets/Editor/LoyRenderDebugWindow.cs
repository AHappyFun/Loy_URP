using UnityEditor;
using UnityEngine;

internal enum LoyRenderDebugMode
{
    None = 0,
    Albedo = 1,
    Emission = 2,
    GI = 3,
    WorldNormal = 4,
    Smoothness = 5,
    Metallic = 6,
    MaterialAO = 7,
    RealtimeShadow = 8,
    ScreenSpaceAO = 9,
}

[InitializeOnLoad]
internal static class LoyRenderDebugState
{
    internal const string Keyword = "LOY_RENDER_DEBUG";
    internal static readonly int ModeId = Shader.PropertyToID("_LoyRenderDebugMode");

    const string EnabledKey = "Loy.RenderDebug.Enabled";
    const string ModeKey = "Loy.RenderDebug.Mode";

    static LoyRenderDebugState()
    {
        EditorApplication.delayCall += Apply;
    }

    internal static bool Enabled
    {
        get => EditorPrefs.GetBool(EnabledKey, false);
        set
        {
            EditorPrefs.SetBool(EnabledKey, value);
            Apply();
        }
    }

    internal static LoyRenderDebugMode Mode
    {
        get => (LoyRenderDebugMode)EditorPrefs.GetInt(ModeKey, (int)LoyRenderDebugMode.Albedo);
        set
        {
            EditorPrefs.SetInt(ModeKey, (int)value);
            Apply();
        }
    }

    internal static void Apply()
    {
        bool enabled = Enabled && Mode != LoyRenderDebugMode.None;
        if (enabled)
            Shader.EnableKeyword(Keyword);
        else
            Shader.DisableKeyword(Keyword);

        Shader.SetGlobalInteger(ModeId, enabled ? (int)Mode : 0);
        SceneView.RepaintAll();
        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
    }
}

internal sealed class LoyRenderDebugWindow : EditorWindow
{
    static readonly GUIContent[] ModeLabels =
    {
        new GUIContent("Albedo"),
        new GUIContent("Emission"),
        new GUIContent("GI"),
        new GUIContent("World Normal"),
        new GUIContent("Smoothness"),
        new GUIContent("Metallic"),
        new GUIContent("Material AO"),
        new GUIContent("Realtime Shadow"),
        new GUIContent("Screen Space AO"),
    };

    [MenuItem("Tools/Loy/Rendering Debug")]
    static void Open()
    {
        GetWindow<LoyRenderDebugWindow>("Rendering Debug");
    }

    void OnEnable()
    {
        minSize = new Vector2(330f, 285f);
        LoyRenderDebugState.Apply();
    }

    void OnGUI()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Loy Rendering Debug", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "开启后使用一套 Debug Shader 变体，视图类型由全局整数动态切换。会同时影响 Scene View 和 Game View。",
            MessageType.Info);

        EditorGUI.BeginChangeCheck();
        bool enabled = EditorGUILayout.ToggleLeft("Enable Rendering Debug", LoyRenderDebugState.Enabled);
        if (EditorGUI.EndChangeCheck())
            LoyRenderDebugState.Enabled = enabled;

        EditorGUILayout.Space(6f);
        using (new EditorGUI.DisabledScope(!enabled))
        {
            int selected = Mathf.Max(0, (int)LoyRenderDebugState.Mode - 1);
            EditorGUI.BeginChangeCheck();
            selected = GUILayout.SelectionGrid(selected, ModeLabels, 2, EditorStyles.miniButton);
            if (EditorGUI.EndChangeCheck())
                LoyRenderDebugState.Mode = (LoyRenderDebugMode)(selected + 1);
        }

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField(
            $"_LoyRenderDebugMode = {(LoyRenderDebugState.Enabled ? (int)LoyRenderDebugState.Mode : 0)}",
            EditorStyles.miniLabel);
        EditorGUILayout.HelpBox(
            "支持：Loy/DeferredLit、Loy/ToonLit、Loy/TerrainDeferredLit、Loy/VegetationGrass、Loy/WaterTransparent。\n" +
            "GI 显示材质 BRDF 处理后的间接光；Material AO 来自材质贴图，Screen Space AO 来自 URP SSAO。",
            MessageType.None);
    }
}
