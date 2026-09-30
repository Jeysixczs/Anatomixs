using UnityEngine;

namespace Anatomia3D.UI.Animation
{
    /// <summary>
    /// Pure easing math used by <see cref="UIAnimationUtility"/>.
    /// Input and output are normalized (0..1); OutBack may overshoot slightly past 1.
    /// </summary>
    internal static class UIEasing
    {
        public static float Evaluate(UIAnimationUtility.Ease ease, float t)
        {
            t = Mathf.Clamp01(t);

            switch (ease)
            {
                case UIAnimationUtility.Ease.InQuad:
                    return t * t;

                case UIAnimationUtility.Ease.OutQuad:
                    return 1f - (1f - t) * (1f - t);

                case UIAnimationUtility.Ease.InOutQuad:
                    return t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) / 2f;

                case UIAnimationUtility.Ease.InCubic:
                    return t * t * t;

                case UIAnimationUtility.Ease.OutCubic:
                    return 1f - Mathf.Pow(1f - t, 3f);

                case UIAnimationUtility.Ease.InOutCubic:
                    return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;

                case UIAnimationUtility.Ease.OutBack:
                {
                    const float c1 = 1.70158f;
                    const float c3 = c1 + 1f;
                    float p = t - 1f;
                    return 1f + c3 * p * p * p + c1 * p * p;
                }

                case UIAnimationUtility.Ease.OutElastic:
                {
                    if (t <= 0f) return 0f;
                    if (t >= 1f) return 1f;
                    const float c4 = (2f * Mathf.PI) / 3f;
                    return Mathf.Pow(2f, -10f * t) * Mathf.Sin((t * 10f - 0.75f) * c4) + 1f;
                }

                case UIAnimationUtility.Ease.Linear:
                default:
                    return t;
            }
        }
    }
}
