using UnityEngine;
using UnityEngine.Serialization;

namespace StylizedSky
{
    /// <summary>
    /// Controls the directional light from the time of day, and passes that time on to the SkyController
    /// (which owns the skybox material).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Rendering/Sky Light Controller")]
    public class SkyLightController : MonoBehaviour
    {

        [Header("References")]
        [Tooltip("Main directional light. If unassigned, automatically detects RenderSettings.sun or light on this GameObject.")]
        [SerializeField] private Light _directionalLight;

        [Tooltip("Sky controller that owns the skybox material. If unassigned, automatically detects one in the scene.")]
        [SerializeField] private SkyController _skyController;

        [Header("Sky Parameters")]
        [Range(0f, 24f)]
        [Tooltip("Time of day in hours (0.0 to 24.0, where 6=Dawn, 12=Noon, 18=Dusk, 0/24=Midnight).")]
        [SerializeField] private float _timeOfDay = 12f;
        
        [Range(-180f, 180f)]
        [Tooltip("Orientation at noon in degrees (-180 to 180). Controls the sun's arc height in the sky at noon.")]
        [SerializeField] private float _orientationAtNoon = -67f;

        [Range(-180f, 180f)]
        [Tooltip("Orientation at night in degrees (-180 to 180). Controls the celestial arc height in the sky at night.")]
        [SerializeField] private float _orientationAtNight = -90f;

        [Range(-180f, 180f)]
        [Tooltip("Rotates the sun and moon path around the world's vertical axis.")]
        [SerializeField] private float _azimuthOffset;

        [Header("Moonlight (Night Tracking)")]
        [Tooltip("Automatically blend directional light to track the moon (negated sun angle) during night hours.")]
        [SerializeField] private bool _trackMoonAtNight = true;

        [Range(0f, 24f)]
        [Tooltip("Time of day when the sun sets below the horizon and blending into moonlight begins (hours).")]
        [SerializeField] private float _sunsetHour = 18.0f;

        [Range(0.1f, 6f)]
        [Tooltip("Duration in hours for blending from sun angle to moon angle during sunset.")]
        [SerializeField] private float _sunsetBlendDuration = 2.0f;

        [Range(0f, 24f)]
        [Tooltip("Time of day when dawn ends and the sun rises above the horizon (hours).")]
        [SerializeField] private float _sunriseHour = 6.0f;

        [Range(0.1f, 6f)]
        [Tooltip("Duration in hours for blending from moon angle back to sun angle before sunrise.")]
        [SerializeField] private float _sunriseBlendDuration = 2.0f;

        [Tooltip("Synchronize directional light color with the Sky Controller's Moon Color during the night.")]
        [SerializeField] private bool _useMoonColorAtNight = true;

        [Tooltip("Moonlight color multiplier / tint during night hours.")]
        [SerializeField] private Color _moonlightTint = new Color(0.85f, 0.92f, 1.0f, 1.0f);

        [Header("Light Appearance Sync")]
        [Tooltip("Synchronize directional light color with the Sky Controller's Sun Color gradient.")]
        [SerializeField] private bool _syncLightColor = true;

        [Tooltip("Synchronize directional light intensity across the day/night cycle.")]
        [SerializeField] private bool _syncLightIntensity = true;

        [Tooltip("Light intensity at solar noon.")]
        [SerializeField] private float _dayIntensity = 2.0f;

        [Tooltip("Light intensity at midnight.")]
        [SerializeField] private float _nightIntensity = 0.1f;

        [Tooltip("Synchronize directional light indirect multiplier (bounce intensity) across the day/night cycle.")]
        [SerializeField] private bool _syncIndirectMultiplier = true;

        [Tooltip("Indirect light multiplier (bounce intensity) during full daylight.")]
        [SerializeField] private float _dayIndirectMultiplier = 1.0f;

        [Tooltip("Indirect light multiplier (bounce intensity) during night.")]
        [SerializeField] private float _nightIndirectMultiplier = 0.0f;

        [Tooltip("Scene Environment Lighting intensity multiplier (sky ambient) during full daylight.")]
        [SerializeField] private float _daySkyIndirectMultiplier = 1.0f;

        [Tooltip("Scene Environment Lighting intensity multiplier (sky ambient) during night.")]
        [SerializeField] private float _nightSkyIndirectMultiplier = 1.0f;

        [Tooltip("Fade directional light intensity to 0 when the light direction is below the horizon to prevent lighting from underneath.")]
        [SerializeField] private bool _fadeBelowHorizon = true;

        [Range(0f, 15f)]
        [Tooltip("Angular range in degrees near the horizon over which light fades smoothly to 0.")]
        [SerializeField] private float _horizonFadeAngle = 3.0f;

        [Header("Reflection Probes")]
        [Tooltip("Reflection probes whose Intensity is blended between the day and night values below.")]
        [SerializeField] private ReflectionProbe[] _reflectionProbes = new ReflectionProbe[0];

        [Tooltip("Reflection probe intensity during full daylight.")]
        [SerializeField] private float _reflectionProbeDayIntensity = 1.0f;

        [Tooltip("Reflection probe intensity during night.")]
        [SerializeField] private float _reflectionProbeNightIntensity = 0.5f;

        [Header("Day-Night Animation (Play Mode)")]
        [Tooltip("Automatically progress time of day during Play mode.")]
        [SerializeField] private bool _animateTime = false;

        [Tooltip("Full 24-hour day duration in real-time seconds.")]
        [SerializeField] private float _dayDurationSeconds = 120f;

        public SkyController SkyController => _skyController;

        public float TimeOfDay
        {
            get => _timeOfDay;
            set
            {
                float hours = Mathf.Repeat(value, 24f);
                if (hours == _timeOfDay) return;
                _timeOfDay = hours;
                UpdateController();
            }
        }

        public float OrientationAtNoon
        {
            get => _orientationAtNoon;
            set
            {
                float orientation = Mathf.Clamp(value, -180f, 180f);
                if (orientation == _orientationAtNoon) return;
                _orientationAtNoon = orientation;
                UpdateController();
            }
        }

        public float OrientationAtNight
        {
            get => _orientationAtNight;
            set
            {
                float orientation = Mathf.Clamp(value, -180f, 180f);
                if (orientation == _orientationAtNight) return;
                _orientationAtNight = orientation;
                UpdateController();
            }
        }

        public float CurrentOrientation => CalculateCurrentOrientation(
            _timeOfDay,
            _orientationAtNoon,
            _orientationAtNight,
            _sunsetHour,
            _sunsetBlendDuration,
            _sunriseHour,
            _sunriseBlendDuration);

        [System.Obsolete("Use OrientationAtNoon and OrientationAtNight instead.")]
        public float Latitude
        {
            get => CurrentOrientation;
            set
            {
                _orientationAtNoon = Mathf.Clamp(value, -180f, 180f);
                _orientationAtNight = Mathf.Clamp(value, -180f, 180f);
                UpdateController();
            }
        }

        // The sky controller whose Changed event this controller is listening to.
        private SkyController _subscribedSkyController;

        private void OnEnable()
        {
            ValidateReferences();
            UpdateController();
        }

        private void OnDisable()
        {
            SubscribeToSkyController(null);
        }

        private void OnValidate()
        {
            ValidateReferences();
            UpdateController();
        }

        // Nothing is written here unless the time of day advances or a reference is newly found; settings
        // changes are applied by OnValidate, the property setters and the sky controller's Changed event.
        private void Update()
        {
            Light previousLight = _directionalLight;
            SkyController previousSkyController = _skyController;
            ValidateReferences();
            bool changed = _directionalLight != previousLight || _skyController != previousSkyController;

            if (Application.isPlaying && _animateTime && _dayDurationSeconds > 0.001f && Time.deltaTime > 0f)
            {
                float hoursPerSecond = 24f / _dayDurationSeconds;
                _timeOfDay = Mathf.Repeat(_timeOfDay + hoursPerSecond * Time.deltaTime, 24f);
                changed = true;
            }

            if (changed)
            {
                UpdateController();
            }
        }

        private void SubscribeToSkyController(SkyController skyController)
        {
            if (_subscribedSkyController == skyController) return;

            if (_subscribedSkyController != null)
            {
                _subscribedSkyController.Changed -= OnSkyControllerChanged;
            }
            _subscribedSkyController = skyController;
            if (_subscribedSkyController != null)
            {
                _subscribedSkyController.Changed += OnSkyControllerChanged;
            }
        }

        // The sky's gradients feed the light colour, so re-apply when its settings are edited.
        private void OnSkyControllerChanged()
        {
            UpdateLightAppearance();
        }

        public void ValidateReferences()
        {
            if (_directionalLight == null)
            {
                _directionalLight = GetComponent<Light>();
                if (_directionalLight == null && RenderSettings.sun != null)
                {
                    _directionalLight = RenderSettings.sun;
                }
                if (_directionalLight == null)
                {
                    Light[] lights = FindObjectsByType<Light>(FindObjectsInactive.Exclude);
                    foreach (Light l in lights)
                    {
                        if (l.type == LightType.Directional)
                        {
                            _directionalLight = l;
                            break;
                        }
                    }
                }
            }

            if (_skyController == null)
            {
                _skyController = FindAnyObjectByType<SkyController>(FindObjectsInactive.Exclude);
            }

            if (isActiveAndEnabled)
            {
                SubscribeToSkyController(_skyController);
            }
        }

        /// <summary>
        /// Applies the current time of day and settings. Each value is only written when it differs from
        /// what is already set, so calling this when nothing has changed touches nothing.
        /// </summary>
        public void UpdateController()
        {
            UpdateReflectionProbes();

            if (_directionalLight == null) return;

            ChangeLightAngle();
        }

        private void UpdateReflectionProbes()
        {
            if (_reflectionProbes == null || _reflectionProbes.Length == 0) return;

            float intensity = CalculateReflectionProbeIntensity(_timeOfDay, _reflectionProbeDayIntensity, _reflectionProbeNightIntensity);
            foreach (ReflectionProbe probe in _reflectionProbes)
            {
                if (probe != null && probe.intensity != intensity)
                {
                    probe.intensity = intensity;
                }
            }
        }

        /// <summary>
        /// Change directional light angle from Time of Day and Orientation.
        /// </summary>
        public void ChangeLightAngle()
        {
            if (_directionalLight == null) return;

            // The sky's colours follow the time of day; its sun-moon disk follows this light in the shader,
            // using the moon's settings while the light tracks the moon.
            if (_skyController != null)
            {
                _skyController.TimeOfDay = _timeOfDay;
                _skyController.NightBlend = _trackMoonAtNight
                    ? CalculateMoonBlendFactor(_timeOfDay, _sunsetHour, _sunsetBlendDuration, _sunriseHour, _sunriseBlendDuration)
                    : 0f;
            }

            float currentOrientation = CurrentOrientation;

            // Compute light rotation, tracking the sun by day and the moon by night
            Quaternion lightRotation = Quaternion.AngleAxis(_azimuthOffset, Vector3.up) * CalculateLightRotation(
                _timeOfDay, 
                currentOrientation, 
                _trackMoonAtNight, 
                _sunsetHour, 
                _sunsetBlendDuration, 
                _sunriseHour, 
                _sunriseBlendDuration);

            // Exact comparison: Quaternion's == tolerance would swallow the small steps of a slow day cycle.
            Transform lightTransform = _directionalLight.transform;
            if (!lightTransform.rotation.Equals(lightRotation))
            {
                lightTransform.rotation = lightRotation;
            }

            UpdateLightAppearance();
        }

        private void UpdateLightAppearance()
        {
            if (_directionalLight == null) return;

            if (_syncLightColor && _skyController != null)
            {
                Color lightColor = CalculateSkySunColor(
                    _skyController,
                    _timeOfDay, 
                    _trackMoonAtNight, 
                    _useMoonColorAtNight, 
                    _moonlightTint, 
                    _sunsetHour, 
                    _sunsetBlendDuration, 
                    _sunriseHour,
                    _sunriseBlendDuration);
                if (!_directionalLight.color.Equals(lightColor))
                {
                    _directionalLight.color = lightColor;
                }
            }

            // Calculate horizon fade factor (tones down intensity to 0.001 when celestial body is below the horizon)
            float horizonFactor = 1.0f;
            if (_fadeBelowHorizon)
            {
                Vector3 currentLightDir = CalculateEffectiveLightDirection(
                    _timeOfDay,
                    CurrentOrientation,
                    _trackMoonAtNight,
                    _sunsetHour,
                    _sunsetBlendDuration,
                    _sunriseHour,
                    _sunriseBlendDuration);

                // The StylizedSky shader draws its sun-moon disk where the light comes from, at -currentLightDir.
                // The celestial body is above the horizon when (-currentLightDir).y > 0.
                Vector3 celestialSkyPos = -currentLightDir;
                horizonFactor = CalculateHorizonFadeFactor(celestialSkyPos, _horizonFadeAngle);
            }

            if (_syncLightIntensity)
            {
                float baseIntensity = CalculateSkySunIntensity(_timeOfDay, _dayIntensity, _nightIntensity);
                float intensity = Mathf.Max(baseIntensity * horizonFactor, 0.001f);
                if (_directionalLight.intensity != intensity)
                {
                    _directionalLight.intensity = intensity;
                }
            }

            if (_syncIndirectMultiplier)
            {
                float baseIndirect = CalculateSkyIndirectMultiplier(_timeOfDay, _dayIndirectMultiplier, _nightIndirectMultiplier);
                float bounceIntensity = Mathf.Max(baseIndirect * horizonFactor, 0.001f);
                if (_directionalLight.bounceIntensity != bounceIntensity)
                {
                    _directionalLight.bounceIntensity = bounceIntensity;
                }

                // The sky lights the scene from the whole dome, so it ignores the sun-moon horizon fade.
                float skyIndirect = CalculateSkyIndirectMultiplier(_timeOfDay, _daySkyIndirectMultiplier, _nightSkyIndirectMultiplier);
                if (gameObject.scene == UnityEngine.SceneManagement.SceneManager.GetActiveScene()
                    && RenderSettings.ambientIntensity != skyIndirect)
                {
                    RenderSettings.ambientIntensity = skyIndirect;
                }
            }
        }

        #region Mathematical Formulas

        /// <summary>
        /// Calculates the effective orientation (latitude) for the given time of day,
        /// blending between orientationAtNoon during day hours and orientationAtNight during night hours.
        /// </summary>
        public static float CalculateCurrentOrientation(
            float timeOfDay,
            float orientationAtNoon,
            float orientationAtNight,
            float sunsetHour = 18f,
            float sunsetDuration = 2f,
            float sunriseHour = 6f,
            float sunriseDuration = 2f)
        {
            float moonBlend = CalculateMoonBlendFactor(timeOfDay, sunsetHour, sunsetDuration, sunriseHour, sunriseDuration);
            return Mathf.Lerp(orientationAtNoon, orientationAtNight, moonBlend);
        }

        /// <summary>
        /// Computes the light rotation Quaternion with separate noon and night orientations.
        /// </summary>
        public static Quaternion CalculateLightRotation(
            float timeOfDay,
            float orientationAtNoon,
            float orientationAtNight,
            bool trackMoonAtNight = true,
            float sunsetHour = 18f,
            float sunsetDuration = 2f,
            float sunriseHour = 6f,
            float sunriseDuration = 2f)
        {
            float currentOrientation = CalculateCurrentOrientation(
                timeOfDay,
                orientationAtNoon,
                orientationAtNight,
                sunsetHour,
                sunsetDuration,
                sunriseHour,
                sunriseDuration);

            return CalculateLightRotation(
                timeOfDay,
                currentOrientation,
                trackMoonAtNight,
                sunsetHour,
                sunsetDuration,
                sunriseHour,
                sunriseDuration);
        }

        /// <summary>
        /// Computes the light rotation Quaternion.
        /// When trackMoonAtNight is enabled, during sunset the light smoothly rotates below the horizon
        /// into the moon angle (negated sun angle), tracks the moon throughout the night,
        /// and as night turns to dawn, smoothly rotates below the horizon back into the sun angle.
        /// Uses a continuous celestial polar sweep to eliminate 180-degree Slerp singularities and jumps.
        /// </summary>
        public static Quaternion CalculateLightRotation(
            float timeOfDay, 
            float latitude, 
            bool trackMoonAtNight = true,
            float sunsetHour = 18f,
            float sunsetDuration = 2f,
            float sunriseHour = 6f,
            float sunriseDuration = 2f)
        {
            Vector3 celestialDir = CalculateEffectiveLightDirection(
                timeOfDay, 
                latitude, 
                trackMoonAtNight, 
                sunsetHour, 
                sunsetDuration, 
                sunriseHour, 
                sunriseDuration);

            // The visual celestial body (Sun/Moon) sits at -celestialDir on the sky dome; the StylizedSky shader
            // draws its disk there by reading the main light direction.
            // Light rays illuminate along transform.forward from the celestial body downwards into the scene:
            // lightRayForward = -(-celestialDir) = celestialDir.
            Vector3 lightRayForward = celestialDir;

            float latRad = latitude * Mathf.Deg2Rad;
            Vector3 polarAxis = new Vector3(0f, Mathf.Cos(latRad), -Mathf.Sin(latRad)).normalized;

            return LookRotationSafe(lightRayForward, polarAxis);
        }

        /// <summary>
        /// Computes the continuous light direction vector with separate noon and night orientations.
        /// </summary>
        public static Vector3 CalculateEffectiveLightDirection(
            float timeOfDay,
            float orientationAtNoon,
            float orientationAtNight,
            bool trackMoonAtNight = true,
            float sunsetHour = 18f,
            float sunsetDuration = 2f,
            float sunriseHour = 6f,
            float sunriseDuration = 2f)
        {
            float currentOrientation = CalculateCurrentOrientation(
                timeOfDay,
                orientationAtNoon,
                orientationAtNight,
                sunsetHour,
                sunsetDuration,
                sunriseHour,
                sunriseDuration);

            return CalculateEffectiveLightDirection(
                timeOfDay,
                currentOrientation,
                trackMoonAtNight,
                sunsetHour,
                sunsetDuration,
                sunriseHour,
                sunriseDuration);
        }

        /// <summary>
        /// Computes the continuous light direction vector without any rotational jumps or singularities.
        /// </summary>
        public static Vector3 CalculateEffectiveLightDirection(
            float timeOfDay,
            float latitude,
            bool trackMoonAtNight = true,
            float sunsetHour = 18f,
            float sunsetDuration = 2f,
            float sunriseHour = 6f,
            float sunriseDuration = 2f)
        {
            float t = (timeOfDay % 24f + 24f) % 24f;
            float latRad = latitude * Mathf.Deg2Rad;
            float hourAngle = (t / 24.0f) * 6.2831853f - 1.5707963f;

            Vector3 sunDir = new Vector3(
                Mathf.Cos(hourAngle),
                Mathf.Sin(hourAngle) * Mathf.Sin(latRad),
                Mathf.Sin(hourAngle) * Mathf.Cos(latRad)).normalized;

            if (!trackMoonAtNight)
            {
                return sunDir;
            }

            // Celestial polar axis (invariant perpendicular to orbital plane)
            Vector3 polarAxis = new Vector3(0f, Mathf.Cos(latRad), -Mathf.Sin(latRad)).normalized;

            sunsetDuration = Mathf.Max(0.01f, sunsetDuration);
            sunriseDuration = Mathf.Max(0.01f, sunriseDuration);

            float sunsetEnd = sunsetHour + sunsetDuration;
            float dawnStart = (sunriseHour - sunriseDuration + 24f) % 24f;

            // 1. Full Day (sunrise to sunset)
            if (t >= sunriseHour && t <= sunsetHour)
            {
                return sunDir;
            }

            // 2. Sunset Transition: smoothly sweeps below the horizon into moon angle
            if (t > sunsetHour && t < sunsetEnd)
            {
                float p = Mathf.SmoothStep(0f, 1f, (t - sunsetHour) / sunsetDuration);
                return Quaternion.AngleAxis(-p * 180f, polarAxis) * sunDir;
            }

            // 3. Dawn Transition: smoothly sweeps below the horizon from moon angle back to sun angle
            if (t > dawnStart && t < sunriseHour)
            {
                float p = Mathf.SmoothStep(1f, 0f, (t - dawnStart) / sunriseDuration);
                return Quaternion.AngleAxis(p * 180f, polarAxis) * sunDir;
            }

            // 4. Full Night: exactly tracks the moon (-sunDir)
            return -sunDir;
        }

        private static Quaternion LookRotationSafe(Vector3 forward, Vector3 referenceUp)
        {
            if (referenceUp.sqrMagnitude < 0.001f || Mathf.Abs(Vector3.Dot(forward, referenceUp)) > 0.999f)
            {
                referenceUp = (Mathf.Abs(forward.y) < 0.99f) ? Vector3.up : Vector3.forward;
            }
            return Quaternion.LookRotation(forward, referenceUp);
        }

        /// <summary>
        /// Calculates the blend factor between Sun (0.0) and Moon (1.0).
        /// Blends from 0 to 1 during sunset hours, remains 1 throughout the night,
        /// and blends from 1 back to 0 during dawn hours.
        /// </summary>
        public static float CalculateMoonBlendFactor(
            float timeOfDay, 
            float sunsetHour = 18f, 
            float sunsetDuration = 2f, 
            float sunriseHour = 6f, 
            float sunriseDuration = 2f)
        {
            float t = (timeOfDay % 24f + 24f) % 24f;
            sunsetHour = (sunsetHour % 24f + 24f) % 24f;
            sunriseHour = (sunriseHour % 24f + 24f) % 24f;
            sunsetDuration = Mathf.Max(0.001f, sunsetDuration);
            sunriseDuration = Mathf.Max(0.001f, sunriseDuration);

            float dayLength = (sunsetHour - sunriseHour + 24f) % 24f;
            float timeSinceSunrise = (t - sunriseHour + 24f) % 24f;

            if (timeSinceSunrise <= dayLength)
            {
                return 0f;
            }

            float nightLength = 24f - dayLength;
            float timeSinceSunset = timeSinceSunrise - dayLength;

            if (timeSinceSunset < sunsetDuration)
            {
                float p = timeSinceSunset / sunsetDuration;
                return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(p));
            }

            float timeUntilSunrise = nightLength - timeSinceSunset;
            if (timeUntilSunrise < sunriseDuration)
            {
                float p = timeUntilSunrise / sunriseDuration;
                return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(p));
            }

            return 1f;
        }

        /// <summary>
        /// Calculates the light colour from the Sky Controller's Sun Color gradient, blending to its Moon Color at night.
        /// </summary>
        public static Color CalculateSkySunColor(
            SkyController sky,
            float timeOfDay,
            bool trackMoonAtNight = true,
            bool useMoonColorAtNight = true,
            Color? moonlightTint = null,
            float sunsetHour = 18f,
            float sunsetDuration = 2f,
            float sunriseHour = 6f,
            float sunriseDuration = 2f)
        {
            if (sky == null) return Color.white;

            Color sunColor = sky.EvaluateSunColor(timeOfDay);

            if (trackMoonAtNight && useMoonColorAtNight)
            {
                float moonBlend = CalculateMoonBlendFactor(timeOfDay, sunsetHour, sunsetDuration, sunriseHour, sunriseDuration);
                if (moonBlend > 0.0001f)
                {
                    Color tint = moonlightTint ?? Color.white;
                    Color finalMoonColor = sky.NightMoonColor * tint;

                    return Color.Lerp(sunColor, finalMoonColor, moonBlend);
                }
            }

            return sunColor;
        }

        /// <summary>
        /// Calculates blended sun intensity based on time of day.
        /// </summary>
        public static float CalculateSkySunIntensity(float timeOfDay, float dayIntensity, float nightIntensity)
        {
            float dayWeight = CalculateDayNightFactor(timeOfDay);
            return Mathf.Lerp(nightIntensity, dayIntensity, dayWeight);
        }

        /// <summary>
        /// Calculates blended indirect light multiplier (bounce intensity) based on time of day.
        /// </summary>
        public static float CalculateSkyIndirectMultiplier(float timeOfDay, float dayIndirect, float nightIndirect)
        {
            float dayWeight = CalculateDayNightFactor(timeOfDay);
            return Mathf.Lerp(nightIndirect, dayIndirect, dayWeight);
        }

        /// <summary>
        /// Calculates blended reflection probe intensity based on time of day.
        /// </summary>
        public static float CalculateReflectionProbeIntensity(float timeOfDay, float dayIntensity, float nightIntensity)
        {
            float dayWeight = CalculateDayNightFactor(timeOfDay);
            return Mathf.Lerp(nightIntensity, dayIntensity, dayWeight);
        }

        /// <summary>
        /// Returns 0.0 at full night and 1.0 at full daylight with smooth transitions during dawn and dusk.
        /// </summary>
        public static float CalculateDayNightFactor(float timeOfDay)
        {
            float t = (timeOfDay % 24f + 24f) % 24f;
            float distNight = Mathf.Min(t, 24f - t);
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((distNight - 3f) / 4f));
        }

        /// <summary>
        /// Calculates a smooth 0.0 to 1.0 factor that tones down the light to 0 when its direction points below the horizon.
        /// </summary>
        public static float CalculateHorizonFadeFactor(Vector3 lightDirection, float fadeAngleDegrees = 3.0f)
        {
            if (fadeAngleDegrees <= 0.001f)
            {
                return lightDirection.y > 0f ? 1f : 0f;
            }

            // Elevation above horizon in sine (normalized Y coordinate)
            float sinFade = Mathf.Sin(fadeAngleDegrees * Mathf.Deg2Rad);
            if (lightDirection.y <= 0f)
            {
                return 0f;
            }
            if (lightDirection.y >= sinFade)
            {
                return 1f;
            }

            float p = lightDirection.y / sinFade;
            return Mathf.SmoothStep(0f, 1f, p);
        }

        #endregion
    }
}

