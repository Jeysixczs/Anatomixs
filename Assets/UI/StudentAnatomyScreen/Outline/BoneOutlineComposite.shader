// Fullscreen composite pass for the screen-space bone outline system.
//
// Input: the selection mask produced by BoneOutlineMask.shader (a
// screen-space texture where every pixel covered by the selected bone's
// silhouette, from the current camera, is 1 - everywhere else is 0).
//
// This shader does NOT re-touch the selected bone's own pixels in the
// camera image at all. It only looks for pixels that sit on the BOUNDARY
// of the mask (some samples in the local neighborhood are inside the
// silhouette, some are outside) and tints just those boundary pixels with
// _OutlineColor. Everywhere else the scene color is passed through
// untouched. That is what keeps this a thin line rather than a fill:
//   - Deep inside the silhouette: all neighborhood samples are 1 -> not an edge.
//   - Deep outside the silhouette: all neighborhood samples are 0 -> not an edge.
//   - Only right at the rim: neighborhood samples disagree -> edge -> outline color.
//
// Because the mask itself already encodes "this pixel is part of the
// selected bone's silhouette, occluded or not" (see BoneOutlineMask.shader,
// drawn with ZTest Always), this same edge test finds BOTH the normally
// visible part of the outline AND the part that would otherwise be hidden
// behind an occluding bone - so no separate occluded-color pass is needed
// the way the old inverted-hull shader required.
//
// _OutlineWidth is expressed in mask-texture texels, not world units, so
// the line reads as the same thin thickness on screen regardless of the
// bone's size, distance from camera, or camera zoom - purely a screen-space
// property, matching the "camera-facing silhouette" requirement.
Shader "Hidden/Anatomia3D/BoneOutlineComposite"
{
    // No Properties block here on purpose. _MaskTex, _OutlineColor, and
    // _OutlineWidth are fed in per-frame as plain global shader variables
    // via CommandBuffer.SetGlobalTexture/Float/Color from
    // BoneOutlineFeature.OutlineCompositePass (see the comment on the HLSL
    // declarations below). If they were declared in a Properties block,
    // Unity would treat them as per-material slots, and Blitter.BlitTexture
    // binds the *material's own* value for any property that has a material
    // slot - which for these was never set, so it falls back to the
    // Properties block's default ("black" {}) and silently overrides the
    // global binding every draw. That was the actual bug behind the "mask
    // reads real data but composite always samples black" symptom: keep
    // this Properties-less so the plain globals below are the only source.
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "BoneOutlineComposite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            // Blit.hlsl (below) pulls in URP's full shader library surface -
            // TextureXR macros, Packing.hlsl (UnpackNormalOctQuadEncode),
            // etc. Including only Common.hlsl isn't enough; URP's own
            // Core.hlsl is what actually brings in everything Blit.hlsl
            // needs, same as Unity's own fullscreen/post-process shaders.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Provides the standard URP fullscreen-triangle Vert() plus
            // _BlitTexture (a copy of the camera color taken just before
            // this pass - see BoneOutlineFeature.OutlineCompositePass) and
            // its matching sampler_LinearClamp.
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // Set every frame via CommandBuffer.SetGlobalTexture/Float/Color
            // from BoneOutlineFeature.OutlineCompositePass - NOT via
            // Material.SetTexture/SetColor. The render-graph pass that binds
            // these can execute off the main thread, so mutating the shared
            // Material asset's properties from there is unsafe; plain global
            // shader variables (no UnityPerMaterial CBUFFER here) are the
            // correct way to feed a render-graph pass's per-frame values in.
            TEXTURE2D(_MaskTex);
            SAMPLER(sampler_MaskTex);
            float4 _MaskTex_TexelSize; // x,y = 1/width, 1/height of the mask texture
            half4 _OutlineColor;
            float _OutlineWidth;

            // ===== OUTLINE DEBUG (safe to remove) =====
            // Set from BoneOutlineFeature.Settings.debugFillSilhouette via
            // OutlineCompositePass - see OutlineDebug.cs. When >0.5, Frag()
            // below fills the whole silhouette instead of just its thin edge,
            // as a diagnostic to prove whether this pass's output is reaching
            // the display at all. Defaults to 0 (normal thin-edge behavior)
            // whenever the debug system/toggle is off, so this never changes
            // default behavior. To fully remove: delete this variable and the
            // "OUTLINE DEBUG" branch inside Frag() below.
            float _OutlineDebugFillMode;

            half SampleMask(float2 uv)
            {
                return SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, uv).r;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                half4 sceneRGBA = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                half3 scene = sceneRGBA.rgb;

                float2 texel = _MaskTex_TexelSize.xy * max(_OutlineWidth, 0.0001);

                // 8-neighbor sample ring at the outline-width offset. If the
                // ring is not uniform (some samples inside the silhouette,
                // some outside), this pixel sits on the silhouette's rim.
                half m0 = SampleMask(uv + float2( texel.x,  0));
                half m1 = SampleMask(uv + float2(-texel.x,  0));
                half m2 = SampleMask(uv + float2( 0,  texel.y));
                half m3 = SampleMask(uv + float2( 0, -texel.y));
                half m4 = SampleMask(uv + float2( texel.x,  texel.y));
                half m5 = SampleMask(uv + float2(-texel.x,  texel.y));
                half m6 = SampleMask(uv + float2( texel.x, -texel.y));
                half m7 = SampleMask(uv + float2(-texel.x, -texel.y));

                half maxV = max(m0, max(m1, max(m2, max(m3, max(m4, max(m5, max(m6, m7)))))));
                half minV = min(m0, min(m1, min(m2, min(m3, min(m4, min(m5, min(m6, m7)))))));

                half isEdge = step(0.5, maxV - minV); // 1 where the ring straddles the silhouette boundary

                // ===== OUTLINE DEBUG (safe to remove) =====
                // See _OutlineDebugFillMode declaration above. Off (0) by
                // default - isEdge is used exactly as before in that case, so
                // this branch is a no-op unless the Inspector toggle is on.
                half centerMask = SampleMask(uv);
                half highlight = lerp(isEdge, step(0.5, centerMask), step(0.5, _OutlineDebugFillMode));

                half3 result = lerp(scene, _OutlineColor.rgb, highlight * _OutlineColor.a);
                // Preserve the scene's original alpha everywhere except the
                // outline rim itself - this pass composites onto camera color
                // that may be alpha-blended with UI behind it (e.g. a RawImage
                // showing this camera's output over a UI panel). Forcing alpha
                // to 1 on every pixel - not just the outline - would make the
                // whole frame opaque and hide whatever was meant to show
                // through the camera's background alpha.
                half resultAlpha = lerp(sceneRGBA.a, 1, highlight * _OutlineColor.a);
                return half4(result, resultAlpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
