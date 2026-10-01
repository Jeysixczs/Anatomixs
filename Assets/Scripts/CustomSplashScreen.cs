using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class CustomSplashScreen : MonoBehaviour
{
    [Header("Splash Content")]
    [SerializeField] private RectTransform logo;
    [SerializeField] private RectTransform appName;
    [SerializeField] private RectTransform description;

    
    [Header("Logo Animation")]
    [SerializeField] private float startScale = 0.01f;
    [SerializeField] private float finalScale = 1.0f;
    [SerializeField] private float logoAnimationDuration = 3.2f;
    [SerializeField] private float rotationAmount = 180f;

    [Header("Text Animation")]
    [SerializeField] private float textDelay = 0.2f;
    [SerializeField] private float textFadeDuration = 0.6f;
    [SerializeField] private float textSlideDistance = 20f;

    [Header("Timing")]
    [SerializeField] private float holdDuration = 1.0f;
    [SerializeField] private float fadeOutDuration = 0.6f;

    [Header("Next Scene")]
    [SerializeField] private string nextSceneName = "Anatomia";

    private CanvasGroup appNameCanvasGroup;
    private CanvasGroup descriptionCanvasGroup;

    private Vector2 appNameStartPosition;
    private Vector2 descriptionStartPosition;

    private void Awake()
    {
        // Get or create CanvasGroups for text
        appNameCanvasGroup =
            GetOrAddCanvasGroup(appName);

        descriptionCanvasGroup =
            GetOrAddCanvasGroup(description);

        // Save original text positions
        if (appName != null)
        {
            appNameStartPosition =
                appName.anchoredPosition;
        }

        if (description != null)
        {
            descriptionStartPosition =
                description.anchoredPosition;
        }
    }

    private void Start()
    {
        StartCoroutine(PlaySplashAnimation());
    }

    // =========================================================
    // MAIN SPLASH ANIMATION
    // =========================================================

    private IEnumerator PlaySplashAnimation()
    {
        // =====================================================
        // INITIAL STATE
        // =====================================================

        if (logo != null)
        {
            // Start very small
            logo.localScale =
                Vector3.one * startScale;

            // Start at 180 degrees
            logo.localRotation =
                Quaternion.Euler(
                    0f,
                    0f,
                    rotationAmount
                );
        }

        // Hide app name
        if (appNameCanvasGroup != null)
        {
            appNameCanvasGroup.alpha = 0f;
        }

        // Hide description
        if (descriptionCanvasGroup != null)
        {
            descriptionCanvasGroup.alpha = 0f;
        }

        // Move app name upward
        if (appName != null)
        {
            appName.anchoredPosition =
                appNameStartPosition +
                Vector2.up * textSlideDistance;
        }

        // Move description upward
        if (description != null)
        {
            description.anchoredPosition =
                descriptionStartPosition +
                Vector2.up * textSlideDistance;
        }

        // =====================================================
        // LOGO GROW + SMOOTH ROTATION
        // =====================================================

        float elapsed = 0f;

        while (elapsed < logoAnimationDuration)
        {
            elapsed += Time.deltaTime;

            float progress =
                Mathf.Clamp01(
                    elapsed / logoAnimationDuration
                );

            // Smooth easing
            float smoothProgress =
                Mathf.SmoothStep(
                    0f,
                    1f,
                    progress
                );

            // =================================================
            // SCALE
            // =================================================

            float scaleProgress =
                EaseOutBack(progress);

            float currentScale =
                Mathf.Lerp(
                    startScale,
                    finalScale,
                    scaleProgress
                );

            // =================================================
            // ROTATION
            // =================================================

            // Smoothly rotate:
            //
            // 180° → 90° → 45° → 10° → 0°
            //
            // This creates a natural slowdown near the end.

            float currentRotation =
                Mathf.Lerp(
                    rotationAmount,
                    0f,
                    smoothProgress
                );

            if (logo != null)
            {
                logo.localScale =
                    Vector3.one * currentScale;

                logo.localRotation =
                    Quaternion.Euler(
                        0f,
                        0f,
                        currentRotation
                    );
            }

            yield return null;
        }

        // =====================================================
        // FINAL LOGO STATE
        // =====================================================

        if (logo != null)
        {
            // Make sure logo reaches exact final size
            logo.localScale =
                Vector3.one * finalScale;

            // Make sure logo reaches exact default rotation
            logo.localRotation =
                Quaternion.identity;
        }

        // =====================================================
        // WAIT BEFORE TEXT
        // =====================================================

        yield return new WaitForSeconds(
            textDelay
        );

        // =====================================================
        // TEXT ANIMATION
        // =====================================================

        yield return StartCoroutine(
            AnimateTextIn()
        );

        // =====================================================
        // HOLD SPLASH SCREEN
        // =====================================================

        yield return new WaitForSeconds(
            holdDuration
        );

        // =====================================================
        // FADE OUT
        // =====================================================

        yield return StartCoroutine(
            FadeEverythingOut()
        );

        // =====================================================
        // LOAD MAIN SCENE
        // =====================================================

        Debug.Log(
            "Loading scene: " + nextSceneName
        );

        SceneManager.LoadScene(
            nextSceneName
        );
    }

    // =========================================================
    // TEXT FADE + SLIDE
    // =========================================================

    private IEnumerator AnimateTextIn()
    {
        float elapsed = 0f;

        while (elapsed < textFadeDuration)
        {
            elapsed += Time.deltaTime;

            float progress =
                Mathf.Clamp01(
                    elapsed / textFadeDuration
                );

            // Smooth text animation
            float easedProgress =
                Mathf.SmoothStep(
                    0f,
                    1f,
                    progress
                );

            // =================================================
            // APP NAME
            // =================================================

            if (appNameCanvasGroup != null)
            {
                appNameCanvasGroup.alpha =
                    easedProgress;
            }

            if (appName != null)
            {
                appName.anchoredPosition =
                    Vector2.Lerp(
                        appNameStartPosition +
                        Vector2.up * textSlideDistance,

                        appNameStartPosition,

                        easedProgress
                    );
            }

            // =================================================
            // DESCRIPTION
            // =================================================

            if (descriptionCanvasGroup != null)
            {
                descriptionCanvasGroup.alpha =
                    easedProgress;
            }

            if (description != null)
            {
                description.anchoredPosition =
                    Vector2.Lerp(
                        descriptionStartPosition +
                        Vector2.up * textSlideDistance,

                        descriptionStartPosition,

                        easedProgress
                    );
            }

            yield return null;
        }

        // =====================================================
        // FINAL TEXT STATE
        // =====================================================

        if (appNameCanvasGroup != null)
        {
            appNameCanvasGroup.alpha = 1f;
        }

        if (descriptionCanvasGroup != null)
        {
            descriptionCanvasGroup.alpha = 1f;
        }

        if (appName != null)
        {
            appName.anchoredPosition =
                appNameStartPosition;
        }

        if (description != null)
        {
            description.anchoredPosition =
                descriptionStartPosition;
        }
    }

    // =========================================================
    // FADE EVERYTHING OUT
    // =========================================================

    private IEnumerator FadeEverythingOut()
    {
        float elapsed = 0f;

        Graphic[] graphics =
            GetComponentsInChildren<Graphic>(
                true
            );

        Color[] originalColors =
            new Color[graphics.Length];

        // Save original colors
        for (int i = 0; i < graphics.Length; i++)
        {
            originalColors[i] =
                graphics[i].color;
        }

        // Fade animation
        while (elapsed < fadeOutDuration)
        {
            elapsed += Time.deltaTime;

            float progress =
                Mathf.Clamp01(
                    elapsed / fadeOutDuration
                );

            float alpha =
                Mathf.Lerp(
                    1f,
                    0f,
                    progress
                );

            for (int i = 0; i < graphics.Length; i++)
            {
                Color color =
                    originalColors[i];

                color.a *= alpha;

                graphics[i].color =
                    color;
            }

            yield return null;
        }

        // Completely transparent
        for (int i = 0; i < graphics.Length; i++)
        {
            Color color =
                graphics[i].color;

            color.a = 0f;

            graphics[i].color =
                color;
        }
    }

    // =========================================================
    // CANVAS GROUP
    // =========================================================

    private CanvasGroup GetOrAddCanvasGroup(
        RectTransform target
    )
    {
        if (target == null)
        {
            return null;
        }

        CanvasGroup group =
            target.GetComponent<CanvasGroup>();

        if (group == null)
        {
            group =
                target.gameObject.AddComponent<CanvasGroup>();
        }

        return group;
    }

    // =========================================================
    // SCALE EASING
    // =========================================================

    private float EaseOutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;

        return 1f
            + c3 * Mathf.Pow(t - 1f, 3f)
            + c1 * Mathf.Pow(t - 1f, 2f);
    }
}