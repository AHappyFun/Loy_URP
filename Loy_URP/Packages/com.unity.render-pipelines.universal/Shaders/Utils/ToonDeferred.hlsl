// Loy_Toon 卡通材质的延迟光照实现（供 StencilDeferred.hlsl / ClusterDeferred.hlsl 共用）。
//
// GBuffer 约定：GBuffer0/1/2 保持 URP 标准 Metallic PBR 布局。
// 独立 CustomData RGBA：
//   R = 漫反射色阶阈值
//   G = 漫反射色阶软度
//   B = GGX 高光归一化后的阈值
//   A = GGX 高光色阶软度
//
// 只有当 materialFlags & kMaterialFlagToon 时才走本函数，其余材质保持标准 PBR。
#ifndef URP_TOON_DEFERRED_INCLUDED
#define URP_TOON_DEFERRED_INCLUDED

half3 ToonDeferredLighting(BRDFData brdfData, GBufferData gBufferData, Light light, half3 normalWS, half3 viewDirectionWS)
{
    half NoL = saturate(dot(normalWS, light.direction));
    half attenuation = light.distanceAttenuation * light.shadowAttenuation;
    half3 attenuatedLightColor = light.color * attenuation;

    // 分段的是光照响应，不是 PBR 材质数据。软度显式存进 CustomData，
    // 避免在 Cluster Deferred 的可变灯光循环中使用屏幕导数。
    half diffuseThreshold = gBufferData.customData.r;
    half diffuseWidth = max(gBufferData.customData.g, 2.0h / 255.0h);
    half toonDiffuse = smoothstep(
        diffuseThreshold - diffuseWidth,
        diffuseThreshold + diffuseWidth,
        NoL);
    half3 color = brdfData.diffuse * attenuatedLightColor * toonDiffuse;

    // 先计算 URP 的 GGX/Cook-Torrance 高光，再对 lobe 做卡通阈值化。
    // roughness、F0、metallic 和视角响应都来自标准 BRDFData。
    half pbrSpecular = DirectBRDFSpecular(brdfData, normalWS, light.direction, viewDirectionWS);
    half specularSignal = pbrSpecular * rcp(pbrSpecular + 1.0h);
    half specularThreshold = gBufferData.customData.b;
    half specularWidth = max(gBufferData.customData.a, 2.0h / 255.0h);
    half toonSpecular = smoothstep(
        specularThreshold - specularWidth,
        specularThreshold + specularWidth,
        specularSignal);

    // NoL 保留高光在光照半球内的物理衰减；色阶只改变 GGX lobe 的视觉边界。
    color += brdfData.specular * attenuatedLightColor * toonSpecular * NoL;
    return color;
}

#endif // URP_TOON_DEFERRED_INCLUDED
