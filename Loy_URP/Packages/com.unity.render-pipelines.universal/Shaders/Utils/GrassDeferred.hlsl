// Loy_Grass 草地材质的延迟光照实现（供 StencilDeferred.hlsl / ClusterDeferred.hlsl 共用）。
//
// 写实草地的两个关键效果：
//   ① 次表面散射/透光：草叶薄且透光，逆光时叶子被光穿透发亮。
//      用 wrap lighting（NoL 半域偏置）+ 透光系数实现。
//   ② 宽阔 sheen 高光：太阳角度下草地那种大片油亮的闪光带，
//      用宽柔和高光项（低 shininess 的 specular lobe）叠加，比标准 GGX 点状高光更像草地。
//
// GBuffer 约定：GBuffer0/1/2 保持 URP 标准 Metallic PBR 布局（albedo/normal/smoothness）。
// CustomData RGBA（由草地 GBuffer pass 写入）：
//   R = translucency（透光强度，0~1）
//   G = sheen strength（高光带强度，0~1）
//   B = sheen power（高光带宽窄，越大越窄越亮，建议 2~8）
//   A = ambient strength（环境光强度）
//
// 只有当 materialFlags & kMaterialFlagGrass 时才走本函数，其余材质保持标准 PBR。
#ifndef URP_GRASS_DEFERRED_INCLUDED
#define URP_GRASS_DEFERRED_INCLUDED

half3 GrassDeferredLighting(BRDFData brdfData, GBufferData gBufferData, Light light, half3 normalWS, half3 viewDirectionWS)
{
    half translucency = gBufferData.customData.r;
    half sheenStrength = gBufferData.customData.g;
    half sheenPower = max(gBufferData.customData.b * 16.0h, 1.0h); // customData 是 UNorm，存储时除以 16，这里还原
    // customData.a = ambient（已归一化），延迟光照阶段的环境光已在 GBuffer pass 写入 GI buffer，这里不用。

    half3 attenuatedLightColor = light.color * (light.distanceAttenuation * light.shadowAttenuation);

    half3 lightDir = light.direction;

    // ① 次表面散射：wrap lighting + 逆光透光
    // wrap：NoL = (dot(N,L) + wrap) / (1 + wrap)，让背光面也有柔和光照
    half wrap = 0.5h;
    half NoL_wrap = saturate((dot(normalWS, lightDir) + wrap) / (1.0h + wrap));

    // 透光：视线方向与光的夹角，逆光时（光从草叶背面穿过来）最强
    // 用 -lightDir 与 viewDir 的点积衡量"光从背后射向视线"的程度
    half translit = saturate(dot(-lightDir, viewDirectionWS));
    translit = pow(translit, 2.0h); // 收窄一点，避免全场景过亮

    half3 diffuse = brdfData.diffuse * attenuatedLightColor * NoL_wrap;
    diffuse += brdfData.diffuse * attenuatedLightColor * translit * translucency;

    // ② 宽阔 sheen 高光：宽柔和高光带，模拟草叶群整体的油亮感
    // 用 Blinn-Phong 式的宽高光（低 shininess），比标准 GGX 更柔更宽
    half3 halfDir = normalize(lightDir + viewDirectionWS);
    half NoH = saturate(dot(normalWS, halfDir));
    half sheen = pow(NoH, sheenPower);
    half3 specular = brdfData.specular * attenuatedLightColor * sheen * sheenStrength;

    return diffuse + specular;
}

#endif // URP_GRASS_DEFERRED_INCLUDED
