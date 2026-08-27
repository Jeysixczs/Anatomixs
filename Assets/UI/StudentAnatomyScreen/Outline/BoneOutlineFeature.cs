using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// Screen-space silhouette outline for the currently selected bone.
//
// Render Graph implementation (this project has Compatibility Mode
// disabled - so every pass here is written against
// RecordRenderGraph/TextureHandle instead of RTHandle/CommandBuffer).
//
// Pipeline:
//
//   Selected Bone -> Selection Mask -> Screen-Space RT -> Edge Detection
//                  -> Thin Outline -> Overlay on final camera image
//
// IMPORTANT - single-pass-object design:
// Mask-write, scene-color-copy, and composite all happen inside ONE
// ScriptableRenderPass's RecordRenderGraph method (BoneOutlinePass below),
// as three render-graph sub-passes added back to back. This is deliberate:
// Render Graph calls every enqueued pass's RecordRenderGraph first (to
// build the dependency graph) and only executes afterwards, and the order
// those RecordRenderGraph calls happen in is NOT guaranteed to match
// EnqueuePass order. The previous version stored the mask's TextureHandle
// on a mutable property (SelectionMaskPass.MaskHandle) and read it from a
// second, independently-enqueued pass object - if the composite pass's
// RecordRenderGraph ran before the mask pass's had run for that frame,
// MaskHandle still held a stale/default handle. It still passed
// TextureHandle.IsValid() (a plain non-negative index check) but didn't
// belong to this frame's graph, so Render Graph silently substituted the
// default black texture at execution - the mask sampled as pure black,
// so the outline never appeared, with no error anywhere.
// Keeping every TextureHandle a local variable inside one RecordRenderGraph
// call - never stored back on `this` or handed to another pass object -
// makes that class of bug structurally impossible: a handle is only ever
// read within the exact call that created it.
//
// Zero passes/zero cost when nothing is selected - see AddRenderPasses.
public class BoneOutlineFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        [Tooltip("Material using Hidden/Anatomia3D/BoneOutlineMask. Stamps the selected bone's silhouette into the mask render target - never assign anything else here, this material must never write to the camera color target.")]
        public Material maskMaterial;

        [Tooltip("Material using Hidden/Anatomia3D/BoneOutlineComposite. Reads the mask, detects its edge, and overlays the outline on the camera image.")]
        public Material compositeMaterial;

        [Tooltip("Outline thickness in mask-texture texels. Purely a screen-space size - stays visually the same thin width regardless of the selected bone's world size, camera distance, or zoom.")]
        [Range(0.5f, 8f)] public float outlineWidthPixels = 2f;

        [Tooltip("Outline color, including alpha (alpha 1 = fully opaque line).")]
        public Color outlineColor = new Color(1f, 0.82f, 0.15f, 1f);

        [Tooltip("Renders the selection mask at 1/N screen resolution to save mobile fill-rate. 1 = full res (crispest edge), 2-3 is usually indistinguishable for a thin outline and noticeably cheaper.")]
        [Range(1, 4)] public int maskDownsample = 2;

      
    }

    public Settings settings = new Settings();

    // Set by BoneOutlineController every time the bone selection changes -
    // see SetSelectedRenderers/ClearSelectedRenderers below. Deliberately
    // just a List<Renderer> of EXACTLY what the controller resolved (the
    // clicked GameObject's own renderer, in the normal case) - this feature
    // never widens the selection by name, layer, or hierarchy on its own.
    private readonly List<Renderer> _selectedRenderers = new List<Renderer>();

    // Self-registers so BoneOutlineController (a plain MonoBehaviour) can
    // reach this renderer-feature sub-asset without the project having to
    // wire up a direct Inspector reference to an asset nested inside the
    // Universal Renderer Data asset.
    public static BoneOutlineFeature Instance { get; private set; }

    private BoneOutlinePass _pass;

    public void SetSelectedRenderers(IReadOnlyList<Renderer> renderers)
    {
        _selectedRenderers.Clear();
        if (renderers != null) _selectedRenderers.AddRange(renderers);
    }

    public void ClearSelectedRenderers()
    {
        _selectedRenderers.Clear();
    }

    public override void Create()
    {
        Instance = this;
        _pass = new BoneOutlinePass(_selectedRenderers)
        {
            // Must be BeforeRenderingPostProcessing, not AfterRenderingOpaques.
            // The mask sub-pass has no timing dependency of its own (ZTest
            // Always, doesn't read scene color) so it's free to run wherever
            // this single shared event puts it - but the composite sub-pass
            // writes straight onto camera color, and that write has to land
            // AFTER URP's transparent queue, not just after opaques. A bone
            // with an alpha-blended material slot (e.g. Bone + Cartilage)
            // renders its transparent slot after opaques but before
            // post-processing; if the composite ran at AfterRenderingOpaques
            // it would draw the outline and then have the transparent pass
            // paint right over it before the frame ever reaches the screen -
            // the pass still executes every frame and the mask still has
            // real data, so nothing about that failure shows up as an error
            // anywhere.
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing
        };

  
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.maskMaterial == null || settings.compositeMaterial == null)
        {
           
            return;
        }

        if (_selectedRenderers.Count == 0)
        {
           
            return; // nothing selected - skip the pass entirely, zero added cost
        }

        if (renderingData.cameraData.cameraType != CameraType.Game &&
            renderingData.cameraData.cameraType != CameraType.SceneView)
        {
            
            return;
        }

        _pass.Setup(settings);
        renderer.EnqueuePass(_pass);

     
    }

    protected override void Dispose(bool disposing)
    {
        if (Instance == this) Instance = null;
        base.Dispose(disposing);
    }

    // ===================================================================
    // The whole outline pipeline as ONE ScriptableRenderPass:
    //
    //   1. Selection Mask (unsafe pass) - draws exactly the selected
    //      bone's Renderer(s) into a small dedicated mask render-graph
    //      texture, ignoring scene depth/occlusion (BoneOutlineMask.shader
    //      uses ZTest Always, so the mask captures the bone's full
    //      screen-space silhouette even where another bone is currently
    //      drawn in front of it in the real scene).
    //
    //   2. Copy Scene Color (raster pass) - copies the camera color into a
    //      temp texture. Needed because a shader can never safely read and
    //      write the same render target in one draw, and sub-pass 3 needs
    //      to both sample the scene-so-far and write back onto it.
    //
    //   3. Composite (raster pass) - fullscreen pass using
    //      BoneOutlineComposite.shader, reading the temp color copy + the
    //      selection mask, and writing the result back onto the real
    //      camera color target. It only ever changes pixels that sit on
    //      the mask's screen-space edge - the selected bone's own
    //      material/color and every other bone are left completely
    //      untouched.
    //
    // All three sub-passes are added inside a single RecordRenderGraph
    // call, and every TextureHandle they share (maskTex, tempColor) is a
    // plain local variable - see the header comment on BoneOutlineFeature
    // for why that matters.
    // ===================================================================
    private class BoneOutlinePass : ScriptableRenderPass
    {
        private class MaskPassData
        {
            public List<Renderer> renderers;
            public Material maskMaterial;
            public TextureHandle mask;
        }

        private class CopyPassData
        {
            public TextureHandle source;
        }

        private class CompositePassData
        {
            public TextureHandle source;
            public TextureHandle mask;
            public Material material;
            public float outlineWidth;
            public Color outlineColor;
            public Vector2Int maskSize;
       
        }

        private readonly List<Renderer> _renderers;
        private Settings _settings;

        public BoneOutlinePass(List<Renderer> renderers)
        {
            _renderers = renderers;
            profilingSampler = new ProfilingSampler("Bone Outline");
            // Without this, URP may render straight to the backbuffer when nothing
            // else (e.g. Post Processing) forces an intermediate color target - and
            // this pass has nothing valid to read/write in that case (see
            // UniversalResourceData.isActiveTargetBackBuffer check below). Declaring
            // that this pass needs to read Color forces URP to always allocate an
            // offscreen camera color texture, regardless of camera/post-process setup.
            ConfigureInput(ScriptableRenderPassInput.Color);
        }

        public void Setup(Settings settings) => _settings = settings;

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_renderers == null || _renderers.Count == 0 || _settings?.maskMaterial == null || _settings?.compositeMaterial == null)
            {
                
                return;
            }

            var cameraData = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();

            if (resourceData.isActiveTargetBackBuffer)
            {
            
                return; // nothing to composite onto if there's no offscreen camera color target
            }

            // ---- Sub-pass 1: selection mask ----
            var maskDesc = new TextureDesc(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height)
            {
                colorFormat = GraphicsFormat.R8_UNorm,
                depthBufferBits = 0,
                msaaSamples = MSAASamples.None,
                clearBuffer = true,
                clearColor = Color.clear,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "_BoneSelectionMask"
            };
            int downsample = Mathf.Max(1, _settings.maskDownsample);
            maskDesc.width = Mathf.Max(1, maskDesc.width / downsample);
            maskDesc.height = Mathf.Max(1, maskDesc.height / downsample);

            TextureHandle maskTex = renderGraph.CreateTexture(maskDesc);

        

            using (var builder = renderGraph.AddUnsafePass<MaskPassData>("Bone Outline: Selection Mask", out var maskPassData, profilingSampler))
            {
                maskPassData.renderers = _renderers;
                maskPassData.maskMaterial = _settings.maskMaterial;
                maskPassData.mask = maskTex;

                builder.UseTexture(maskTex, AccessFlags.Write);
                // This sub-pass's real work (drawing external Renderer objects)
                // isn't visible to the graph's automatic dependency tracking
                // the way a texture read/write is - keep it from being
                // culled as a no-op.
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((MaskPassData data, UnsafeGraphContext ctx) =>
                {
                    ctx.cmd.SetRenderTarget(data.mask);
                    ctx.cmd.ClearRenderTarget(false, true, Color.clear);

                    foreach (var r in data.renderers)
                    {
                        if (r == null || !r.enabled) continue;

                        // One draw per submesh/material slot - a bone split
                        // into several material slots (e.g. Bone/Cartilage)
                        // must have every slot contribute to the silhouette,
                        // not just slot 0.
                        int subMeshCount = Mathf.Max(1, r.sharedMaterials.Length);
                        for (int sub = 0; sub < subMeshCount; sub++)
                            ctx.cmd.DrawRenderer(r, data.maskMaterial, sub, 0);
                    }

             
                });
            }

            // ---- Sub-pass 2: copy the scene-so-far into a temp texture ----
            TextureDesc tempDesc = renderGraph.GetTextureDesc(resourceData.cameraColor);
            tempDesc.name = "_BoneOutlineTempColor";
            tempDesc.clearBuffer = false;
            tempDesc.depthBufferBits = 0;
            tempDesc.msaaSamples = MSAASamples.None;
            TextureHandle tempColor = renderGraph.CreateTexture(tempDesc);

            using (var builder = renderGraph.AddRasterRenderPass<CopyPassData>("Bone Outline: Copy Scene Color", out var copyData, profilingSampler))
            {
                copyData.source = resourceData.cameraColor;
                builder.UseTexture(copyData.source, AccessFlags.Read);
                builder.SetRenderAttachment(tempColor, 0, AccessFlags.Write);

                builder.SetRenderFunc((CopyPassData data, RasterGraphContext ctx) =>
                {
                    Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), 0, false);
                });
            }

            // ---- Sub-pass 3: fullscreen edge-detect + overlay, temp -> real camera color ----
            using (var builder = renderGraph.AddRasterRenderPass<CompositePassData>("Bone Outline: Composite", out var passData, profilingSampler))
            {
                passData.source = tempColor;
                passData.mask = maskTex;
                passData.material = _settings.compositeMaterial;
                passData.outlineWidth = _settings.outlineWidthPixels;
                passData.outlineColor = _settings.outlineColor;
                passData.maskSize = new Vector2Int(maskDesc.width, maskDesc.height);
             

                builder.UseTexture(passData.source, AccessFlags.Read);
                builder.UseTexture(passData.mask, AccessFlags.Read);
                builder.SetRenderAttachment(resourceData.cameraColor, 0, AccessFlags.Write);
                // Required because SetRenderFunc below calls ctx.cmd.SetGlobalTexture/
                // SetGlobalFloat/SetGlobalColor to feed the composite shader - raster
                // passes disallow global-state writes by default and throw
                // "Modifying global state from this command buffer is not allowed"
                // at execute time otherwise.
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((CompositePassData data, RasterGraphContext ctx) =>
                {
                    // Fed to the shader as plain global shader variables
                    // (not Material.Set*) - see the comment at the top of
                    // BoneOutlineComposite.shader for why.
                    ctx.cmd.SetGlobalTexture("_MaskTex", data.mask);
                    ctx.cmd.SetGlobalVector("_MaskTex_TexelSize",
                        new Vector4(1f / Mathf.Max(1, data.maskSize.x), 1f / Mathf.Max(1, data.maskSize.y), 0, 0));
                    ctx.cmd.SetGlobalFloat("_OutlineWidth", data.outlineWidth);
                    ctx.cmd.SetGlobalColor("_OutlineColor", data.outlineColor);
                  

                    Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, 0);
                });
            }
        }
    }
}
