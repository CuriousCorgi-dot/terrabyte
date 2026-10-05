// MaxHeightSlider.cs
//
// Runtime slider that lets the user set a scene's maximum height (the top of
// elevation_range_m) while the app is running. Intended for non-georeferenced
// scenes, where the 0-50 m range written by the ML pipeline is an assumption
// rather than a measurement, so the user can pick what looks right.
//
// What it does when the slider moves:
//   1. Writes the new max into the in-memory LoadedManifest.elevation_range_m.
//      ElevationDisplay (cursor/camera elevation, "Elevation Range") and
//      anything else that reads the manifest follow automatically.
//   2. Resizes the active terrain's height: size.y = newMax - elevationMin.
//      The heights array is normalised 0-1, so changing size.y rescales the
//      whole relief without rebuilding the terrain.
//
// Reloading a scene re-parses the manifest from disk, which resets the slider
// to that scene's original range. Nothing is written back to the file.
//
// By default the panel is hidden for georeferenced scenes
// (reference_available == true), since moving the max there would make the
// real elevation numbers wrong. Untick "Only For Relative Scenes" to show it
// everywhere (it then acts as vertical exaggeration).
//
// No prefab wiring: add this component to any GameObject in the scene (the
// Canvas or the Scene Manifest Loader object are fine). It builds its own
// panel at Start, same pattern as MetadataPanel / ElevationDisplay.
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class MaxHeightSlider : MonoBehaviour
{
    [Tooltip("The ManifestSceneLoader to read LoadedManifest from. Auto-found if left empty.")]
    [SerializeField] private ManifestSceneLoader loader;

    [Tooltip("Canvas to parent the panel under. Auto-found if left empty.")]
    [SerializeField] private Canvas targetCanvas;

    [Tooltip("Anchored position (from the Canvas's bottom-right) of the panel.")]
    [SerializeField] private Vector2 anchoredPosition = new Vector2(-16f, 16f);

    [Tooltip("Slider upper limit = elevation min + the scene's original span x this.")]
    [SerializeField] private float maxSpanMultiplier = 20f;

    [Tooltip("Smallest allowed span (max - min) in metres.")]
    [SerializeField] private float minSpanMeters = 5f;

    [Tooltip("Hide the slider for georeferenced scenes (reference_available == true).")]
    [SerializeField] private bool onlyForRelativeScenes = true;

    GameObject panel;
    Slider slider;
    TMP_Text label;
    TerrainSceneManifest lastManifest;
    float syncTimer = -1f; // < 0 means idle

    void Start()
    {
        if (loader == null) loader = Object.FindFirstObjectByType<ManifestSceneLoader>();
        if (targetCanvas == null) targetCanvas = Object.FindFirstObjectByType<Canvas>();

        if (loader == null || targetCanvas == null)
        {
            Debug.LogWarning("MaxHeightSlider: no ManifestSceneLoader or Canvas found - slider not built.");
            enabled = false;
            return;
        }

        BuildPanel();
        panel.SetActive(false);
    }

    void Update()
    {
        TerrainSceneManifest m = loader.LoadedManifest;

        if (!ReferenceEquals(m, lastManifest))
        {
            lastManifest = m;
            ConfigureForManifest(m);
        }

        // Debounced collider refresh so dragging stays smooth.
        if (syncTimer >= 0f)
        {
            syncTimer -= Time.unscaledDeltaTime;
            if (syncTimer < 0f) SyncTerrain();
        }
    }

    void ConfigureForManifest(TerrainSceneManifest m)
    {
        bool usable = m != null && m.elevation_range_m != null &&
                      !(onlyForRelativeScenes && m.reference_available);

        panel.SetActive(usable);
        if (!usable) return;

        float min = m.elevation_range_m.min;
        float max = m.elevation_range_m.max;
        float span = max - min;

        slider.minValue = min + minSpanMeters;
        slider.maxValue = min + Mathf.Max(span * maxSpanMultiplier, minSpanMeters * 2f);
        slider.SetValueWithoutNotify(max);
        UpdateLabel(max);
    }

    void OnSliderChanged(float newMax)
    {
        if (lastManifest == null || lastManifest.elevation_range_m == null) return;

        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null || terrain.terrainData == null) return;

        lastManifest.elevation_range_m.max = newMax;

        float span = newMax - lastManifest.elevation_range_m.min;
        Vector3 size = terrain.terrainData.size;
        terrain.terrainData.size = new Vector3(size.x, span, size.z);

        UpdateLabel(newMax);
        syncTimer = 0.2f;
    }

    void SyncTerrain()
    {
        // Refreshes collider/LOD data so the cursor-elevation raycast matches
        // the resized terrain.
        Terrain terrain = Terrain.activeTerrain;
        if (terrain != null && terrain.terrainData != null)
            terrain.terrainData.SyncHeightmap();
    }

    void UpdateLabel(float max)
    {
        if (label != null) label.text = $"Max Height: {max:F0} m";
    }

    // ------------------------------------------------------------------
    // UI construction
    // ------------------------------------------------------------------

    void BuildPanel()
    {
        panel = new GameObject("MaxHeightPanel", typeof(RectTransform));
        panel.transform.SetParent(targetCanvas.transform, false);

        RectTransform rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(1f, 0f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = new Vector2(280f, 0f);

        Image bg = panel.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.55f);

        VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 6f;
        layout.padding = new RectOffset(10, 10, 8, 8);
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        ContentSizeFitter fitter = panel.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        GameObject labelGO = new GameObject("Label", typeof(RectTransform));
        labelGO.transform.SetParent(panel.transform, false);
        labelGO.AddComponent<LayoutElement>().preferredHeight = 26f;

        label = labelGO.AddComponent<TextMeshProUGUI>();
        label.fontSize = 20f;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.color = Color.white;
        label.raycastTarget = false;
        label.text = "Max Height: -";

        slider = CreateSlider(panel.transform);
        slider.onValueChanged.AddListener(OnSliderChanged);
    }

    static Slider CreateSlider(Transform parent)
    {
        GameObject root = new GameObject("Slider", typeof(RectTransform));
        root.transform.SetParent(parent, false);
        root.AddComponent<LayoutElement>().preferredHeight = 22f;

        // Track
        GameObject bg = new GameObject("Background", typeof(RectTransform));
        bg.transform.SetParent(root.transform, false);
        Image bgImg = bg.AddComponent<Image>();
        bgImg.color = new Color(1f, 1f, 1f, 0.25f);
        RectTransform bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = new Vector2(0f, 0.3f);
        bgRt.anchorMax = new Vector2(1f, 0.7f);
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;

        // Fill
        GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(root.transform, false);
        RectTransform faRt = fillArea.GetComponent<RectTransform>();
        faRt.anchorMin = new Vector2(0f, 0.3f);
        faRt.anchorMax = new Vector2(1f, 0.7f);
        faRt.offsetMin = new Vector2(5f, 0f);
        faRt.offsetMax = new Vector2(-15f, 0f);

        GameObject fill = new GameObject("Fill", typeof(RectTransform));
        fill.transform.SetParent(fillArea.transform, false);
        Image fillImg = fill.AddComponent<Image>();
        fillImg.color = new Color(0.35f, 0.7f, 1f, 0.9f);
        RectTransform fillRt = fill.GetComponent<RectTransform>();
        fillRt.sizeDelta = new Vector2(10f, 0f);

        // Handle
        GameObject handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(root.transform, false);
        RectTransform haRt = handleArea.GetComponent<RectTransform>();
        haRt.anchorMin = Vector2.zero;
        haRt.anchorMax = Vector2.one;
        haRt.offsetMin = new Vector2(10f, 0f);
        haRt.offsetMax = new Vector2(-10f, 0f);

        GameObject handle = new GameObject("Handle", typeof(RectTransform));
        handle.transform.SetParent(handleArea.transform, false);
        Image handleImg = handle.AddComponent<Image>();
        handleImg.color = Color.white;
        RectTransform handleRt = handle.GetComponent<RectTransform>();
        handleRt.sizeDelta = new Vector2(18f, 0f);

        Slider s = root.AddComponent<Slider>();
        s.fillRect = fillRt;
        s.handleRect = handleRt;
        s.targetGraphic = handleImg;
        s.direction = Slider.Direction.LeftToRight;
        s.wholeNumbers = false;
        s.minValue = 0f;
        s.maxValue = 1f;
        s.value = 0f;

        return s;
    }
}