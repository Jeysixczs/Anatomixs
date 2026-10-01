using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Adds animated loading dots at the bottom of the splash. Builds everything in code:
/// no sprites or UI objects to create. Does not touch CustomSplashScreen or its animation.
/// Add this component to any object in the splash scene (e.g. the one with CustomSplashScreen).
/// </summary>
public class SplashLoadingDots : MonoBehaviour
{
    [Header("Layout")]
    [SerializeField] private int dotCount = 3;
    [SerializeField] private float dotSize = 24f;
    [SerializeField] private float dotSpacing = 40f;
    [Tooltip("Distance of the dots from the bottom edge (canvas units).")]
    [SerializeField] private float bottomOffset = 150f;
    [SerializeField] private Color dotColor = new Color(1f, 1f, 1f, 0.9f);

    [Header("Animation")]
    [SerializeField] private float startDelay = 0.3f;
    [SerializeField] private float fadeInDuration = 0.4f;
    [SerializeField] private float cycleDuration = 1f;
    [SerializeField, Range(0f, 0.5f)] private float stagger = 0.18f;

    private Graphic[] graphics;
    private RectTransform[] rects;
    private Texture2D circleTexture;
    private Sprite circleSprite;
    private float startTime;

    private void Awake()
    {
        // Always attach to the ROOT canvas so "bottom" means the real bottom of the screen,
        // wherever this component happens to sit in the hierarchy.
        Canvas canvas = GetComponentInParent<Canvas>(true);
        if (canvas == null) canvas = GetComponentInChildren<Canvas>(true);
        if (canvas == null) canvas = FindFirstObjectByType<Canvas>();
        if (canvas == null)
        {
            Debug.LogError("[SplashLoadingDots] No Canvas found in the scene.");
            enabled = false;
            return;
        }
        RectTransform parent = canvas.rootCanvas.transform as RectTransform;

        circleSprite = CreateCircleSprite();

        var container = new GameObject("LoadingDots", typeof(RectTransform));
        var cRect = (RectTransform)container.transform;
        cRect.SetParent(parent, false);
        cRect.anchorMin = cRect.anchorMax = new Vector2(0.5f, 0f);   // bottom center
        cRect.pivot = new Vector2(0.5f, 0.5f);
        cRect.sizeDelta = Vector2.zero;
        cRect.anchoredPosition = new Vector2(0f, bottomOffset);
        cRect.SetAsLastSibling();

        int n = Mathf.Max(1, dotCount);
        graphics = new Graphic[n];
        rects = new RectTransform[n];

        for (int i = 0; i < n; i++)
        {
            var go = new GameObject("Dot" + i, typeof(RectTransform), typeof(Image));
            var r = (RectTransform)go.transform;
            r.SetParent(cRect, false);
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(dotSize, dotSize);
            r.anchoredPosition = new Vector2((i - (n - 1) * 0.5f) * dotSpacing, 0f);

            var img = go.GetComponent<Image>();
            img.sprite = circleSprite;
            img.color = dotColor;
            img.raycastTarget = false;
            img.canvasRenderer.SetAlpha(0f);

            graphics[i] = img;
            rects[i] = r;
        }

        Debug.Log("[SplashLoadingDots] Created " + n + " dots under canvas '" + parent.name + "'");
    }

    private void Start()
    {
        startTime = Time.unscaledTime;
    }

    private void Update()
    {
        if (graphics == null) return;

        float t = Time.unscaledTime - startTime - startDelay;
        if (t < 0f) return;

        float fadeIn = Mathf.Clamp01(t / Mathf.Max(0.01f, fadeInDuration));
        float cycle = Mathf.Max(0.1f, cycleDuration);

        for (int i = 0; i < graphics.Length; i++)
        {
            float phase = t / cycle - i * stagger;
            float pulse = 0.5f + 0.5f * Mathf.Sin(phase * Mathf.PI * 2f);
            float s = Mathf.Lerp(0.65f, 1f, pulse);
            graphics[i].canvasRenderer.SetAlpha(fadeIn * Mathf.Lerp(0.25f, 1f, pulse));
            rects[i].localScale = new Vector3(s, s, 1f);
        }
    }

    private void OnDestroy()
    {
        if (circleSprite != null) Destroy(circleSprite);
        if (circleTexture != null) Destroy(circleTexture);
    }

    private Sprite CreateCircleSprite()
    {
        const int size = 64;
        circleTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        var px = new Color32[size * size];
        float c = (size - 1) * 0.5f;
        float radius = size * 0.5f - 1f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
            byte a = (byte)(Mathf.Clamp01(radius - d + 0.5f) * 255f);
            px[y * size + x] = new Color32(255, 255, 255, a);
        }
        circleTexture.SetPixels32(px);
        circleTexture.Apply(false, false);
        return Sprite.Create(circleTexture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
    }
}
