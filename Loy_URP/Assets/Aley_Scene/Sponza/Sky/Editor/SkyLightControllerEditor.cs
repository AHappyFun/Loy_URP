using UnityEngine;
using UnityEditor;

namespace StylizedSky
{
    [CustomEditor(typeof(SkyLightController))]
    [CanEditMultipleObjects]
    public class SkyLightControllerEditor : Editor
    {
        private SerializedProperty _directionalLightProp;
        private SerializedProperty _skyControllerProp;
        private SerializedProperty _timeOfDayProp;
        private SerializedProperty _orientationAtNoonProp;
        private SerializedProperty _orientationAtNightProp;
        private SerializedProperty _azimuthOffsetProp;
        private SerializedProperty _trackMoonAtNightProp;
        private SerializedProperty _sunsetHourProp;
        private SerializedProperty _sunsetBlendDurationProp;
        private SerializedProperty _sunriseHourProp;
        private SerializedProperty _sunriseBlendDurationProp;
        private SerializedProperty _useMoonColorAtNightProp;
        private SerializedProperty _moonlightTintProp;
        private SerializedProperty _syncLightColorProp;
        private SerializedProperty _syncLightIntensityProp;
        private SerializedProperty _dayIntensityProp;
        private SerializedProperty _nightIntensityProp;
        private SerializedProperty _fadeBelowHorizonProp;
        private SerializedProperty _horizonFadeAngleProp;
        private SerializedProperty _syncIndirectMultiplierProp;
        private SerializedProperty _dayIndirectMultiplierProp;
        private SerializedProperty _nightIndirectMultiplierProp;
        private SerializedProperty _daySkyIndirectMultiplierProp;
        private SerializedProperty _nightSkyIndirectMultiplierProp;
        private SerializedProperty _reflectionProbesProp;
        private SerializedProperty _reflectionProbeDayIntensityProp;
        private SerializedProperty _reflectionProbeNightIntensityProp;
        private SerializedProperty _animateTimeProp;
        private SerializedProperty _dayDurationSecondsProp;

        private void OnEnable()
        {
            _directionalLightProp = serializedObject.FindProperty("_directionalLight");
            _skyControllerProp = serializedObject.FindProperty("_skyController");
            _timeOfDayProp = serializedObject.FindProperty("_timeOfDay");
            _orientationAtNoonProp = serializedObject.FindProperty("_orientationAtNoon");
            _orientationAtNightProp = serializedObject.FindProperty("_orientationAtNight");
            _azimuthOffsetProp = serializedObject.FindProperty("_azimuthOffset");
            _trackMoonAtNightProp = serializedObject.FindProperty("_trackMoonAtNight");
            _sunsetHourProp = serializedObject.FindProperty("_sunsetHour");
            _sunsetBlendDurationProp = serializedObject.FindProperty("_sunsetBlendDuration");
            _sunriseHourProp = serializedObject.FindProperty("_sunriseHour");
            _sunriseBlendDurationProp = serializedObject.FindProperty("_sunriseBlendDuration");
            _useMoonColorAtNightProp = serializedObject.FindProperty("_useMoonColorAtNight");
            _moonlightTintProp = serializedObject.FindProperty("_moonlightTint");
            _syncLightColorProp = serializedObject.FindProperty("_syncLightColor");
            _syncLightIntensityProp = serializedObject.FindProperty("_syncLightIntensity");
            _dayIntensityProp = serializedObject.FindProperty("_dayIntensity");
            _nightIntensityProp = serializedObject.FindProperty("_nightIntensity");
            _fadeBelowHorizonProp = serializedObject.FindProperty("_fadeBelowHorizon");
            _horizonFadeAngleProp = serializedObject.FindProperty("_horizonFadeAngle");
            _syncIndirectMultiplierProp = serializedObject.FindProperty("_syncIndirectMultiplier");
            _dayIndirectMultiplierProp = serializedObject.FindProperty("_dayIndirectMultiplier");
            _nightIndirectMultiplierProp = serializedObject.FindProperty("_nightIndirectMultiplier");
            _daySkyIndirectMultiplierProp = serializedObject.FindProperty("_daySkyIndirectMultiplier");
            _nightSkyIndirectMultiplierProp = serializedObject.FindProperty("_nightSkyIndirectMultiplier");
            _reflectionProbesProp = serializedObject.FindProperty("_reflectionProbes");
            _reflectionProbeDayIntensityProp = serializedObject.FindProperty("_reflectionProbeDayIntensity");
            _reflectionProbeNightIntensityProp = serializedObject.FindProperty("_reflectionProbeNightIntensity");
            _animateTimeProp = serializedObject.FindProperty("_animateTime");
            _dayDurationSecondsProp = serializedObject.FindProperty("_dayDurationSeconds");
        }

        public override void OnInspectorGUI()
        {
            if (serializedObject == null || serializedObject.targetObject == null) return;

            if (_orientationAtNoonProp == null || _orientationAtNightProp == null || _timeOfDayProp == null)
            {
                OnEnable();
                if (_orientationAtNoonProp == null || _orientationAtNightProp == null || _timeOfDayProp == null)
                {
                    DrawDefaultInspector();
                    return;
                }
            }

            serializedObject.Update();

            SkyLightController controller = target as SkyLightController;
            if (controller == null) return;

            // Title / Info Header
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            
            float tod = _timeOfDayProp.floatValue;
            int hours = Mathf.FloorToInt(tod) % 24;
            int minutes = Mathf.FloorToInt((tod - Mathf.Floor(tod)) * 60f);
            string timePhase = GetTimePhaseString(tod);

            float moonBlend = SkyLightController.CalculateMoonBlendFactor(
                tod, 
                _sunsetHourProp.floatValue, 
                _sunsetBlendDurationProp.floatValue, 
                _sunriseHourProp.floatValue, 
                _sunriseBlendDurationProp.floatValue);

            string celestialTracking = "Sun (100%)";
            if (_trackMoonAtNightProp.boolValue)
            {
                if (moonBlend >= 0.999f)
                {
                    celestialTracking = "Moon (100%)";
                }
                else if (moonBlend <= 0.001f)
                {
                    celestialTracking = "Sun (100%)";
                }
                else
                {
                    float t = (tod % 24f + 24f) % 24f;
                    string target = (t > 12f) ? "Sun -> Moon" : "Moon -> Sun";
                    celestialTracking = $"{target} ({moonBlend * 100f:F0}% Moon)";
                }
            }
            
            EditorGUILayout.LabelField($"Clock: {hours:D2}:{minutes:D2} ({timePhase})", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Light Target: {celestialTracking} | Orientation: {controller.CurrentOrientation:F1}° (Noon: {_orientationAtNoonProp.floatValue:F1}°, Night: {_orientationAtNightProp.floatValue:F1}°)", EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(6);

            // References
            EditorGUILayout.LabelField("References", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_directionalLightProp, new GUIContent("Directional Light"));
            EditorGUILayout.PropertyField(_skyControllerProp, new GUIContent("Sky Controller"));

            EditorGUILayout.Space(6);

            // Sky Parameters
            EditorGUILayout.LabelField("Sky Parameters", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_timeOfDayProp, new GUIContent("Time Of Day (0-24h)"));
            EditorGUILayout.PropertyField(_orientationAtNoonProp, new GUIContent("Orientation At Noon"));
            EditorGUILayout.PropertyField(_orientationAtNightProp, new GUIContent("Orientation At Night"));
            EditorGUILayout.PropertyField(_azimuthOffsetProp);

            // Quick Time Presets
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("06:00 (Dawn)", EditorStyles.miniButtonLeft))
            {
                _timeOfDayProp.floatValue = 6.0f;
            }
            if (GUILayout.Button("12:00 (Noon)", EditorStyles.miniButtonMid))
            {
                _timeOfDayProp.floatValue = 12.0f;
            }
            if (GUILayout.Button("18:00 (Dusk)", EditorStyles.miniButtonMid))
            {
                _timeOfDayProp.floatValue = 18.0f;
            }
            if (GUILayout.Button("00:00 (Night)", EditorStyles.miniButtonRight))
            {
                _timeOfDayProp.floatValue = 0.0f;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);

            // Moonlight (Night Tracking)
            EditorGUILayout.LabelField("Moonlight (Night Tracking)", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_trackMoonAtNightProp, new GUIContent("Track Moon At Night"));
            if (_trackMoonAtNightProp.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_sunsetHourProp, new GUIContent("Sunset Hour (Start Blend)"));
                EditorGUILayout.PropertyField(_sunsetBlendDurationProp, new GUIContent("Sunset Duration (Hours)"));
                EditorGUILayout.PropertyField(_sunriseHourProp, new GUIContent("Sunrise Hour (End Blend)"));
                EditorGUILayout.PropertyField(_sunriseBlendDurationProp, new GUIContent("Dawn Duration (Hours)"));
                EditorGUILayout.PropertyField(_useMoonColorAtNightProp, new GUIContent("Use MoonColor At Night"));
                if (_useMoonColorAtNightProp.boolValue)
                {
                    EditorGUILayout.PropertyField(_moonlightTintProp, new GUIContent("Moonlight Tint"));
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(6);

            // Light Appearance Sync
            EditorGUILayout.LabelField("Light Appearance Sync", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_syncLightColorProp, new GUIContent("Sync Light Color"));
            EditorGUILayout.PropertyField(_syncLightIntensityProp, new GUIContent("Sync Light Intensity"));
            if (_syncLightIntensityProp.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_dayIntensityProp, new GUIContent("Day Intensity"));
                EditorGUILayout.PropertyField(_nightIntensityProp, new GUIContent("Night Intensity"));
                EditorGUILayout.PropertyField(_fadeBelowHorizonProp, new GUIContent("Fade Below Horizon"));
                if (_fadeBelowHorizonProp.boolValue)
                {
                    EditorGUILayout.PropertyField(_horizonFadeAngleProp, new GUIContent("Fade Angle (Degrees)"));
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.PropertyField(_syncIndirectMultiplierProp, new GUIContent("Sync Indirect Multiplier"));
            if (_syncIndirectMultiplierProp.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_dayIndirectMultiplierProp, new GUIContent("Light Indirect Multiplier (Day)"));
                EditorGUILayout.PropertyField(_nightIndirectMultiplierProp, new GUIContent("Light Indirect Multiplier (Night)"));
                EditorGUILayout.PropertyField(_daySkyIndirectMultiplierProp, new GUIContent("Sky Indirect Multiplier (Day)"));
                EditorGUILayout.PropertyField(_nightSkyIndirectMultiplierProp, new GUIContent("Sky Indirect Multiplier (Night)"));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(6);

            // Reflection Probes
            EditorGUILayout.LabelField("Reflection Probes", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_reflectionProbesProp, new GUIContent("Reflection Probes"), true);
            EditorGUILayout.PropertyField(_reflectionProbeDayIntensityProp, new GUIContent("Reflection Probe Day Intensity"));
            EditorGUILayout.PropertyField(_reflectionProbeNightIntensityProp, new GUIContent("Reflection Probe Night Intensity"));

            EditorGUILayout.Space(6);

            // Day-Night Animation
            EditorGUILayout.LabelField("Day-Night Cycle (Play Mode)", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_animateTimeProp, new GUIContent("Animate Day/Night"));
            if (_animateTimeProp.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_dayDurationSecondsProp, new GUIContent("Day Duration (Seconds)"));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(8);

            // Manual Trigger Buttons
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply Mode Now", GUILayout.Height(24)))
            {
                controller.UpdateController();
            }
            EditorGUILayout.EndHorizontal();

            bool changed = serializedObject.ApplyModifiedProperties();
            if (changed)
            {
                controller.UpdateController();
            }
        }

        private static string GetTimePhaseString(float tod)
        {
            float t = (tod % 24f + 24f) % 24f;
            if (t >= 5f && t < 8f) return "Dawn / Sunrise";
            if (t >= 8f && t < 16f) return "Day / Afternoon";
            if (t >= 16f && t < 19.5f) return "Dusk / Sunset";
            return "Night";
        }
    }
}
