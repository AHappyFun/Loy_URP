using UnityEngine;
using UnityEditor;

namespace StylizedSky
{
    /// <summary>
    /// Default SkyController inspector, except the day/night cloud settings get "(Day)" and "(Night)" labels.
    /// </summary>
    [CustomEditor(typeof(SkyController))]
    [CanEditMultipleObjects]
    public class SkyControllerEditor : Editor
    {
        private static readonly string[] CloudSettingNames =
        {
            "_dayCloudCoverage", "_nightCloudCoverage", "_dayCloudSharpness", "_nightCloudSharpness"
        };

        private static readonly GUIContent[] CloudSettingLabels =
        {
            new GUIContent("Cloud Coverage (Day)"), new GUIContent("Cloud Coverage (Night)"),
            new GUIContent("Cloud Sharpness (Day)"), new GUIContent("Cloud Sharpness (Night)")
        };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // The cloud settings are the last fields, so drawing them after the rest keeps them at the bottom of Clouds.
            DrawPropertiesExcluding(serializedObject, CloudSettingNames);
            for (int i = 0; i < CloudSettingNames.Length; i++)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(CloudSettingNames[i]), CloudSettingLabels[i]);
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
