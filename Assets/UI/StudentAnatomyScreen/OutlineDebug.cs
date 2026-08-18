using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// =============================================================================
// OUTLINE DEBUG SYSTEM  (temporary diagnostic tooling - safe to delete)
// -----------------------------------------------------------------------------
// Centralizes every diagnostic log for the screen-space bone outline pipeline:
//
//   Bone Selection -> Renderer/Mesh -> Camera -> URP Renderer Feature
//     -> Render Pass (RecordRenderGraph/Execute) -> Shader -> Game View
//
// This file is 100% additive: it only reads state and calls Debug.Log/
// LogWarning/LogError. It never changes what the outline pipeline actually
// does. All call sites in BoneOutlineController.cs and BoneOutlineFeature.cs
// are wrapped in "#region OUTLINE DEBUG" / "#endregion" blocks that call
// only into this class, so the whole system can be removed by:
//
//   1. Deleting this file, and
//   2. Deleting every "#region OUTLINE DEBUG" ... "#endregion" block in
//      BoneOutlineController.cs and BoneOutlineFeature.cs
//      (search both files for "OutlineDebug." to find every call site).
//
// To just SILENCE it without deleting anything, set OutlineDebug.Enabled = false
// below (or from anywhere else, e.g. a debug menu: OutlineDebug.Enabled = false;).
// =============================================================================
public static class OutlineDebug
{
    /// <summary>Master switch. Flip to false to silence every [OutlineDebug] log without deleting any code.</summary>
    public static bool Enabled = true;

    /// <summary>
    /// RecordRenderGraph/Execute run every frame, so per-frame logs (mask draw,
    /// composite execute) are throttled after the first occurrence for a given
    /// selection: logged once immediately, then once every N frames afterward
    /// as a "still alive" heartbeat. Set to 1 to log literally every frame.
    /// </summary>
    public static int SteadyStateLogInterval = 90;

    private static int _frameCounter;
    private static bool _firstMaskDrawLoggedForSelection;
    private static bool _firstCompositeLoggedForSelection;
    private static bool _firstAddRenderPassesLoggedForSelection;
    private static bool _maskReadbackRequestedForSelection;

    // ---------------------------------------------------------------
    // Low-level helpers
    // ---------------------------------------------------------------
    public static void Log(string tag, string msg)
    {
        if (!Enabled) return;
        Debug.Log($"[OutlineDebug][{tag}] {msg}");
    }

    public static void Warn(string tag, string msg)
    {
        if (!Enabled) return;
        Debug.LogWarning($"[OutlineDebug][{tag}] {msg}");
    }

    public static void Err(string tag, string msg)
    {
        if (!Enabled) return;
        Debug.LogError($"[OutlineDebug][{tag}] {msg}");
    }

    public static string HierarchyPath(Transform t)
    {
        if (t == null) return "<null>";
        var sb = new StringBuilder(t.name);
        var p = t.parent;
        while (p != null)
        {
            sb.Insert(0, p.name + "/");
            p = p.parent;
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------
    // Section 1: selection state (BoneOutlineController.SetSelectedBone)
    // ---------------------------------------------------------------
    public static void LogSelection(Transform selected)
    {
        if (!Enabled) return;
        _firstMaskDrawLoggedForSelection = false;
        _firstCompositeLoggedForSelection = false;
        _firstAddRenderPassesLoggedForSelection = false;
        _maskReadbackRequestedForSelection = false;

        if (selected == null)
        {
            Log("Target", "SetSelectedBone(null) - clearing selection, outline should disappear.");
            return;
        }

        Log("Target",
            $"Selected GameObject='{selected.name}'  path='{HierarchyPath(selected)}'  " +
            $"layer='{LayerMask.LayerToName(selected.gameObject.layer)}'({selected.gameObject.layer})  " +
            $"activeInHierarchy={selected.gameObject.activeInHierarchy}");
    }

    // ---------------------------------------------------------------
    // Section 2: renderer / mesh checks
    // ---------------------------------------------------------------
    public static void LogRendererCandidate(Renderer r, bool kept, string reason)
    {
        if (!Enabled || r == null) return;

        string meshInfo = "n/a";
        if (r is MeshRenderer mr)
        {
            var mf = mr.GetComponent<MeshFilter>();
            meshInfo = (mf != null && mf.sharedMesh != null)
                ? $"MeshFilter.sharedMesh='{mf.sharedMesh.name}' verts={mf.sharedMesh.vertexCount}"
                : "MeshFilter or sharedMesh MISSING";
        }
        else if (r is SkinnedMeshRenderer smr)
        {
            meshInfo = (smr.sharedMesh != null)
                ? $"SkinnedMeshRenderer.sharedMesh='{smr.sharedMesh.name}' verts={smr.sharedMesh.vertexCount}"
                : "sharedMesh MISSING";
        }

        var mats = r.sharedMaterials;
        string matList = "0";
        if (mats != null && mats.Length > 0)
        {
            var names = new string[mats.Length];
            for (int i = 0; i < mats.Length; i++) names[i] = mats[i] == null ? "<null>" : mats[i].name;
            matList = string.Join(", ", names);
        }

        string line =
            $"'{r.name}' type={r.GetType().Name} enabled={r.enabled} " +
            $"materialCount={(mats?.Length ?? 0)} materials=[{matList}] " +
            $"boundsSizeSqr={r.bounds.size.sqrMagnitude:F6} isVisible(lastCull)={r.isVisible} " +
            meshInfo;

        if (kept) Log("Renderer", $"KEPT    {line}");
        else Warn("Renderer", $"SKIPPED [{reason}] {line}");
    }

    public static void LogNoRendererFound(GameObject go, bool usedDescendantFallback)
    {
        Err("Renderer",
            $"'{go.name}' - no usable MeshRenderer/SkinnedMeshRenderer found " +
            $"({(usedDescendantFallback ? "even after descendant fallback search" : "on the GameObject itself")}). " +
            "The pipeline stops here: SelectionMaskPass has nothing to draw, so no mask is ever produced. " +
            "THIS IS A CANDIDATE FAILURE POINT if you see this log.");
    }

    // ---------------------------------------------------------------
    // Section 3: camera checks. Pass any camera you want inspected -
    // usually the camera actually rendering the model (e.g. modelCamera).
    // ---------------------------------------------------------------
    public static void LogCameraChecks(Renderer[] renderers, Camera cam)
    {
        if (!Enabled) return;

        if (cam == null)
        {
            Warn("Camera",
                "No camera supplied for diagnostics. Wire BoneOutlineController.debugCamera to the camera that " +
                "actually renders the model (e.g. AnatomyScreenController.modelCamera) to get frustum/culling-mask " +
                "checks. This does not affect whether the outline itself renders - only this diagnostic.");
            return;
        }

        Log("Camera",
            $"cam='{cam.name}' cameraType={cam.cameraType} " +
            $"targetTexture={(cam.targetTexture != null ? cam.targetTexture.name : "None (renders directly to screen/backbuffer)")} " +
            $"cullingMask={System.Convert.ToString(cam.cullingMask, 2)} fov={cam.fieldOfView} " +
            $"near={cam.nearClipPlane} far={cam.farClipPlane} " +
            $"allowHDR={cam.allowHDR} allowMSAA={cam.allowMSAA}");

        var planes = GeometryUtility.CalculateFrustumPlanes(cam);
        foreach (var r in renderers)
        {
            if (r == null) continue;

            bool inFrustum = GeometryUtility.TestPlanesAABB(planes, r.bounds);
            int layerBit = 1 << r.gameObject.layer;
            bool layerIncluded = (cam.cullingMask & layerBit) != 0;

            if (!inFrustum)
                Warn("Camera",
                    $"'{r.name}' bounds are OUTSIDE '{cam.name}'s frustum right now - the bone (and therefore its " +
                    "outline) is offscreen from this camera's current position/zoom.");
            else
                Log("Camera", $"'{r.name}' bounds ARE inside '{cam.name}'s frustum.");

            if (!layerIncluded)
                Err("Camera",
                    $"'{r.name}' is on layer '{LayerMask.LayerToName(r.gameObject.layer)}' " +
                    $"({r.gameObject.layer}), which is NOT included in '{cam.name}'s culling mask " +
                    $"({System.Convert.ToString(cam.cullingMask, 2)}). This camera will not draw the bone at all - " +
                    "fix the camera's Culling Mask or the object's layer. THIS IS A CANDIDATE FAILURE POINT if you see this log.");
            else
                Log("Camera",
                    $"'{r.name}' layer '{LayerMask.LayerToName(r.gameObject.layer)}' IS included in '{cam.name}'s culling mask.");
        }
    }

    // ---------------------------------------------------------------
    // Section 4/5: renderer feature presence + shader/material checks
    // ---------------------------------------------------------------
    public static void LogFeatureCreated(bool maskMaterialAssigned, bool compositeMaterialAssigned)
    {
        Log("RenderFeature",
            "BoneOutlineFeature.Create() ran - this confirms the feature IS present on some Universal Renderer Data " +
            "asset that got loaded. If you never see this log at all (not even once, at startup), the feature is " +
            "MISSING from the active URP Renderer asset - add it under the Renderer Data asset's " +
            "'Renderer Features' list. " +
            $"maskMaterialAssigned={maskMaterialAssigned} compositeMaterialAssigned={compositeMaterialAssigned}");
    }

    public static void LogFeatureInstanceMissing()
    {
        Err("RenderFeature",
            "BoneOutlineFeature.Instance is null when BoneOutlineController tried to use it. Create() never ran for " +
            "any BoneOutlineFeature, which means either (a) the feature isn't added to the Universal Renderer Data " +
            "asset actually assigned to the camera rendering the model, or (b) that camera is using a different " +
            "URP Renderer / URP Asset than the one you edited in the Inspector. " +
            "THIS IS A CANDIDATE FAILURE POINT if you see this log - nothing downstream can run.");
    }

    public static void LogMaterialAndShader(string label, Material mat, string expectedShaderNameContains)
    {
        if (!Enabled) return;

        if (mat == null)
        {
            Err("Shader",
                $"{label} material is NULL (not assigned in BoneOutlineFeature.Settings on the Universal Renderer " +
                "Data asset). AddRenderPasses returns early with ZERO passes enqueued when this is null. " +
                "THIS IS A CANDIDATE FAILURE POINT if you see this log.");
            return;
        }

        if (mat.shader == null)
        {
            Err("Shader", $"{label} material '{mat.name}' has a NULL shader reference (shader asset missing/renamed/guid mismatch).");
            return;
        }

        if (!mat.shader.isSupported)
        {
            Err("Shader", $"{label} material '{mat.name}' uses shader '{mat.shader.name}', which Unity reports as NOT SUPPORTED on the current platform/graphics API.");
            return;
        }

        bool nameMatches = string.IsNullOrEmpty(expectedShaderNameContains) ||
                            mat.shader.name.Contains(expectedShaderNameContains);

        if (!nameMatches)
        {
            Err("Shader",
                $"{label} material '{mat.name}' is assigned shader '{mat.shader.name}', which does NOT look like the " +
                $"expected outline shader (expected name to contain '{expectedShaderNameContains}'). This material's " +
                "shader reference was probably never set / got reset to a default (e.g. URP/Lit) - check the " +
                "Inspector on this material asset directly. THIS IS A LIKELY FAILURE POINT if you see this log: the " +
                "render passes will run and draw something, but not the outline effect.");
        }
        else
        {
            Log("Shader", $"{label} OK - material='{mat.name}' shader='{mat.shader.name}' passCount={mat.passCount}.");
        }
    }

    public static void LogOutlineParams(float widthPixels, Color color, int downsample)
    {
        Log("Shader", $"Outline params - widthPixels={widthPixels} color={color} maskDownsample={downsample}.");
    }

    // ---------------------------------------------------------------
    // Section 6: render pass enqueue / execute
    // ---------------------------------------------------------------
    public static void LogAddRenderPassesSkipped(string reason)
    {
        Warn("RenderPass", $"AddRenderPasses SKIPPED this frame - {reason}");
    }

    public static void LogAddRenderPassesRun(CameraType camType, string camName, int rendererCount, bool featureIsActive)
    {
        if (!Enabled) return;
        bool first = !_firstAddRenderPassesLoggedForSelection;
        _firstAddRenderPassesLoggedForSelection = true;
        if (first || ShouldHeartbeat())
            Log("RenderPass",
                $"AddRenderPasses ENQUEUED both passes.  camera='{camName}'  cameraType={camType}  " +
                $"selectedRendererCount={rendererCount}  featureIsActive={featureIsActive}{(first ? "" : "  (heartbeat)")}");
    }

    public static void LogMaskPassRecord(int rendererCount, int width, int height)
    {
        if (!Enabled) return;
        bool first = !_firstMaskDrawLoggedForSelection;
        _firstMaskDrawLoggedForSelection = true;
        if (first || ShouldHeartbeat())
            Log("RenderPass", $"SelectionMaskPass.RecordRenderGraph - drawing {rendererCount} renderer(s) into a {width}x{height} mask texture.{(first ? "" : "  (heartbeat)")}");
    }

    /// <summary>
    /// Decisive mask-content check: schedules a GPU->CPU readback of the mask
    /// texture right after it's drawn and reports what % of texels are
    /// actually non-zero. Splits the pipeline in half - if this reports
    /// 0% non-zero, the bone never made it into the mask (matrices/culling/
    /// shader problem upstream). If it reports >0%, the mask is fine and the
    /// bug is downstream in OutlineCompositePass or BoneOutlineComposite.shader.
    /// Only fires ONCE per selection (readback is relatively expensive) - call
    /// from inside SelectionMaskPass's SetRenderFunc, AFTER the DrawRenderer
    /// calls, so the command buffer reads back the finished mask.
    /// </summary>
    public static void MaybeRequestMaskReadback(CommandBuffer cmd, TextureHandle mask)
    {
        if (!Enabled) return;
        if (_maskReadbackRequestedForSelection) return;
        _maskReadbackRequestedForSelection = true;

        Log("RenderPass", "Requesting one-time async GPU readback of the mask texture to check its actual content...");

        cmd.RequestAsyncReadback(mask, request =>
        {
            if (request.hasError)
            {
                Err("RenderPass", "Mask readback FAILED (request.hasError == true) - could not verify mask content this way.");
                return;
            }

            var data = request.GetData<byte>();
            int total = data.Length;
            int nonZero = 0;
            for (int i = 0; i < total; i++)
                if (data[i] != 0) nonZero++;

            float pct = total > 0 ? (100f * nonZero / total) : 0f;

            if (nonZero == 0)
            {
                Err("RenderPass",
                    $"Mask readback: 0 / {total} texels are non-zero - THE MASK TEXTURE IS COMPLETELY EMPTY. " +
                    "The bone's DrawRenderer call ran (see the log above) but produced no visible silhouette in the " +
                    "render target - likely cause: wrong/uninitialized view-projection matrices for an Unsafe render-graph " +
                    "pass at this renderPassEvent, the renderer's bounds not actually overlapping this camera's frustum " +
                    "in mask-space, or BoneOutlineMask.shader failing to compile/bind on this platform. " +
                    "THIS IS THE FAILURE POINT if you see this log.");
            }
            else
            {
                Log("RenderPass",
                    $"Mask readback: {nonZero} / {total} texels ({pct:F2}%) are non-zero - the mask DOES contain real " +
                    "silhouette data. The failure is downstream of the mask: check OutlineCompositePass's edge-detect " +
                    "math / _OutlineWidth / _OutlineColor in BoneOutlineComposite.shader, or confirm the RenderTexture " +
                    "that composite pass writes into is the exact same one being displayed in Game View / on BodyArea " +
                    "(e.g. not a stale copy, not overwritten by something later in the frame).");
            }
        });
    }

    public static void LogMaskPassSkipped(string reason)
    {
        Warn("RenderPass",
            $"SelectionMaskPass.RecordRenderGraph SKIPPED - {reason}. MaskHandle stays invalid, so " +
            "OutlineCompositePass will also skip this frame. THIS IS A CANDIDATE FAILURE POINT if you see this log every frame.");
    }

    public static void LogCompositeSkipped(string reason)
    {
        Warn("RenderPass", $"OutlineCompositePass.RecordRenderGraph SKIPPED - {reason}");
    }

    // ---------------------------------------------------------------
    // Section 7: Game View vs Scene View - the #1 cause of "works in
    // Scene View but not Game View" for this kind of pass is URP
    // rendering straight to the backbuffer (no offscreen camera-color
    // target for the composite pass to read/write).
    // ---------------------------------------------------------------
    public static void LogCompositeBackBufferSkip(CameraType camType, string camName)
    {
        Err("RenderPass",
            $"OutlineCompositePass SKIPPED because isActiveTargetBackBuffer==true for {camType} camera '{camName}' " +
            "(URP is rendering straight to the backbuffer - there is no offscreen camera-color target to read/write). " +
            "*** THIS IS THE #1 CAUSE OF 'outline shows in Scene View but not Game View' ***  " +
            "The Scene View camera almost always forces an offscreen target (gizmos/overlays need one), while a " +
            "bare Game View camera with no Post Processing, no other renderer feature requiring Color, and no " +
            "HDR/MSAA-forcing setting CAN render directly to the backbuffer. " +
            "OutlineCompositePass's constructor calls ConfigureInput(ScriptableRenderPassInput.Color) specifically " +
            "to prevent this - if you're still seeing this log for the Game camera, check: " +
            "(1) is this the same BoneOutlineFeature instance/asset actually assigned to that camera's Renderer, " +
            "(2) is there a second/duplicate Universal Renderer Data asset in play, " +
            "(3) does the camera have 'Render Type' set to something that bypasses this Renderer's feature list.");
    }

    public static void LogCompositeExecuting()
    {
        if (!Enabled) return;
        bool first = !_firstCompositeLoggedForSelection;
        _firstCompositeLoggedForSelection = true;
        if (first || ShouldHeartbeat())
            Log("RenderPass", $"OutlineCompositePass executing - copy-scene-color + fullscreen composite both recorded onto the real camera color target this frame.{(first ? "" : "  (heartbeat)")}");
    }

    private static bool _debugFillModeLoggedOnce;
    public static void LogDebugFillModeActive()
    {
        if (!Enabled || _debugFillModeLoggedOnce) return;
        _debugFillModeLoggedOnce = true;
        Warn("Shader",
            "settings.debugFillSilhouette is ON - BoneOutlineComposite.shader will fill the ENTIRE silhouette solid, " +
            "ignoring outlineWidthPixels. If you now see a big solid blob in Game View: compositing IS reaching the " +
            "display, and the real bug is in the edge-detect math or outlineWidthPixels/maskDownsample being too " +
            "small to see. If you STILL see nothing: the composite pass's write isn't reaching the displayed " +
            "RenderTexture at all - check that AnatomyModelRT (or whatever's bound to BodyArea) is exactly the " +
            "texture this camera renders into, and that nothing else redraws over it later in the frame. " +
            "Remember to turn this back off when done - it's for diagnosis only.");
    }

    private static bool ShouldHeartbeat()
    {
        _frameCounter++;
        return SteadyStateLogInterval > 0 && (_frameCounter % SteadyStateLogInterval == 0);
    }
}
