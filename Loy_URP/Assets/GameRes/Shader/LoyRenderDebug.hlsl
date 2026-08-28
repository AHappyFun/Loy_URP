#ifndef LOY_RENDER_DEBUG_INCLUDED
#define LOY_RENDER_DEBUG_INCLUDED

// Keep these values in sync with LoyRenderDebugMode in LoyRenderDebugWindow.cs.
#define LOY_DEBUG_NONE          0
#define LOY_DEBUG_ALBEDO        1
#define LOY_DEBUG_EMISSION      2
#define LOY_DEBUG_GI            3
#define LOY_DEBUG_NORMAL_WS     4
#define LOY_DEBUG_SMOOTHNESS    5
#define LOY_DEBUG_METALLIC      6
#define LOY_DEBUG_MATERIAL_AO   7
#define LOY_DEBUG_SHADOW        8
#define LOY_DEBUG_SSAO          9

#if defined(LOY_RENDER_DEBUG)
int _LoyRenderDebugMode;

half3 LoyGetSurfaceDebugColor(
    half3 albedo,
    half3 emission,
    half3 gi,
    half3 normalWS,
    half smoothness,
    half metallic,
    half materialAO)
{
    UNITY_BRANCH if (_LoyRenderDebugMode == LOY_DEBUG_ALBEDO)
        return albedo;
    UNITY_BRANCH if (_LoyRenderDebugMode == LOY_DEBUG_EMISSION)
        return emission;
    UNITY_BRANCH if (_LoyRenderDebugMode == LOY_DEBUG_GI)
        return gi;
    UNITY_BRANCH if (_LoyRenderDebugMode == LOY_DEBUG_NORMAL_WS)
        return normalize(normalWS) * 0.5h + 0.5h;
    UNITY_BRANCH if (_LoyRenderDebugMode == LOY_DEBUG_SMOOTHNESS)
        return smoothness.xxx;
    UNITY_BRANCH if (_LoyRenderDebugMode == LOY_DEBUG_METALLIC)
        return metallic.xxx;
    UNITY_BRANCH if (_LoyRenderDebugMode == LOY_DEBUG_MATERIAL_AO)
        return materialAO.xxx;
    UNITY_BRANCH if (_LoyRenderDebugMode == LOY_DEBUG_SHADOW || _LoyRenderDebugMode == LOY_DEBUG_SSAO)
        return 0.0h.xxx;

    return gi + emission;
}
#endif

#endif
