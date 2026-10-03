using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI.Animation
{
    /// <summary>
    /// Centralized, reusable animations for any UI Toolkit <see cref="VisualElement"/>.
    /// Standalone: it does nothing until you call one of its methods.
    ///
    /// QUICK START
    ///   UIAnimationUtility.FadeIn(element);
    ///   UIAnimationUtility.SlideIn(element, UIAnimationUtility.Direction.Left);
    ///   UIAnimationUtility.FadeAndSlideIn(element, UIAnimationUtility.Direction.Bottom, duration: 0.35f, delay: 0.1f);
    ///
    /// BEHAVIOR
    ///  - Every method is null-safe (a null element is ignored).
    ///  - One animation per element at a time: starting a new one stops the previous one
    ///    (its onComplete is NOT called). Fade + slide/scale combos run in a single tick, so they never conflict.
    ///  - "In" animations animate to the element's natural look, then remove the temporary inline styles
    ///    (only the ones they animated) so USS controls the element again. They never force
    ///    "display", so USS rules such as a .hidden class keep working; they only undo a hide done by
    ///    this utility's own "Out" animations.
    ///  - "Out" animations end at opacity 0 / reduced scale. With hideOnComplete (default) the element
    ///    is then set to display: none and its inline styles are cleaned up, ready to be shown again.
    ///  - Uses unscaled time, so animations still play when Time.timeScale is 0. A single long frame
    ///    never skips an animation (each tick advances at most 50 ms).
    ///  - Ticks at ~60 Hz through the element's own scheduler; no MonoBehaviour and no per-frame allocations.
    ///  - If the element is not attached to a panel, the animation is skipped and jumps to its final state.
    ///  - Requires a Unity version with USS translate/scale support (2021.2+).
    /// </summary>
    public static partial class UIAnimationUtility
    {
        // ------------------------------------------------------------------ Public types & defaults

        /// <summary>Side the element slides in from.</summary>
        public enum Direction { Left, Right, Top, Bottom }

        /// <summary>Common easing curves.</summary>
        public enum Ease
        {
            Linear,
            InQuad, OutQuad, InOutQuad,
            InCubic, OutCubic, InOutCubic,
            OutBack,
            OutElastic
        }

        public const float DefaultDuration = 0.3f;
        public const float DefaultSlideDistance = 50f;   // pixels
        public const float DefaultScale = 0.85f;         // start (In) / end (Out) scale

        private const long TickIntervalMs = 16;

        // Longest time step a single tick may advance. If the app hitches (e.g. a screen is busy
        // building its lists), the animation continues smoothly afterwards instead of being skipped.
        private const float MaxStepSeconds = 0.05f;

        /// <summary>Set to true to log every animation start/finish to the Console (for troubleshooting).</summary>
        public static bool DebugLog;

        // ------------------------------------------------------------------ Fade

        /// <summary>Fades the element from transparent to its normal opacity.</summary>
        public static void FadeIn(VisualElement element,
            float duration = DefaultDuration, float delay = 0f,
            Ease ease = Ease.OutCubic, Action onComplete = null)
        {
            Run(element, new Spec
            {
                IsIn = true, Fade = true,
                Duration = duration, Delay = delay, Ease = ease, OnComplete = onComplete
            });
        }

        /// <summary>Fades the element to transparent. By default it is hidden (display: none) afterwards.</summary>
        public static void FadeOut(VisualElement element,
            float duration = DefaultDuration, float delay = 0f,
            Ease ease = Ease.OutCubic, bool hideOnComplete = true, Action onComplete = null)
        {
            Run(element, new Spec
            {
                IsIn = false, Fade = true, HideOnComplete = hideOnComplete,
                Duration = duration, Delay = delay, Ease = ease, OnComplete = onComplete
            });
        }

        // ------------------------------------------------------------------ Slide

        /// <summary>
        /// Slides the element in from the given side to its normal position.
        /// distance = how many pixels away the slide starts.
        /// </summary>
        public static void SlideIn(VisualElement element, Direction direction,
            float duration = DefaultDuration, float delay = 0f,
            Ease ease = Ease.OutCubic, float distance = DefaultSlideDistance, Action onComplete = null)
        {
            Run(element, new Spec
            {
                IsIn = true, Slide = true, FromOffset = OffsetFor(direction, distance),
                Duration = duration, Delay = delay, Ease = ease, OnComplete = onComplete
            });
        }

        // ------------------------------------------------------------------ Scale

        /// <summary>Scales the element up from fromScale (default 0.85) to normal size.</summary>
        public static void ScaleIn(VisualElement element,
            float duration = DefaultDuration, float delay = 0f,
            Ease ease = Ease.OutCubic, float fromScale = DefaultScale, Action onComplete = null)
        {
            Run(element, new Spec
            {
                IsIn = true, Scale = true, FromScale = fromScale, ToScale = 1f,
                Duration = duration, Delay = delay, Ease = ease, OnComplete = onComplete
            });
        }

        /// <summary>Scales the element down to toScale (default 0.85). By default it is hidden afterwards.</summary>
        public static void ScaleOut(VisualElement element,
            float duration = DefaultDuration, float delay = 0f,
            Ease ease = Ease.OutCubic, float toScale = DefaultScale,
            bool hideOnComplete = true, Action onComplete = null)
        {
            Run(element, new Spec
            {
                IsIn = false, Scale = true, ToScale = toScale, HideOnComplete = hideOnComplete,
                Duration = duration, Delay = delay, Ease = ease, OnComplete = onComplete
            });
        }

        // ------------------------------------------------------------------ Combined

        /// <summary>Fade in while sliding in from the given side.</summary>
        public static void FadeAndSlideIn(VisualElement element, Direction direction,
            float duration = DefaultDuration, float delay = 0f,
            Ease ease = Ease.OutCubic, float distance = DefaultSlideDistance, Action onComplete = null)
        {
            Run(element, new Spec
            {
                IsIn = true, Fade = true, Slide = true, FromOffset = OffsetFor(direction, distance),
                Duration = duration, Delay = delay, Ease = ease, OnComplete = onComplete
            });
        }

        /// <summary>Fade in while scaling up from fromScale (default 0.85) to normal size.</summary>
        public static void FadeAndScaleIn(VisualElement element,
            float duration = DefaultDuration, float delay = 0f,
            Ease ease = Ease.OutCubic, float fromScale = DefaultScale, Action onComplete = null)
        {
            Run(element, new Spec
            {
                IsIn = true, Fade = true, Scale = true, FromScale = fromScale, ToScale = 1f,
                Duration = duration, Delay = delay, Ease = ease, OnComplete = onComplete
            });
        }

        /// <summary>Fade out while scaling down to toScale (default 0.85). By default it is hidden afterwards.</summary>
        public static void FadeAndScaleOut(VisualElement element,
            float duration = DefaultDuration, float delay = 0f,
            Ease ease = Ease.OutCubic, float toScale = DefaultScale,
            bool hideOnComplete = true, Action onComplete = null)
        {
            Run(element, new Spec
            {
                IsIn = false, Fade = true, Scale = true, ToScale = toScale, HideOnComplete = hideOnComplete,
                Duration = duration, Delay = delay, Ease = ease, OnComplete = onComplete
            });
        }

        // ------------------------------------------------------------------ Stagger

        /// <summary>
        /// Fades and slides several elements in one after another (cards, list rows...).
        /// Element i starts at startDelay + i * step seconds. Only the first maxCount elements animate,
        /// so long lists don't take forever. Example:
        ///   UIAnimationUtility.StaggerIn(cardList.Children(), UIAnimationUtility.Direction.Bottom);
        /// </summary>
        public static void StaggerIn(IEnumerable<VisualElement> elements,
            Direction direction = Direction.Bottom, float step = 0.07f, float startDelay = 0f,
            float duration = 0.4f, Ease ease = Ease.OutCubic,
            float distance = DefaultSlideDistance, int maxCount = 8)
        {
            if (elements == null) return;

            int index = 0;
            foreach (var element in elements)
            {
                if (element == null) continue;
                if (index >= maxCount) break;

                FadeAndSlideIn(element, direction, duration, startDelay + index * step, ease, distance);
                index++;
            }
        }

        // ------------------------------------------------------------------ Control

        /// <summary>True while an animation started by this utility is running (or waiting for its delay).</summary>
        public static bool IsAnimating(VisualElement element)
        {
            return element != null && Tracks.TryGetValue(element, out var track) && track.Job != null;
        }

        /// <summary>
        /// Stops the running animation without calling onComplete.
        /// restore = true removes the temporary inline styles (element returns to its USS look);
        /// restore = false leaves the element exactly where the animation stopped.
        /// </summary>
        public static void Cancel(VisualElement element, bool restore = true)
        {
            if (element == null || !Tracks.TryGetValue(element, out var track)) return;

            if (track.Job != null)
            {
                track.Job.Stop();
                track.Job = null;
            }

            if (restore)
            {
                ClearInlineStyles(element, track.Touched);
                track.Touched = Props.None;
            }
        }

        // ------------------------------------------------------------------ Internals

        [Flags]
        private enum Props { None = 0, Opacity = 1, Translate = 2, Scale = 4 }

        private struct Spec
        {
            public float Duration, Delay;
            public Ease Ease;
            public Action OnComplete;

            public bool IsIn;            // In = end at natural look; Out = end faded/scaled (optionally hidden)
            public bool HideOnComplete;  // Out only

            public bool Fade;
            public float FromOpacity, ToOpacity;

            public bool Slide;
            public Vector2 FromOffset;   // slides to (0,0)

            public bool Scale;
            public float FromScale, ToScale;

            public Props Drives
            {
                get
                {
                    Props p = Props.None;
                    if (Fade)  p |= Props.Opacity;
                    if (Slide) p |= Props.Translate;
                    if (Scale) p |= Props.Scale;
                    return p;
                }
            }
        }

        /// <summary>Per-element bookkeeping. Weakly held, so it never keeps an element alive.</summary>
        private sealed class Track
        {
            public Job Job;
            public float BaseOpacity = 1f;   // the element's natural (USS) opacity
            public Props Touched;            // inline properties this utility has set and not yet cleared
            public bool HiddenByUs;          // an "Out" animation set display: none on this element
        }

        private static readonly ConditionalWeakTable<VisualElement, Track> Tracks =
            new ConditionalWeakTable<VisualElement, Track>();

        private static Vector2 OffsetFor(Direction direction, float distance)
        {
            distance = Mathf.Abs(distance);
            switch (direction)
            {
                case Direction.Left:   return new Vector2(-distance, 0f);
                case Direction.Right:  return new Vector2(distance, 0f);
                case Direction.Top:    return new Vector2(0f, -distance);
                default:               return new Vector2(0f, distance);   // Bottom
            }
        }

        /// <summary>Clears only the given inline properties, so styles set by other code are never touched.</summary>
        private static void ClearInlineStyles(VisualElement element, Props props)
        {
            if ((props & Props.Opacity) != 0)   element.style.opacity = StyleKeyword.Null;
            if ((props & Props.Translate) != 0) element.style.translate = StyleKeyword.Null;
            if ((props & Props.Scale) != 0)     element.style.scale = StyleKeyword.Null;
        }

        private static void Run(VisualElement element, Spec spec)
        {
            if (element == null) return;

            Track track = Tracks.GetOrCreateValue(element);

            // Replace any animation already running on this element.
            if (track.Job != null)
            {
                track.Job.Stop();
                track.Job = null;
            }
            else if (track.Touched == Props.None)
            {
                // Element is in its natural state: remember its real opacity.
                // (A value of 0 or NaN means "not resolved yet" or "meant to be hidden": fade to 1.)
                float o = element.resolvedStyle.opacity;
                track.BaseOpacity = (float.IsNaN(o) || o <= 0.001f) ? 1f : Mathf.Clamp01(o);
            }

            // Resolve the values that depend on the element's current state.
            if (spec.Fade)
            {
                if (spec.IsIn)
                {
                    spec.FromOpacity = 0f;
                    spec.ToOpacity = track.BaseOpacity;
                }
                else
                {
                    var current = element.style.opacity;
                    spec.FromOpacity = current.keyword == StyleKeyword.Undefined ? current.value : track.BaseOpacity;
                    spec.ToOpacity = 0f;
                }
            }

            if (spec.Scale && !spec.IsIn)
            {
                var current = element.style.scale;
                spec.FromScale = current.keyword == StyleKeyword.Undefined ? current.value.value.x : 1f;
            }

            var job = new Job(element, track, spec);
            track.Job = job;
            job.Begin();
        }

        /// <summary>One running animation. A class (not a closure) so ticking allocates nothing.</summary>
        private sealed class Job
        {
            private readonly VisualElement _element;
            private readonly Track _track;
            private readonly Spec _spec;
            private IVisualElementScheduledItem _item;
            private float _lastTime;
            private float _elapsed;   // seconds of animation time, including the delay

            public Job(VisualElement element, Track track, Spec spec)
            {
                _element = element;
                _track = track;
                _spec = spec;
            }

            public void Begin()
            {
                if (DebugLog)
                    Debug.Log($"[UIAnimation] start '{_element.name}' in={_spec.IsIn} fade={_spec.Fade} slide={_spec.Slide} scale={_spec.Scale} " +
                              $"dur={_spec.Duration} delay={_spec.Delay} attached={_element.panel != null}");

                if (_spec.IsIn)
                {
                    // Start from a clean natural state (also drops leftovers from a previous animation).
                    ClearInlineStyles(_element, _track.Touched | _spec.Drives);
                    _track.Touched = Props.None;

                    // Only undo a hide done by this utility; never force display, so USS classes
                    // like .hidden keep controlling visibility.
                    if (_track.HiddenByUs)
                    {
                        _element.style.display = StyleKeyword.Null;
                        _track.HiddenByUs = false;
                    }
                }

                // Apply the start values immediately so a delayed "In" never flashes at full opacity.
                Apply(0f);

                if (_element.panel == null)
                {
                    Finish(true);   // Not attached: nothing to animate, go straight to the end state.
                    return;
                }

                _lastTime = Time.realtimeSinceStartup;
                _item = _element.schedule.Execute(Tick).Every(TickIntervalMs);
            }

            public void Stop()
            {
                _item?.Pause();
                _item = null;
            }

            private void Tick()
            {
                if (_element.panel == null)
                {
                    Finish(false);  // Element was removed mid-animation.
                    return;
                }

                float now = Time.realtimeSinceStartup;
                _elapsed += Mathf.Min(now - _lastTime, MaxStepSeconds);
                _lastTime = now;

                float running = _elapsed - Mathf.Max(0f, _spec.Delay);
                if (running < 0f) return;

                float duration = Mathf.Max(0f, _spec.Duration);
                float progress = duration <= 0f ? 1f : Mathf.Clamp01(running / duration);

                Apply(UIEasing.Evaluate(_spec.Ease, progress));

                if (progress >= 1f) Finish(true);
            }

            private void Apply(float k)
            {
                _track.Touched |= _spec.Drives;

                if (_spec.Fade)
                    _element.style.opacity = Mathf.Lerp(_spec.FromOpacity, _spec.ToOpacity, k);

                if (_spec.Slide)
                {
                    float x = Mathf.LerpUnclamped(_spec.FromOffset.x, 0f, k);
                    float y = Mathf.LerpUnclamped(_spec.FromOffset.y, 0f, k);
                    _element.style.translate = new StyleTranslate(
                        new Translate(new Length(x, LengthUnit.Pixel), new Length(y, LengthUnit.Pixel)));
                }

                if (_spec.Scale)
                {
                    float s = Mathf.LerpUnclamped(_spec.FromScale, _spec.ToScale, k);
                    _element.style.scale = new StyleScale(new Scale(new Vector2(s, s)));
                }
            }

            private void Finish(bool invokeCallback)
            {
                Stop();
                Apply(1f);
                _track.Job = null;

                if (_spec.IsIn)
                {
                    // Hand control back to USS.
                    ClearInlineStyles(_element, _track.Touched);
                    _track.Touched = Props.None;
                }
                else if (_spec.HideOnComplete)
                {
                    _element.style.display = DisplayStyle.None;
                    _track.HiddenByUs = true;
                    ClearInlineStyles(_element, _track.Touched);   // so the next "In" starts clean
                    _track.Touched = Props.None;
                }
                // else: stays faded/scaled until the next animation or Cancel.

                if (DebugLog) Debug.Log($"[UIAnimation] finish '{_element.name}' completed={invokeCallback}");

                if (invokeCallback) _spec.OnComplete?.Invoke();
            }
        }
    }
}
