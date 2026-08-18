using System.Collections.Generic;
using UnityEngine;

// Drives the visible outline for whichever bone is currently selected in
// the Anatomy screen.
//
// Screen-space silhouette system (replaces the old inverted-hull mesh
// outline entirely - see BoneOutlineFeature.cs for the render-feature side):
//
//   Bone Selection -> Selected Bone ID -> Selection Mask
//     -> Screen-Space Silhouette Detection -> Thin Outline Only
//     -> Overlay on Final Camera
//
// This script's job is ONLY the left half of that pipeline: resolving
// "selecting this Transform means outlining exactly these Renderer(s)" -
// the exact same narrow resolution rules the old mesh-based version used
// (own renderer only; a descendant-search fallback strictly limited to
// renderer-less group nodes; degenerate/disabled renderers skipped) - and
// then handing that renderer list to BoneOutlineFeature, which does the
// actual screen-space mask + edge-detect rendering on the GPU.
//
// No mesh duplication happens here anymore. No outline GameObjects are
// created. The selected bone's own material/color is never touched -
// this script never assigns anything to the bone's Renderer at all.
[DisallowMultipleComponent]
public class BoneOutlineController : MonoBehaviour
{
    [Tooltip("A renderer whose bounds size-squared falls below this is treated as degenerate placeholder geometry (e.g. hollow sinus/air-cell volumes with no real triangles) and skipped - same threshold AnatomyScreenController.ComputeSkeletonBounds uses for the same reason.")]
    [SerializeField] private float degenerateBoundsSqrThreshold = 0.0001f;

    #region OUTLINE DEBUG
    // Optional - wire this to the camera that actually renders the model
    // (e.g. AnatomyScreenController.modelCamera) to get frustum/culling-mask
    // diagnostics in SetSelectedBone. Purely diagnostic: leaving this unset
    // does not change outline behavior, it just skips the camera checks.
    // Delete this field + region as part of removing the debug system
    // (see OutlineDebug.cs header for full removal steps).
    [Header("Debug (safe to remove - see OutlineDebug.cs)")]
    [Tooltip("Camera used ONLY for the [OutlineDebug] frustum/culling-mask diagnostics below. Does not affect actual outline rendering.")]
    [SerializeField] private Camera debugCamera;
    #endregion

    // The exact Renderer(s) currently registered with BoneOutlineFeature for
    // outlining. Kept here only so ClearOutline/SetSelectedBone can tell the
    // feature "nothing selected" without needing to re-resolve anything.
    private readonly List<Renderer> _selectedRenderers = new List<Renderer>();

    /// <summary>
    /// Combined world-space bounds of every real (non-degenerate)
    /// MeshRenderer/SkinnedMeshRenderer found under <paramref name="boneRoot"/>,
    /// found via GetComponentsInChildren so multi-part bones (several mesh
    /// pieces/nested FBX objects under one bone root) are fully accounted
    /// for. Public and static, unchanged from the previous implementation -
    /// AnatomyScreenController's camera-focus zoom relies on this exact
    /// signature and behavior.
    /// </summary>
    public static bool TryComputeCombinedBounds(Transform boneRoot, out Bounds bounds, float degenerateSqrThreshold = 0.0001f)
    {
        bounds = default;
        if (boneRoot == null) return false;

        bool hasBounds = false;
        foreach (var r in boneRoot.GetComponentsInChildren<Renderer>())
        {
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
            if (!r.enabled) continue;
            if (r.bounds.size.sqrMagnitude < degenerateSqrThreshold) continue;

            if (!hasBounds) { bounds = r.bounds; hasBounds = true; }
            else bounds.Encapsulate(r.bounds);
        }

        return hasBounds;
    }

    /// <summary>
    /// Clears whatever outline is currently shown and, if
    /// <paramref name="selectedGameObject"/> (the EXACT GameObject the
    /// selection resolved to - see AnatomyScreenController.selectedGameObject,
    /// which is always hit.collider.gameObject for a mesh tap) has a
    /// renderable component of its own, registers that GameObject's
    /// Renderer(s) with BoneOutlineFeature so the next frame's mask/outline
    /// pass draws its silhouette. Safe to call with null (just clears).
    /// </summary>
    /// <remarks>
    /// This method must never widen the selection past the exact GameObject
    /// it was given. In particular it must NOT outline based on:
    ///   - name/parent-name/child-name similarity
    ///   - BoneDatabase.json ID / mesh name / object index
    ///   - nearby or overlapping objects
    /// The one deliberate, narrow exception: if the given GameObject has no
    /// MeshRenderer/SkinnedMeshRenderer of its own (a pure organizational
    /// group node with no mesh - e.g. an empty parent used only to group
    /// other bones), there is nothing on the node itself to outline, so a
    /// descendant search is used as an explicit fallback rather than
    /// silently outlining nothing.
    /// </remarks>
    public void SetSelectedBone(Transform selectedGameObject)
    {
        // Always fully remove the previous selection's outline first -
        // never layer a new outline on top of a stale one, and never leave
        // an outline behind on a GameObject that is no longer selected.
        ClearOutline();

        #region OUTLINE DEBUG
        OutlineDebug.LogSelection(selectedGameObject);
        #endregion

        if (selectedGameObject == null) return;

        // ===== Resolve exactly which renderer(s) to outline =====
        // Local helper: filters a candidate list down to real (enabled,
        // non-degenerate) renderers, with debug logging per candidate.
        List<Renderer> FilterReal(List<Renderer> candidates)
        {
            var real = new List<Renderer>();
            foreach (var r in candidates)
            {
                if (!r.enabled)
                {
                    Debug.Log($"[BoneOutlineController]   skip '{r.name}' - renderer disabled.");
                    #region OUTLINE DEBUG
                    OutlineDebug.LogRendererCandidate(r, kept: false, reason: "renderer disabled");
                    #endregion
                    continue;
                }
                if (r.bounds.size.sqrMagnitude < degenerateBoundsSqrThreshold)
                {
                    Debug.Log($"[BoneOutlineController]   skip '{r.name}' - degenerate/zero-size bounds.");
                    #region OUTLINE DEBUG
                    OutlineDebug.LogRendererCandidate(r, kept: false, reason: "degenerate/zero-size bounds");
                    #endregion
                    continue;
                }
                real.Add(r);
                #region OUTLINE DEBUG
                OutlineDebug.LogRendererCandidate(r, kept: true, reason: null);
                #endregion
            }
            return real;
        }

        bool usedDescendantFallback = false;
        var ownRenderer = selectedGameObject.GetComponent<Renderer>();
        var realRenderers = new List<Renderer>();

        if (ownRenderer is MeshRenderer || ownRenderer is SkinnedMeshRenderer)
        {
            // Requirement: prefer the clicked GameObject's own renderer,
            // never switch to another object while it's still usable.
            realRenderers = FilterReal(new List<Renderer> { ownRenderer });
        }

        if (realRenderers.Count == 0)
        {
            // Fallback covers two cases, both meaning "nothing usable on the
            // GameObject itself": (a) no Renderer component at all, or
            // (b) a Renderer component exists but was filtered out above
            // (disabled, or degenerate/zero-size bounds - e.g. a placeholder
            // mesh on a group/joint node like a tiny vessel branch or
            // muscle pulley whose real geometry lives on a child piece).
            usedDescendantFallback = true;
            Debug.LogWarning($"[BoneOutlineController] '{selectedGameObject.name}' has no usable MeshRenderer/SkinnedMeshRenderer " +
                              "of its own (missing, disabled, or degenerate bounds) - falling back to a descendant search " +
                              "(explicit exception for renderer-less/placeholder nodes only; see SetSelectedBone remarks). " +
                              "If this GameObject is expected to have its own mesh, check the model hierarchy.");
            var descendantCandidates = new List<Renderer>();
            foreach (var r in selectedGameObject.GetComponentsInChildren<Renderer>())
            {
                if (r == ownRenderer) continue; // already tried and rejected above
                if (r is MeshRenderer || r is SkinnedMeshRenderer)
                    descendantCandidates.Add(r);
            }
            realRenderers = FilterReal(descendantCandidates);
        }

        if (realRenderers.Count == 0)
        {
            Debug.LogWarning($"[BoneOutlineController] '{selectedGameObject.name}' has no renderer with real geometry " +
                              $"{(usedDescendantFallback ? "under it" : "of its own")} - skipping outline.");
            #region OUTLINE DEBUG
            OutlineDebug.LogNoRendererFound(selectedGameObject.gameObject, usedDescendantFallback);
            #endregion
            return;
        }

        #region OUTLINE DEBUG
        OutlineDebug.LogCameraChecks(realRenderers.ToArray(), debugCamera != null ? debugCamera : Camera.main);
        #endregion

        if (BoneOutlineFeature.Instance == null)
        {
            Debug.LogWarning("[BoneOutlineController] BoneOutlineFeature not found on the active URP Renderer - " +
                              "add it under Renderer Features on the Universal Renderer Data asset. Skipping outline.");
            #region OUTLINE DEBUG
            OutlineDebug.LogFeatureInstanceMissing();
            #endregion
            return;
        }

        #region OUTLINE DEBUG
        OutlineDebug.LogMaterialAndShader("MaskMaterial", BoneOutlineFeature.Instance.settings?.maskMaterial, "BoneOutlineMask");
        OutlineDebug.LogMaterialAndShader("CompositeMaterial", BoneOutlineFeature.Instance.settings?.compositeMaterial, "BoneOutlineComposite");
        if (BoneOutlineFeature.Instance.settings != null)
            OutlineDebug.LogOutlineParams(BoneOutlineFeature.Instance.settings.outlineWidthPixels,
                                           BoneOutlineFeature.Instance.settings.outlineColor,
                                           BoneOutlineFeature.Instance.settings.maskDownsample);
        #endregion

        _selectedRenderers.AddRange(realRenderers);
        BoneOutlineFeature.Instance.SetSelectedRenderers(_selectedRenderers);

        Debug.Log($"[BoneOutlineController] SetSelectedBone: Clicked/Selected GameObject='{selectedGameObject.name}', " +
                  $"{realRenderers.Count} renderer(s) registered for the screen-space outline " +
                  $"({(usedDescendantFallback ? "descendant fallback - no own renderer" : "exact GameObject's own renderer only")}). " +
                  "The selected bone's own material/renderer is never modified - the outline is drawn entirely as a separate screen-space overlay.");
    }

    /// <summary>Removes whatever outline is currently shown, if any.</summary>
    public void ClearOutline()
    {
        _selectedRenderers.Clear();
        if (BoneOutlineFeature.Instance != null)
            BoneOutlineFeature.Instance.ClearSelectedRenderers();
    }
}
