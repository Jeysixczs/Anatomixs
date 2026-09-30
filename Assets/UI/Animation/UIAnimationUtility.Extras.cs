using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI.Animation
{
    /// <summary>
    /// "Modern app" extras for <see cref="UIAnimationUtility"/>. All opt-in: nothing happens until called.
    ///
    ///   UIAnimationUtility.AddPressFeedback(button);                 // tap shrink + spring back
    ///   UIAnimationUtility.AddPressFeedbackToAll(root, "card");      // every Button + every .card under root
    ///   UIAnimationUtility.PopIn(card, delay: 0.1f);                 // bouncy entrance
    ///   UIAnimationUtility.AnimateFill(fillBar, 0.72f);              // bar fills 0 -> 72 %
    ///   UIAnimationUtility.CountUp(label, 0, 1250, format: "{0:N0} XP");
    ///   UIAnimationUtility.Shake(passwordField);                     // wrong password / answer
    ///   UIAnimationUtility.ShowSkeletons(list, 3);  ...  UIAnimationUtility.HideSkeletons(list);
    ///
    /// These use their own tiny tweens, independent of the one-animation-per-element rule of the
    /// entrance/exit methods. Notes:
    ///  - Press feedback drives "scale", Shake drives "translate". Don't run Shake on an element whose USS
    ///    has "transition-property: translate", or the shake will lag behind.
    ///  - Press feedback clears its inline scale when finished, so USS controls the element again.
    /// </summary>
    public static partial class UIAnimationUtility
    {
        // ------------------------------------------------------------------ Small tween helper

        private sealed class Tween
        {
            private readonly VisualElement _element;
            private readonly float _duration, _delay;
            private readonly Ease _ease;
            private readonly Action<float> _apply;
            private readonly Action<bool> _done;   // true = ran to the end, false = element was removed
            private IVisualElementScheduledItem _item;
            private float _last, _elapsed;

            public Tween(VisualElement element, float duration, float delay, Ease ease,
                Action<float> apply, Action<bool> done = null)
            {
                _element = element;
                _duration = Mathf.Max(0f, duration);
                _delay = Mathf.Max(0f, delay);
                _ease = ease;
                _apply = apply;
                _done = done;
            }

            public void Start()
            {
                _apply(0f);

                if (_element.panel == null)
                {
                    _apply(1f);
                    _done?.Invoke(true);
                    return;
                }

                _last = Time.realtimeSinceStartup;
                _item = _element.schedule.Execute(Tick).Every(TickIntervalMs);
            }

            /// <summary>Stops without calling the done callback.</summary>
            public void Stop()
            {
                _item?.Pause();
                _item = null;
            }

            private void Tick()
            {
                if (_element.panel == null)
                {
                    Stop();
                    _done?.Invoke(false);
                    return;
                }

                float now = Time.realtimeSinceStartup;
                _elapsed += Mathf.Min(now - _last, MaxStepSeconds);
                _last = now;

                float running = _elapsed - _delay;
                if (running < 0f) return;

                float progress = _duration <= 0f ? 1f : Mathf.Clamp01(running / _duration);
                _apply(UIEasing.Evaluate(_ease, progress));

                if (progress >= 1f)
                {
                    Stop();
                    _done?.Invoke(true);
                }
            }
        }

        private sealed class TweenSlot { public Tween Tween; }

        private static readonly ConditionalWeakTable<VisualElement, TweenSlot> CountSlots =
            new ConditionalWeakTable<VisualElement, TweenSlot>();
        private static readonly ConditionalWeakTable<VisualElement, TweenSlot> FillSlots =
            new ConditionalWeakTable<VisualElement, TweenSlot>();
        private static readonly ConditionalWeakTable<VisualElement, TweenSlot> ShakeSlots =
            new ConditionalWeakTable<VisualElement, TweenSlot>();

        /// <summary>Stops whatever tween the slot holds and puts the new one in its place.</summary>
        private static void Replace(TweenSlot slot, Tween next)
        {
            slot.Tween?.Stop();
            slot.Tween = next;
        }

        // ------------------------------------------------------------------ 1. Tap feedback

        private sealed class PressState
        {
            public Tween Tween;
            public float Scale = 1f;
            public float PressedScale;
            public bool Down;
        }

        private static readonly ConditionalWeakTable<VisualElement, PressState> PressStates =
            new ConditionalWeakTable<VisualElement, PressState>();

        /// <summary>
        /// The element shrinks slightly while pressed and springs back on release.
        /// Safe to call more than once on the same element (the second call is ignored).
        /// pressedScale: 0.96 is subtle; 0.92 is pronounced (good for small buttons).
        /// </summary>
        public static void AddPressFeedback(VisualElement element, float pressedScale = 0.96f)
        {
            if (element == null || PressStates.TryGetValue(element, out _)) return;

            var state = new PressState { PressedScale = Mathf.Clamp(pressedScale, 0.5f, 1f) };
            PressStates.Add(element, state);

            // Trickle-down, so presses on child labels/icons count and nothing stops the event first.
            element.RegisterCallback<PointerDownEvent>(e =>
            {
                if (element.enabledInHierarchy) PressTo(element, state, true);
            }, TrickleDown.TrickleDown);

            element.RegisterCallback<PointerUpEvent>(e => PressTo(element, state, false), TrickleDown.TrickleDown);
            element.RegisterCallback<PointerCancelEvent>(e => PressTo(element, state, false), TrickleDown.TrickleDown);
            element.RegisterCallback<PointerCaptureOutEvent>(e => PressTo(element, state, false));
            element.RegisterCallback<PointerLeaveEvent>(e => PressTo(element, state, false));
            element.RegisterCallback<DetachFromPanelEvent>(e =>
            {
                state.Tween?.Stop();
                state.Tween = null;
                state.Scale = 1f;
                state.Down = false;
                element.style.scale = StyleKeyword.Null;
            });
        }

        /// <summary>
        /// Adds press feedback to every Button under root, plus every element carrying one of the given
        /// USS classes (e.g. your card classes). Call it again after building dynamic content: elements
        /// that already have feedback are skipped.
        /// </summary>
        public static void AddPressFeedbackToAll(VisualElement root, params string[] extraClasses)
        {
            AddPressFeedbackToAll(root, 0.96f, extraClasses);
        }

        public static void AddPressFeedbackToAll(VisualElement root, float pressedScale, params string[] extraClasses)
        {
            if (root == null) return;

            root.Query<Button>().ForEach(b => AddPressFeedback(b, pressedScale));

            if (extraClasses == null) return;
            foreach (var cls in extraClasses)
            {
                if (string.IsNullOrEmpty(cls)) continue;
                root.Query<VisualElement>(className: cls).ForEach(e => AddPressFeedback(e, pressedScale));
            }
        }

        private static void PressTo(VisualElement element, PressState state, bool pressed)
        {
            // Up, cancel, capture-out and leave can all fire for one release: only the first counts,
            // otherwise the spring-back would restart halfway through.
            if (state.Down == pressed) return;
            state.Down = pressed;

            float from = state.Scale;
            float to = pressed ? state.PressedScale : 1f;

            state.Tween?.Stop();
            state.Tween = new Tween(
                element,
                duration: pressed ? 0.08f : 0.4f,
                delay: 0f,
                ease: pressed ? Ease.OutQuad : Ease.OutElastic,
                apply: k =>
                {
                    float s = Mathf.LerpUnclamped(from, to, k);
                    state.Scale = s;
                    element.style.scale = new StyleScale(new Scale(new Vector2(s, s)));
                },
                done: completed =>
                {
                    state.Tween = null;
                    if (!pressed)
                    {
                        state.Scale = 1f;
                        element.style.scale = StyleKeyword.Null;   // hand control back to USS
                    }
                });
            state.Tween.Start();
        }

        // ------------------------------------------------------------------ 2. Bouncy entrance

        /// <summary>
        /// Fade + scale in with a slight overshoot and settle (OutBack). Handy for cards and modals.
        /// For a bouncy slide instead, pass ease: Ease.OutBack to FadeAndSlideIn / StaggerIn.
        /// </summary>
        public static void PopIn(VisualElement element,
            float duration = 0.45f, float delay = 0f, float fromScale = 0.9f, Action onComplete = null)
        {
            FadeAndScaleIn(element, duration, delay, Ease.OutBack, fromScale, onComplete);
        }

        // ------------------------------------------------------------------ 3. Progress fill + count-up

        /// <summary>
        /// Animates a progress bar's fill. to01 is 0..1. from01 defaults to the fill's current inline
        /// width (0 if none). Set vertical = true for bars that grow in height (chart bars).
        /// </summary>
        public static void AnimateFill(VisualElement fill, float to01,
            float from01 = -1f, float duration = 0.8f, float delay = 0f,
            Ease ease = Ease.OutCubic, bool vertical = false, Action onComplete = null)
        {
            if (fill == null) return;

            to01 = Mathf.Clamp01(to01);
            if (from01 < 0f)
            {
                var current = vertical ? fill.style.height : fill.style.width;
                from01 = current.keyword == StyleKeyword.Undefined && current.value.unit == LengthUnit.Percent
                    ? Mathf.Clamp01(current.value.value / 100f)
                    : 0f;
            }
            from01 = Mathf.Clamp01(from01);

            var slot = FillSlots.GetOrCreateValue(fill);
            float from = from01, to = to01;

            Action<float> apply = k =>
            {
                var len = new Length(Mathf.Clamp01(Mathf.LerpUnclamped(from, to, k)) * 100f, LengthUnit.Percent);
                if (vertical) fill.style.height = len; else fill.style.width = len;
            };

            Replace(slot, new Tween(fill, duration, delay, ease, apply, completed =>
            {
                slot.Tween = null;
                if (completed) onComplete?.Invoke();
            }));
            slot.Tween.Start();
        }

        /// <summary>
        /// Counts a number label from one value to another. format is a string.Format pattern:
        /// "{0:N0}" -> 1,250   "{0:N0} XP"   "{0:0.#}%"   "+{0:N0}".
        /// </summary>
        public static void CountUp(TextElement label, float from, float to,
            float duration = 0.8f, float delay = 0f, Ease ease = Ease.OutCubic,
            string format = "{0:N0}", Action onComplete = null)
        {
            if (label == null) return;

            var slot = CountSlots.GetOrCreateValue(label);
            var culture = CultureInfo.CurrentCulture;

            Action<float> apply = k =>
                label.text = string.Format(culture, format, Mathf.LerpUnclamped(from, to, k));

            Replace(slot, new Tween(label, duration, delay, ease, apply, completed =>
            {
                slot.Tween = null;
                if (completed) onComplete?.Invoke();
            }));
            slot.Tween.Start();
        }

        // ------------------------------------------------------------------ 4. Shake on error

        /// <summary>
        /// A short sideways shake that fades out. Use on a wrong password, a bad code or a wrong answer.
        /// </summary>
        public static void Shake(VisualElement element,
            float amplitude = 10f, float duration = 0.4f, int cycles = 3, Action onComplete = null)
        {
            if (element == null) return;

            // A running slide/entrance would overwrite our translate every tick.
            if (IsAnimating(element)) Cancel(element, restore: true);

            var slot = ShakeSlots.GetOrCreateValue(element);
            int waves = Mathf.Max(1, cycles);

            Action<float> apply = k =>
            {
                float x = amplitude * Mathf.Sin(k * waves * Mathf.PI * 2f) * (1f - k);
                element.style.translate = new StyleTranslate(
                    new Translate(new Length(x, LengthUnit.Pixel), new Length(0f, LengthUnit.Pixel)));
            };

            Replace(slot, new Tween(element, duration, 0f, Ease.Linear, apply, completed =>
            {
                slot.Tween = null;
                element.style.translate = StyleKeyword.Null;
                if (completed) onComplete?.Invoke();
            }));
            slot.Tween.Start();
        }

        // ------------------------------------------------------------------ 5. Loading shimmer

        public const string SkeletonClass = "skeleton-card";

        private static readonly Color SkeletonBase = new Color(150f / 255f, 155f / 255f, 168f / 255f, 0.18f);
        private static readonly Color SkeletonBar = new Color(150f / 255f, 155f / 255f, 168f / 255f, 0.28f);

        // Alpha profile of the moving light: soft edges, bright middle (USS has no gradients).
        private static readonly float[] ShimmerProfile = { 0.10f, 0.30f, 0.55f, 0.30f, 0.10f };

        private sealed class ShimmerState
        {
            public VisualElement Owner, Sweep;
            public IVisualElementScheduledItem Item;
            public float Period, Phase, Last;
            public EventCallback<AttachToPanelEvent> OnAttach;
            public EventCallback<DetachFromPanelEvent> OnDetach;

            public void Begin()
            {
                if (Item != null || Owner.panel == null) return;
                Last = Time.realtimeSinceStartup;
                Item = Owner.schedule.Execute(Tick).Every(TickIntervalMs);
            }

            public void End()
            {
                Item?.Pause();
                Item = null;
            }

            private void Tick()
            {
                float now = Time.realtimeSinceStartup;
                Phase = (Phase + Mathf.Min(now - Last, MaxStepSeconds) / Mathf.Max(0.2f, Period)) % 1f;
                Last = now;

                float w = Owner.resolvedStyle.width;
                if (float.IsNaN(w) || w <= 0f) return;   // not laid out yet

                float sweepWidth = w * 0.45f;
                float x = Mathf.Lerp(-sweepWidth, w, UIEasing.Evaluate(Ease.InOutQuad, Phase));
                Sweep.style.translate = new StyleTranslate(
                    new Translate(new Length(x, LengthUnit.Pixel), new Length(0f, LengthUnit.Pixel)));
            }
        }

        private static readonly ConditionalWeakTable<VisualElement, ShimmerState> Shimmers =
            new ConditionalWeakTable<VisualElement, ShimmerState>();

        /// <summary>
        /// Adds a moving light sweep over the element (which should have a background, e.g. a
        /// placeholder block). It loops until RemoveShimmer, or until the element leaves the panel.
        /// The element is clipped (overflow: hidden) so the sweep stays inside it.
        /// </summary>
        public static void AddShimmer(VisualElement element, float period = 1.3f, float strength = 1f)
        {
            if (element == null) return;

            if (Shimmers.TryGetValue(element, out var existing))
            {
                existing.Period = period;
                existing.Begin();
                return;
            }

            element.style.overflow = Overflow.Hidden;

            var sweep = new VisualElement { name = "shimmer-sweep", pickingMode = PickingMode.Ignore };
            sweep.style.position = Position.Absolute;
            sweep.style.top = new Length(-50f, LengthUnit.Percent);
            sweep.style.bottom = new Length(-50f, LengthUnit.Percent);
            sweep.style.left = 0f;
            sweep.style.width = new Length(45f, LengthUnit.Percent);
            sweep.style.flexDirection = FlexDirection.Row;
            sweep.style.rotate = new StyleRotate(new Rotate(new Angle(12f, AngleUnit.Degree)));

            foreach (float a in ShimmerProfile)
            {
                var strip = new VisualElement { pickingMode = PickingMode.Ignore };
                strip.style.flexGrow = 1f;
                strip.style.backgroundColor = new Color(1f, 1f, 1f, a * Mathf.Clamp01(strength));
                sweep.Add(strip);
            }

            element.Add(sweep);

            var state = new ShimmerState { Owner = element, Sweep = sweep, Period = period };
            state.OnAttach = e => state.Begin();
            state.OnDetach = e => state.End();
            element.RegisterCallback(state.OnAttach);
            element.RegisterCallback(state.OnDetach);
            Shimmers.Add(element, state);

            state.Begin();   // no-op until attached; OnAttach starts it then
        }

        /// <summary>Stops the shimmer and removes its sweep element.</summary>
        public static void RemoveShimmer(VisualElement element)
        {
            if (element == null || !Shimmers.TryGetValue(element, out var state)) return;

            state.End();
            element.UnregisterCallback(state.OnAttach);
            element.UnregisterCallback(state.OnDetach);
            state.Sweep.RemoveFromHierarchy();
            Shimmers.Remove(element);
        }

        /// <summary>One placeholder card: a soft grey block with a few "text lines" and a shimmer.</summary>
        public static VisualElement CreateSkeleton(float height = 96f, int lines = 2, float cornerRadius = 16f)
        {
            var card = new VisualElement { name = SkeletonClass, pickingMode = PickingMode.Ignore };
            card.AddToClassList(SkeletonClass);
            card.style.height = height;
            card.style.flexShrink = 0f;
            card.style.marginBottom = 12f;
            card.style.paddingLeft = card.style.paddingRight = 16f;
            card.style.paddingTop = card.style.paddingBottom = 16f;
            card.style.justifyContent = Justify.Center;
            card.style.backgroundColor = SkeletonBase;
            card.style.borderTopLeftRadius = card.style.borderTopRightRadius =
                card.style.borderBottomLeftRadius = card.style.borderBottomRightRadius = cornerRadius;

            for (int i = 0; i < Mathf.Max(0, lines); i++)
            {
                var bar = new VisualElement { pickingMode = PickingMode.Ignore };
                bar.style.height = i == 0 ? 14f : 10f;
                bar.style.width = new Length(i == 0 ? 60f : 40f, LengthUnit.Percent);
                bar.style.marginBottom = i == lines - 1 ? 0f : 10f;
                bar.style.backgroundColor = SkeletonBar;
                bar.style.borderTopLeftRadius = bar.style.borderTopRightRadius =
                    bar.style.borderBottomLeftRadius = bar.style.borderBottomRightRadius = 6f;
                card.Add(bar);
            }

            AddShimmer(card);
            return card;
        }

        /// <summary>
        /// Fills a list container with placeholder cards while data loads. Call HideSkeletons when the
        /// real content is ready. (container.Clear() also removes them and the shimmer stops on its own.)
        /// </summary>
        public static List<VisualElement> ShowSkeletons(VisualElement container,
            int count = 3, float height = 96f, int lines = 2)
        {
            var created = new List<VisualElement>();
            if (container == null) return created;

            HideSkeletons(container);
            for (int i = 0; i < count; i++)
            {
                var card = CreateSkeleton(height, lines);
                container.Add(card);
                created.Add(card);
            }
            return created;
        }

        /// <summary>Removes every placeholder card previously added to the container.</summary>
        public static void HideSkeletons(VisualElement container)
        {
            if (container == null) return;

            var doomed = new List<VisualElement>();
            foreach (var child in container.Children())
                if (child.ClassListContains(SkeletonClass)) doomed.Add(child);

            foreach (var card in doomed)
            {
                RemoveShimmer(card);
                card.RemoveFromHierarchy();
            }
        }
    }
}
