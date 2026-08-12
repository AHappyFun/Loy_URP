using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

[CustomEditor(typeof(CustomPostProcessingRenderFeature))]
internal sealed class CustomPostProcessingRenderFeatureEditor : Editor
{
    private SerializedProperty settings;
    private SerializedProperty effectOrder;
    private ReorderableList orderList;

    private void OnEnable()
    {
        settings = serializedObject.FindProperty("settings");
        effectOrder = settings.FindPropertyRelative("effectOrder");

        serializedObject.Update();
        EnsureCompleteOrder();
        serializedObject.ApplyModifiedPropertiesWithoutUndo();

        orderList = new ReorderableList(serializedObject, effectOrder, true, true, false, false)
        {
            drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Effect Order (drag to reorder)"),
            drawElementCallback = DrawEffectElement,
            elementHeight = EditorGUIUtility.singleLineHeight + 4f
        };
        orderList.onReorderCallback = _ => ApplyAndRecreate();
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(settings.FindPropertyRelative("injectionPoint"));
        EditorGUILayout.PropertyField(settings.FindPropertyRelative("affectSceneView"));

        EditorGUILayout.Space();
        orderList.DoLayoutList();

        EditorGUILayout.Space();
        DrawEffectSettingsInExecutionOrder();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Shaders", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("outlineShader"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("streakShader"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("glitchShader"));

        if (serializedObject.ApplyModifiedProperties())
            ApplyAndRecreate();
    }

    private void DrawEffectElement(Rect rect, int index, bool isActive, bool isFocused)
    {
        SerializedProperty element = effectOrder.GetArrayElementAtIndex(index);
        rect.y += 2f;
        rect.height = EditorGUIUtility.singleLineHeight;
        string label = ((CustomPostProcessingRenderFeature.EffectType)element.enumValueIndex).ToString();
        EditorGUI.LabelField(rect, $"{index + 1}.  {label}");
    }

    private void DrawEffectSettingsInExecutionOrder()
    {
        for (int i = 0; i < effectOrder.arraySize; i++)
        {
            var effect = (CustomPostProcessingRenderFeature.EffectType)
                effectOrder.GetArrayElementAtIndex(i).enumValueIndex;
            string propertyName = effect switch
            {
                CustomPostProcessingRenderFeature.EffectType.Outline => "outline",
                CustomPostProcessingRenderFeature.EffectType.Streak => "streak",
                CustomPostProcessingRenderFeature.EffectType.Glitch => "glitch",
                _ => null
            };

            if (propertyName != null)
                EditorGUILayout.PropertyField(settings.FindPropertyRelative(propertyName), true);
        }
    }

    private void EnsureCompleteOrder()
    {
        var order = new List<int>();
        var seen = new HashSet<int>();
        int effectCount = System.Enum.GetValues(typeof(CustomPostProcessingRenderFeature.EffectType)).Length;

        for (int i = 0; i < effectOrder.arraySize; i++)
        {
            int value = effectOrder.GetArrayElementAtIndex(i).enumValueIndex;
            if (value >= 0 && value < effectCount && seen.Add(value))
                order.Add(value);
        }

        for (int value = 0; value < effectCount; value++)
        {
            if (seen.Add(value))
                order.Add(value);
        }

        effectOrder.arraySize = order.Count;
        for (int i = 0; i < order.Count; i++)
            effectOrder.GetArrayElementAtIndex(i).enumValueIndex = order[i];
    }

    private void ApplyAndRecreate()
    {
        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
        ((CustomPostProcessingRenderFeature)target).Create();
        SceneView.RepaintAll();
    }
}
