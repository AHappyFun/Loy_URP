# Custom post-processing

`CustomPostProcessingRenderFeature` is the single URP renderer feature for project-specific post effects.
It executes active effect renderers in registration order and forwards each output to the next effect.
The current order is Outline, then Streak.

## Outline

Configure Outline directly on `CustomPostProcessingRenderFeature`. It contains the same edge detection,
distance fade and styling controls as the former standalone feature.

## Streak

Streak is a URP RenderGraph adaptation of keijiro/Kino's horizontally stretched bloom effect.

1. Keep only `CustomPostProcessingRenderFeature` on the active Universal Renderer Data.
2. Configure Streak directly on that feature and keep `Enabled` checked.
3. Set `Intensity` above zero. HDR highlights above `Threshold` generate the streaks.

This custom stack does not use Unity's Volume system and does not depend on the camera's
**Post Processing** checkbox.

The feature runs before URP's built-in post-processing by default, matching Kino's original injection point.
To add another effect later, implement `ICustomPostProcessRenderer` and register it in the
registration block in `CustomPostProcessingRenderFeature.Create`.
