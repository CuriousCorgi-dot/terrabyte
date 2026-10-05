// ManifestSceneLoader.cs
//
// Given a scene/patch id, finds that patch's manifest.json under
// Assets/StreamingAssets/<searchRoot>, parses + validates it,
// then builds and swaps in the matching terrain at runtime.
//
// Runtime texture support:
//   PNG
//   JPG
//   JPEG
//
// The texture is searched for next to the manifest/heightmap.
//
// Supported naming conventions:
//
//   <id>_texture.png
//   <id>_texture.jpg
//   <id>_texture.jpeg
//
//   texture.png
//   texture.jpg
//   texture.jpeg
//
// As a final fallback, the directory is scanned for any PNG/JPG/JPEG
// that is not the heightmap.
//
// IMPORTANT:
// manifest.heightmap and manifest.source_image are Team A's original
// delivery paths. They are NOT used directly to locate files in the
// Unity project.
//
// Runtime files live under:
// Assets/StreamingAssets/<searchRoot>/...
//
// ------------------------------------------------------------

using System.Collections;
using System.IO;
using TMPro;
using UnityEngine;

public class ManifestSceneLoader : MonoBehaviour
{
    [SerializeField] private TMP_Text loadingText;

    // Water plane that should follow terrain swaps.
    [SerializeField] private Transform waterPlane;

    [Tooltip(
        "Scene/patch id to load, e.g. \"patch_43\" or \"target\". " +
        "Matches a file named \"<id>_manifest.json\", or a \"manifest.json\" " +
        "inside a folder named <id>.")]
    [SerializeField] private string sceneId = "patch_43";

    [Tooltip(
        "Folder relative to Assets/StreamingAssets to search for manifests.")]
    [SerializeField] private string searchRoot = "Terrain";

    public TerrainSceneManifest LoadedManifest { get; private set; }

    // Terrain GameObject created by this loader.
    private GameObject currentTerrainGO;

    // TerrainData created by this loader.
    // This is the ONLY TerrainData that this script explicitly destroys.
    private TerrainData currentTerrainData;

    // Keeps track of the currently running load operation.
    private Coroutine activeLoadCoroutine;


    void Start()
    {
        Load(sceneId);
    }


    /// <summary>
    /// Starts loading the requested scene and displays the loading state.
    /// </summary>
    public Coroutine Load(string id)
    {
        // Stop an older load if the user selects another scene quickly.
        if (activeLoadCoroutine != null)
        {
            StopCoroutine(activeLoadCoroutine);
            activeLoadCoroutine = null;
        }

        activeLoadCoroutine = StartCoroutine(LoadSceneRoutine(id));

        return activeLoadCoroutine;
    }


    private IEnumerator LoadSceneRoutine(string id)
    {
        ShowLoading();

        // Give Unity one frame to render the loading message.
        yield return null;

        try
        {
            sceneId = id;

            // ---------------------------------------------------------
            // FIND MANIFEST
            // ---------------------------------------------------------

            string manifestPath = FindManifestPath(id);

            if (manifestPath == null)
            {
                Debug.LogWarning(
                    $"ManifestSceneLoader: no manifest found for scene id '{id}' " +
                    $"under Assets/StreamingAssets/{searchRoot}.");

                yield break;
            }

            // Parse through TerrainSceneManifest.
            TerrainSceneManifest manifest =
                TerrainSceneManifest.Parse(
                    File.ReadAllText(manifestPath));

            ManifestValidator.ValidateOrThrow(manifest);

            LoadedManifest = manifest;

            LogManifest(manifestPath, manifest);


            // ---------------------------------------------------------
            // FIND HEIGHTMAP
            // ---------------------------------------------------------

            string heightmapPath =
                FindHeightmapPath(manifestPath, id);

            if (heightmapPath == null)
            {
                Debug.LogWarning(
                    $"ManifestSceneLoader: manifest found for '{id}' " +
                    $"but no matching heightmap PNG sits next to it. " +
                    $"Looked for '{id}_heightmap.png' and 'heightmap.png' " +
                    $"in '{Path.GetDirectoryName(manifestPath)}'. " +
                    $"Terrain was not rebuilt.");

                yield break;
            }


            // ---------------------------------------------------------
            // BUILD TERRAIN
            // ---------------------------------------------------------

            BuildTerrainFromManifest(
                id,
                manifest,
                heightmapPath);
        }
        catch (System.Exception e)
        {
            Debug.LogError(
                $"ManifestSceneLoader: failed to load scene '{id}': " +
                $"{e.Message}\n{e.StackTrace}");
        }
        finally
        {
            HideLoading();
            activeLoadCoroutine = null;
        }
    }


    private void ShowLoading()
    {
        if (loadingText == null)
            return;

        loadingText.text = "Loading...";
        loadingText.gameObject.SetActive(true);
    }


    private void HideLoading()
    {
        if (loadingText == null)
            return;

        loadingText.gameObject.SetActive(false);
    }


    // =================================================================
    // TERRAIN BUILDING
    // =================================================================

    void BuildTerrainFromManifest(
        string id,
        TerrainSceneManifest manifest,
        string heightmapPath)
    {
        ushort[,] raw16;
        int srcW;
        int srcH;

        try
        {
            raw16 =
                TerrainBuildUtility.LoadGrayscale16Png(
                    heightmapPath,
                    out srcW,
                    out srcH);
        }
        catch (System.Exception e)
        {
            Debug.LogError(
                $"ManifestSceneLoader: failed to decode heightmap " +
                $"'{heightmapPath}': {e.Message}. " +
                $"Terrain was not rebuilt.");

            return;
        }


        int resolution =
            TerrainBuildUtility.PickHeightmapResolution(
                Mathf.Max(srcW, srcH));


        float[,] heights =
            TerrainBuildUtility.BuildHeightsArray(
                raw16,
                srcW,
                srcH,
                resolution);


        float elevationMin =
            manifest.elevation_range_m != null
                ? manifest.elevation_range_m.min
                : 0f;


        float elevationMax =
            manifest.elevation_range_m != null
                ? manifest.elevation_range_m.max
                : 1f;


        float worldWidth =
            manifest.real_world_size_m != null
                ? manifest.real_world_size_m.width
                : srcW;


        float worldLength =
            manifest.real_world_size_m != null
                ? manifest.real_world_size_m.height
                : srcH;


        // -------------------------------------------------------------
        // CLEAN UP OLD TERRAIN
        // -------------------------------------------------------------

        Terrain[] existingTerrains =
            Object.FindObjectsByType<Terrain>(
                FindObjectsSortMode.None);

        foreach (Terrain t in existingTerrains)
        {
            if (t == null)
                continue;

            // Destroy old terrain GameObjects.
            // Do NOT destroy serialized TerrainData assets.
            Destroy(t.gameObject);
        }


        // Destroy only the runtime TerrainData created by this loader.
        if (currentTerrainData != null)
        {
            Destroy(currentTerrainData);
            currentTerrainData = null;
        }

        currentTerrainGO = null;


        // -------------------------------------------------------------
        // BUILD NEW TERRAIN
        // -------------------------------------------------------------

        string terrainName = $"{id}_Terrain";

        TerrainData newTerrainData;

        currentTerrainGO =
            TerrainBuildUtility.CreateTerrainGameObject(
                heights,
                resolution,
                worldWidth,
                worldLength,
                elevationMin,
                elevationMax,
                terrainName,
                out newTerrainData);


        // Remember runtime TerrainData.
        currentTerrainData = newTerrainData;


        // -------------------------------------------------------------
        // UPDATE WATER PLANE TO MATCH NEW TERRAIN
        // -------------------------------------------------------------

        if (waterPlane != null)
        {
            waterPlane.position = new Vector3(
                worldWidth * 0.5f,
                elevationMin + 0.05f,
                worldLength * 0.5f
            );

            waterPlane.localScale = new Vector3(
                worldWidth / 10f * 1.02f,
                1f,
                worldLength / 10f * 1.02f
            );
        }


        Debug.Log(
            $"ManifestSceneLoader: built '{terrainName}' at runtime " +
            $"from '{heightmapPath}' " +
            $"({srcW}x{srcH} -> resolution {resolution}). " +
            $"Size {worldWidth}m x {elevationMax - elevationMin}m x " +
            $"{worldLength}m, elevation {elevationMin}m to " +
            $"{elevationMax}m.");


        // -------------------------------------------------------------
        // LOAD AND APPLY SOURCE TEXTURE
        // -------------------------------------------------------------

        string texturePath =
            FindTexturePath(
                heightmapPath,
                id);


        Debug.Log(
            $"ManifestSceneLoader: texture search result = " +
            $"'{texturePath}'");


        if (texturePath != null)
        {
            Debug.Log(
                $"ManifestSceneLoader: attempting to load texture " +
                $"'{texturePath}'.");


            Texture2D texture =
                TerrainBuildUtility.LoadTextureFromFile(
                    texturePath);


            if (texture != null)
            {
                Debug.Log(
                    $"ManifestSceneLoader: texture decoded successfully. " +
                    $"Resolution = {texture.width}x{texture.height}, " +
                    $"format = {texture.format}.");


                TerrainBuildUtility.ApplyDiffuseTexture(
                    newTerrainData,
                    texture);


                Debug.Log(
                    $"ManifestSceneLoader: successfully draped " +
                    $"'{texturePath}' onto '{terrainName}'.");
            }
            else
            {
                Debug.LogError(
                    $"ManifestSceneLoader: FOUND texture '{texturePath}' " +
                    $"but TerrainBuildUtility.LoadTextureFromFile() " +
                    $"failed to decode it. Terrain remains untextured.");
            }
        }
        else
        {
            Debug.LogWarning(
                $"ManifestSceneLoader: NO PNG/JPG/JPEG texture was found " +
                $"for terrain '{terrainName}'. Terrain remains untextured.");
        }


        // -------------------------------------------------------------
        // RECENTER CAMERA
        // -------------------------------------------------------------

        OrbitFlyCamera cam =
            Object.FindFirstObjectByType<OrbitFlyCamera>();

        if (cam != null)
            cam.Recenter();
    }


    // =================================================================
    // TEXTURE FINDING
    // =================================================================

    string FindTexturePath(
        string heightmapPath,
        string id)
    {
        string dir =
            Path.GetDirectoryName(heightmapPath);

        if (string.IsNullOrEmpty(dir))
            return null;


        Debug.Log(
            $"ManifestSceneLoader: searching for texture in '{dir}'.");


        // -------------------------------------------------------------
        // 1. PREFERRED ID-BASED NAMES
        // -------------------------------------------------------------

        string[] preferredNames =
        {
            $"{id}_texture.png",
            $"{id}_texture.jpg",
            $"{id}_texture.jpeg"
        };


        foreach (string filename in preferredNames)
        {
            string path =
                Path.Combine(dir, filename);

            if (File.Exists(path))
            {
                Debug.Log(
                    $"ManifestSceneLoader: FOUND preferred texture " +
                    $"'{path}'.");

                return path;
            }
        }


        // -------------------------------------------------------------
        // 2. GENERIC TEXTURE NAMES
        // -------------------------------------------------------------

        string[] genericNames =
        {
            "texture.png",
            "texture.jpg",
            "texture.jpeg"
        };


        foreach (string filename in genericNames)
        {
            string path =
                Path.Combine(dir, filename);

            if (File.Exists(path))
            {
                Debug.Log(
                    $"ManifestSceneLoader: FOUND generic texture " +
                    $"'{path}'.");

                return path;
            }
        }


        // -------------------------------------------------------------
        // 3. FINAL FALLBACK:
        //    SCAN THE DIRECTORY FOR ANY PNG/JPG/JPEG
        // -------------------------------------------------------------

        string[] files =
            Directory.GetFiles(dir);


        foreach (string file in files)
        {
            string extension =
                Path.GetExtension(file)
                    .ToLowerInvariant();


            bool supported =
                extension == ".png" ||
                extension == ".jpg" ||
                extension == ".jpeg";


            if (!supported)
                continue;


            // Do not accidentally use the heightmap itself.
            string filename =
                Path.GetFileName(file);

            string heightmapFilename =
                Path.GetFileName(heightmapPath);


            if (string.Equals(
                    filename,
                    heightmapFilename,
                    System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }


            Debug.Log(
                $"ManifestSceneLoader: FOUND texture by directory " +
                $"scan '{file}'.");

            return file;
        }


        Debug.LogWarning(
            $"ManifestSceneLoader: no PNG/JPG/JPEG texture found " +
            $"in '{dir}'.");

        return null;
    }


    // =================================================================
    // MANIFEST FINDING
    // =================================================================

    string FindManifestPath(string id)
    {
        string root =
            Path.Combine(
                Application.streamingAssetsPath,
                searchRoot);


        if (!Directory.Exists(root))
            return null;


        // Preferred convention:
        // "<id>_manifest.json" anywhere under search root.

        foreach (string candidate in Directory.GetFiles(
            root,
            $"{id}_manifest.json",
            SearchOption.AllDirectories))
        {
            return candidate;
        }


        // Fallback convention:
        // generic "manifest.json" inside a folder named after id.

        foreach (string candidate in Directory.GetFiles(
            root,
            "manifest.json",
            SearchOption.AllDirectories))
        {
            string folder =
                Path.GetFileName(
                    Path.GetDirectoryName(candidate)
                    ?? string.Empty);


            if (string.Equals(
                folder,
                id,
                System.StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }


        return null;
    }


    // =================================================================
    // HEIGHTMAP FINDING
    // =================================================================

    string FindHeightmapPath(
        string manifestPath,
        string id)
    {
        string dir =
            Path.GetDirectoryName(manifestPath);


        if (dir == null)
            return null;


        // Preferred convention:
        // "<id>_heightmap.png"

        string preferred =
            Path.Combine(
                dir,
                $"{id}_heightmap.png");


        if (File.Exists(preferred))
            return preferred;


        // Fallback:
        // "heightmap.png"

        string fallback =
            Path.Combine(
                dir,
                "heightmap.png");


        if (File.Exists(fallback))
            return fallback;


        return null;
    }


    // =================================================================
    // MANIFEST LOGGING
    // =================================================================

    void LogManifest(
        string path,
        TerrainSceneManifest m)
    {
        Debug.Log(
            $"ManifestSceneLoader: loaded '{path}'\n" +
            $"  id: {m.id}\n" +
            $"  source_image: {m.source_image}\n" +
            $"  heightmap: {m.heightmap} " +
            $"({m.heightmap_encoding}, " +
            $"{m.heightmap_width_px}x{m.heightmap_height_px}px)\n" +
            $"  mesh: {m.mesh_width_px}x{m.mesh_height_px}px, " +
            $"subsample x{m.mesh_subsample_factor}\n" +
            $"  elevation_range_m: " +
            $"{m.elevation_range_m?.min} - " +
            $"{m.elevation_range_m?.max}\n" +
            $"  real_world_size_m: " +
            $"{m.real_world_size_m?.width} x " +
            $"{m.real_world_size_m?.height}\n" +
            $"  reference_available: " +
            $"{m.reference_available}");
    }
}