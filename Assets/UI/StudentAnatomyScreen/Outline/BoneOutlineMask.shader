// Selection-mask shader for the screen-space bone outline system.
//
// Draws ONLY into the dedicated mask render target (never into the camera's
// color buffer - the render feature routes this shader's output there, see
// BoneOutlineFeature.SelectionMaskPass). Every pixel it touches is written
// as full coverage (1.0), regardless of:
//   - ZTest against the scene: Always, so a bone that is fully hidden
//     behind another bone still marks its silhouette in the mask.
//   - Cull: Off, so thin/open geometry (e.g. a sliver of cartilage) marks
//     both its front and back faces rather than leaving gaps.
//   - Its own internal depth complexity: ZWrite Off means overlapping
//     triangles of the SAME selected bone simply OR together into the same
//     "inside the silhouette" result - no self z-fighting artifacts.
//
// This shader never reads or writes bone color, so it can never change how
// the selected bone actually looks in the final image - it only produces
// the mask that the composite pass (BoneOutlineComposite.shader) uses to
// find the silhouette's screen-space edge.
Shader "Hidden/Anatomia3D/BoneOutlineMask"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "SelectionMask"
            ZTest Always
            ZWrite Off
            Cull Off
            ColorMask R
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            // No skinning variant needed here: CommandBuffer.DrawRenderer on
            // a SkinnedMeshRenderer already feeds this shader the
            // already-skinned vertex stream, same as any other shader drawn
            // on that renderer.

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                return half4(1, 0, 0, 0); // ColorMask R - only the red channel is written/used as coverage
            }
            ENDHLSL
        }
    }

    Fallback Off
}
