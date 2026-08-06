// Loy_Toon 卡通材质的延迟光照实现（供 StencilDeferred.hlsl / ClusterDeferred.hlsl 共用）。
//
// GBuffer 约定（金属流布局，reflectivity=0）：
//   GBuffer0.rgb = baseColor          GBuffer0.a = materialFlags（含 kMaterialFlagToon）
//   GBuffer1.r   = 0 (reflectivity)
//   GBuffer1.g   = 漫反射色阶阈值（NdotL 低于此值进入暗部）
//   GBuffer1.b   = 卡通高光强度
//   GBuffer1.a   = occlusion
//   GBuffer2.rgb = 编码法线            GBuffer2.a = 卡通高光大小（复用 smoothness 槽）
//
// 只有当 materialFlags & kMaterialFlagToon 时才走本函数，其余材质保持标准 PBR。
#ifndef URP_TOON_DEFERRED_INCLUDED
#define URP_TOON_DEFERRED_INCLUDED

half3 ToonDeferredLighting(BRDFData brdfData, GBufferData gBufferData, Light light, half3 normalWS, half3 viewDirectionWS)
{
    half ndlRaw = dot(normalWS, light.direction);   // 未 saturate，便于诊断方向
    half ndl = saturate(ndlRaw);
    half3 attenuatedLightColor = light.color * (light.distanceAttenuation * light.shadowAttenuation);

    // 漫反射色阶：阈值两侧做小幅 smoothstep，避免硬边锯齿/闪烁
    half diffStep = gBufferData.specularColor.g;
    half diffRamp = smoothstep(diffStep - 0.02h, diffStep + 0.02h, ndl);

    half3 color = brdfData.diffuse * attenuatedLightColor * diffRamp;

    // 卡通高光：ndh 阈值窗口。GBuffer2.a 越小高光越集中。
    half3 halfDir = SafeNormalize(light.direction + viewDirectionWS);
    half ndh = saturate(dot(normalWS, halfDir));
    half specSize = gBufferData.smoothness;
    half specEdge = lerp(0.98h, 0.45h, specSize);       // 0.98 = 小而锐利，0.45 = 大而柔
    half spec = smoothstep(specEdge - 0.02h, specEdge + 0.02h, ndh);
    half specIntensity = gBufferData.specularColor.b;
    color += attenuatedLightColor * spec * specIntensity * diffRamp;

    // === 诊断模式：把 raw N·L 当颜色输出 ===
    // 朝光面(ndl>0) = 红，背光面(ndl<0) = 绿。
    // 若朝光面显示绿色 → 法线或 light.direction 反了。
    // 确认方向正确后删除这一段，恢复 return color;
    //return ndlRaw > 0 ? half3(ndlRaw, 0, 0) : half3(0, -ndlRaw, 0);

    return color;
}

#endif // URP_TOON_DEFERRED_INCLUDED
