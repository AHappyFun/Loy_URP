using UnityEngine;
using UnityEngine.Serialization;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace StylizedSky
{
    /// <summary>
    /// Single source of truth for the stylized skybox material. Holds the time-of-day colour gradients
    /// and separate sun and moon disk settings, and writes the evaluated values to the material's single
    /// sun-moon set whenever they change (blended by NightBlend).
    /// It renders through its own unsaved instance of that asset (assigned to RenderSettings.skybox), in edit
    /// mode as well as play mode, so changes never touch the asset on disk. The instance is destroyed when the
    /// controller is disabled, and the asset is put back in RenderSettings while the scene saves.
    /// The sun-moon disk position is not set here: the shader draws it where the main light comes from.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Rendering/Sky Controller")]
    public class SkyController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Skybox material asset (e.g. SkyStylized_001). Never written to: the controller renders and drives an instance of it.")]
        [SerializeField] private Material _skyMaterial;

        [Header("Time of Day")]
        [Range(0f, 24f)]
        [Tooltip("Time of day in hours. Normally driven by SkyLightController.")]
        [SerializeField] private float _timeOfDay = 12f;

        [Header("Sky Colors Over the Day (Night 0%, Dawn 25%, Day 50%, Dusk 75%, Night 100%)")]
        [SerializeField] private Gradient _zenithColor = TimeOfDayGradient(
            new Color(0.20392159f, 0.18039216f, 0.47450984f), new Color(0.50158626f, 0.35444108f, 0.5566038f),
            new Color(0.32941172f, 0.51287746f, 0.8745098f), new Color(0.4820693f, 0.36044857f, 0.509434f));

        [SerializeField] private Gradient _horizonColor = TimeOfDayGradient(
            new Color(0.11764707f, 0.14117648f, 0.22352943f), new Color(0.8113208f, 0.4789172f, 0.3980064f),
            new Color(0.4784314f, 0.5803922f, 0.65882355f), new Color(0.8679245f, 0.4561413f, 0.26201496f));

        [SerializeField] private Gradient _groundColor = TimeOfDayGradient(
            new Color(0.07075471f, 0.07075471f, 0.09433961f), new Color(0.2316732f, 0.18115878f, 0.2783019f),
            new Color(0.27058825f, 0.3372549f, 0.3529412f), new Color(0.17449456f, 0.1327652f, 0.24056602f));

        [Header("Day / Night")]
        [Range(0f, 1f)]
        [Tooltip("0 = the light is the sun, 1 = the light is the moon. Blends the Sun and Moon settings below. Normally driven by SkyLightController.")]
        [SerializeField] private float _nightBlend;

        [Header("Sun (Day)")]
        [Tooltip("Sun disk and glow colour over the day (Night 0%, Dawn 25%, Day 50%, Dusk 75%). The disk is drawn at 2.5x this colour.")]
        [SerializeField] private Gradient _sunColor = TimeOfDayGradient(
            Color.black, new Color(1f, 0.8784314f, 0.6f),
            new Color(1f, 0.98039216f, 0.9019608f), new Color(1f, 0.5803921f, 0.27843136f));

        [Range(0f, 1f)]
        [Tooltip("Angular radius of the disk, in radians.")]
        [FormerlySerializedAs("_sunMoonSize")]
        [SerializeField] private float _sunSize = 0.044f;

        [Range(0f, 1f)]
        [Tooltip("Angular width of the disk's soft edge, in radians.")]
        [FormerlySerializedAs("_sunMoonSoftness")]
        [SerializeField] private float _sunSoftness = 0.006f;

        [Range(0.1f, 2f)]
        [Tooltip("How far the glow spreads around the disk.")]
        [FormerlySerializedAs("_sunMoonGlowSpread")]
        [SerializeField] private float _sunGlowSpread = 0.209f;

        [Range(0f, 5f)]
        [FormerlySerializedAs("_sunMoonGlowIntensity")]
        [SerializeField] private float _sunGlowIntensity = 1f;

        // Defaults reproduce the old shader's moon: its colour faded to 0.3x at dawn and dusk and to black
        // by day, and its glow used a tighter falloff and an unboosted white colour (spread 0.2 * 12/15,
        // intensity 0.25 / (0.4 moon disk dimming * 1.2 glow boost)).
        [Header("Moon (Night)")]
        [Tooltip("Moon disk and glow colour over the day (Night 0%, Dawn 25%, Day 50%, Dusk 75%). Shown at this brightness, unlike the sun's 2.5x disk boost.")]
        [SerializeField] private Gradient _moonColor = TimeOfDayGradient(
            OldMoonColor, ScaleLinear(OldMoonColor, 0.3f), Color.black, ScaleLinear(OldMoonColor, 0.3f));

        [Range(0f, 1f)]
        [Tooltip("Angular radius of the disk, in radians.")]
        [SerializeField] private float _moonSize = 0.03f;

        [Range(0f, 1f)]
        [Tooltip("Angular width of the disk's soft edge, in radians.")]
        [SerializeField] private float _moonSoftness = 0.005f;

        [Range(0.1f, 2f)]
        [Tooltip("How far the glow spreads around the disk.")]
        [SerializeField] private float _moonGlowSpread = 0.16f;

        [Range(0f, 5f)]
        [SerializeField] private float _moonGlowIntensity = 0.52f;

        // Coverage and sharpness defaults match SkyStylized_001's values.
        [Header("Clouds")]
        [Tooltip("Cloud colour over the day (Night 0%, Dawn 25%, Day 50%, Dusk 75%).")]
        [SerializeField] private Gradient _cloudColor = TimeOfDayGradient(
            new Color(0.16945532f, 0.21358433f, 0.3207547f), new Color(1f, 0.7803922f, 0.6784314f),
            new Color(1f, 1f, 1f), new Color(1f, 0.5787864f, 0.4433962f));

        [Tooltip("Cloud shadow colour over the day (Night 0%, Dawn 25%, Day 50%, Dusk 75%).")]
        [SerializeField] private Gradient _cloudShadowColor = TimeOfDayGradient(
            new Color(0.054512277f, 0.07388825f, 0.11792451f), new Color(0.759434f, 0.6126497f, 0.5444999f),
            new Color(0.764151f, 0.764151f, 0.764151f), new Color(0.3254717f, 0.23717444f, 0.20879316f));

        [Range(0f, 2f)]
        [SerializeField] private float _dayCloudCoverage = 0.26f;

        [Range(0f, 2f)]
        [SerializeField] private float _nightCloudCoverage = 0.26f;

        [Range(0f, 1f)]
        [SerializeField] private float _dayCloudSharpness = 0.864f;

        [Range(0f, 1f)]
        [SerializeField] private float _nightCloudSharpness = 0.864f;

        private static readonly int PropSunMoonColor = Shader.PropertyToID("_SunMoonColor");
        private static readonly int PropSunMoonSize = Shader.PropertyToID("_SunMoonSize");
        private static readonly int PropSunMoonSoftness = Shader.PropertyToID("_SunMoonSoftness");
        private static readonly int PropSunMoonGlowSpread = Shader.PropertyToID("_SunMoonGlowSpread");
        private static readonly int PropSunMoonGlowIntensity = Shader.PropertyToID("_SunMoonGlowIntensity");
        private static readonly int PropZenithColor = Shader.PropertyToID("_ZenithColor");
        private static readonly int PropHorizonColor = Shader.PropertyToID("_HorizonColor");
        private static readonly int PropGroundColor = Shader.PropertyToID("_GroundColor");
        private static readonly int PropCloudColor = Shader.PropertyToID("_CloudColor");
        private static readonly int PropCloudShadowColor = Shader.PropertyToID("_CloudShadowColor");
        private static readonly int PropCloudCoverage = Shader.PropertyToID("_CloudCoverage");
        private static readonly int PropCloudSharpness = Shader.PropertyToID("_CloudSharpness");

        // MoonColor from the original sky material, used for the moon gradient's defaults.
        private static readonly Color OldMoonColor = new Color(1f, 0.9914736f, 0.9339623f);

        private Material _instanceMaterial;
        private Material _instanceSource;

        /// <summary>Raised when this controller's own settings are edited (not when the time of day or night blend changes).</summary>
        public event System.Action Changed;

        /// <summary>The material asset this controller drives.</summary>
        public Material SkyMaterial => _skyMaterial;

        /// <summary>The material actually being rendered: this controller's instance of the asset, while enabled.</summary>
        public Material ActiveMaterial => _instanceMaterial;

        /// <summary>Time of day in hours (0 to 24); 0/24 = night, 6 = dawn, 12 = day, 18 = dusk.</summary>
        public float TimeOfDay
        {
            get => _timeOfDay;
            set
            {
                float hours = Mathf.Repeat(value, 24f);
                if (hours == _timeOfDay) return;
                _timeOfDay = hours;
                Apply();
            }
        }

        /// <summary>0 when the main light is the sun, 1 when it is the moon.</summary>
        public float NightBlend
        {
            get => _nightBlend;
            set
            {
                float blend = Mathf.Clamp01(value);
                if (blend == _nightBlend) return;
                _nightBlend = blend;
                Apply();
            }
        }

        // The material has a single sun-moon set; these are the sun and moon values blended for the current mode.
        public float SunMoonSize => Mathf.Lerp(_sunSize, _moonSize, _nightBlend);
        public float SunMoonSoftness => Mathf.Lerp(_sunSoftness, _moonSoftness, _nightBlend);
        public float SunMoonGlowSpread => Mathf.Lerp(_sunGlowSpread, _moonGlowSpread, _nightBlend);
        public float SunMoonGlowIntensity => Mathf.Lerp(_sunGlowIntensity, _moonGlowIntensity, _nightBlend);

        public Color SunColor => EvaluateSunColor(_timeOfDay);
        public Color MoonColor => EvaluateMoonColor(_timeOfDay);

        /// <summary>The moon's full night-time colour (the gradient's Night key).</summary>
        public Color NightMoonColor => EvaluateMoonColor(0f);

        // The shader boosts the disk 2.5x for the sun; the moon is pre-dimmed by that much (in linear) so it
        // appears at its own colour, as it did when the moon had its own shader property.
        public Color SunMoonColor => Color.Lerp(SunColor, ScaleLinear(MoonColor, 0.4f), _nightBlend);
        public Color ZenithColor => Evaluate(_zenithColor, _timeOfDay);
        public Color HorizonColor => Evaluate(_horizonColor, _timeOfDay);
        public Color GroundColor => Evaluate(_groundColor, _timeOfDay);
        public Color CloudColor => Evaluate(_cloudColor, _timeOfDay);
        public Color CloudShadowColor => Evaluate(_cloudShadowColor, _timeOfDay);

        // Day and night cloud settings, blended for the current mode like the sun and moon.
        public float CloudCoverage => Mathf.Lerp(_dayCloudCoverage, _nightCloudCoverage, _nightBlend);
        public float CloudSharpness => Mathf.Lerp(_dayCloudSharpness, _nightCloudSharpness, _nightBlend);

        /// <summary>Sun colour at the given time of day in hours.</summary>
        public Color EvaluateSunColor(float timeOfDay) => Evaluate(_sunColor, timeOfDay);

        /// <summary>Moon colour at the given time of day in hours.</summary>
        public Color EvaluateMoonColor(float timeOfDay) => Evaluate(_moonColor, timeOfDay);

        private void Reset()
        {
            Material skybox = RenderSettings.skybox;
            if (skybox != null && (skybox.hideFlags & HideFlags.DontSave) == 0)
            {
                _skyMaterial = skybox;
            }
        }

        private void OnEnable()
        {
#if UNITY_EDITOR
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
#endif
            Apply();
        }

        private void OnValidate()
        {
            _timeOfDay = Mathf.Repeat(_timeOfDay, 24f);
            // OnValidate also runs on disabled components and prefab assets, which never get an OnDisable to
            // destroy an instance made here.
            if (isActiveAndEnabled)
            {
                Apply();
            }
            Changed?.Invoke();
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
#endif
            ReleaseInstance();
        }

        /// <summary>Creates the instance of the sky material (again, if the asset was swapped) and makes it the skybox.</summary>
        private Material EnsureInstance()
        {
            if (_skyMaterial == null)
            {
                ReleaseInstance();
                return null;
            }

            if (_instanceMaterial != null && _instanceSource == _skyMaterial) return _instanceMaterial;

            ReleaseInstance();
            _instanceSource = _skyMaterial;
            _instanceMaterial = new Material(_skyMaterial)
            {
                name = _skyMaterial.name + " (Instance)",
                hideFlags = HideFlags.DontSave
            };
            if (RenderSettings.skybox == _skyMaterial || RenderSettings.skybox == null)
            {
                RenderSettings.skybox = _instanceMaterial;
            }
            return _instanceMaterial;
        }

        private void ReleaseInstance()
        {
            if (_instanceMaterial == null) return;

            if (RenderSettings.skybox == _instanceMaterial)
            {
                RenderSettings.skybox = _instanceSource;
            }
            if (Application.isPlaying)
            {
                Destroy(_instanceMaterial);
            }
            else
            {
                DestroyImmediate(_instanceMaterial);
            }
            _instanceMaterial = null;
            _instanceSource = null;
        }

#if UNITY_EDITOR
        // Saves the scene with the asset as its skybox rather than the unsaved instance, then swaps back.
        private void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
        {
            if (scene == gameObject.scene && _instanceMaterial != null && RenderSettings.skybox == _instanceMaterial)
            {
                RenderSettings.skybox = _instanceSource;
            }
        }

        private void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            if (scene == gameObject.scene && _instanceMaterial != null && RenderSettings.skybox == _instanceSource)
            {
                RenderSettings.skybox = _instanceMaterial;
            }
        }
#endif

        /// <summary>
        /// Writes the current colours and sun-moon settings to the active sky material. Only properties whose
        /// value differs are written, so the skybox (and anything lit from it) is left alone when nothing changed.
        /// </summary>
        public void Apply()
        {
            Material mat = EnsureInstance();
            if (mat == null) return;

            SetColorIfChanged(mat, PropSunMoonColor, SunMoonColor);
            SetColorIfChanged(mat, PropZenithColor, ZenithColor);
            SetColorIfChanged(mat, PropHorizonColor, HorizonColor);
            SetColorIfChanged(mat, PropGroundColor, GroundColor);
            SetColorIfChanged(mat, PropCloudColor, CloudColor);
            SetColorIfChanged(mat, PropCloudShadowColor, CloudShadowColor);
            SetFloatIfChanged(mat, PropCloudCoverage, CloudCoverage);
            SetFloatIfChanged(mat, PropCloudSharpness, CloudSharpness);
            SetFloatIfChanged(mat, PropSunMoonSize, SunMoonSize);
            SetFloatIfChanged(mat, PropSunMoonSoftness, SunMoonSoftness);
            SetFloatIfChanged(mat, PropSunMoonGlowSpread, SunMoonGlowSpread);
            SetFloatIfChanged(mat, PropSunMoonGlowIntensity, SunMoonGlowIntensity);
        }

        private static void SetColorIfChanged(Material mat, int property, Color value)
        {
            if (!mat.GetColor(property).Equals(value))
            {
                mat.SetColor(property, value);
            }
        }

        private static void SetFloatIfChanged(Material mat, int property, float value)
        {
            if (mat.GetFloat(property) != value)
            {
                mat.SetFloat(property, value);
            }
        }

        private static Color Evaluate(Gradient gradient, float timeOfDay)
        {
            return gradient != null ? gradient.Evaluate(Mathf.Repeat(timeOfDay, 24f) / 24f) : Color.black;
        }

        /// <summary>Scales a (gamma-space) colour's brightness in linear space, keeping alpha at 1.</summary>
        private static Color ScaleLinear(Color color, float scale)
        {
            return new Color(
                Mathf.LinearToGammaSpace(Mathf.GammaToLinearSpace(color.r) * scale),
                Mathf.LinearToGammaSpace(Mathf.GammaToLinearSpace(color.g) * scale),
                Mathf.LinearToGammaSpace(Mathf.GammaToLinearSpace(color.b) * scale));
        }

        private static Gradient TimeOfDayGradient(Color night, Color dawn, Color day, Color dusk)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(night, 0f),
                    new GradientColorKey(dawn, 0.25f),
                    new GradientColorKey(day, 0.5f),
                    new GradientColorKey(dusk, 0.75f),
                    new GradientColorKey(night, 1f)
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }
    }
}
