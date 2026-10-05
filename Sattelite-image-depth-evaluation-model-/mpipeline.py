import os
import sys
import cv2
import json
import math
import shutil
import torch
import numpy as np
import rasterio
from rasterio.warp import transform_bounds
from dem_stitcher import stitch_dem
from scipy.ndimage import gaussian_filter
from depth_anything_v2.dpt import DepthAnythingV2


# ============================================================
# Configuration & Paths
# ============================================================

DEVICE = 'cuda' if torch.cuda.is_available() else 'cpu'

CHECKPOINT_PATH = 'checkpoints/depth_anything_v2_vitl.pth'

# Root folder containing all generated terrain inputs.
#
# Each input gets its own folder inside this directory:
#
# Terrain/
#   Patch_43/
#   Patch_248/
#
TERRAIN_ROOT = '../topography_unity/Assets/StreamingAssets/Terrain'

# These are assigned after the input image is known.
# They are kept as globals so the rest of the pipeline can
# use the same structure as the original code.
TARGET_DIR = None
OUTPUT_OBJ = None
OUTPUT_HEIGHTMAP = None
OUTPUT_TEXTURE = None
OUTPUT_MANIFEST = None


# 800m relief over 5km footprint provides realistic topography
# for non-georeferenced scenes
RELATIVE_FALLBACK_HEIGHT = 800.0

DEFAULT_NON_GEO_SIZE_M = 5000.0


# ============================================================
# Target Directory Management
# ============================================================

def clear_target_dir(target_dir):
    """
    Purges all files and subdirectories ONLY inside the
    current input's output directory.

    Example:

        Terrain/Patch_43/

    can be replaced without affecting:

        Terrain/Patch_248/
    """

    if os.path.exists(target_dir):

        print(
            f"Purging existing contents in "
            f"'{target_dir}/'..."
        )

        for item in os.listdir(target_dir):

            item_path = os.path.join(
                target_dir,
                item
            )

            try:

                if os.path.isdir(item_path):

                    shutil.rmtree(item_path)

                else:

                    os.remove(item_path)

            except Exception as e:

                print(
                    f"Warning: Could not remove "
                    f"{item_path}: {e}"
                )

    else:

        os.makedirs(
            target_dir,
            exist_ok=True
        )


# ============================================================
# Path Resolution
# ============================================================

def resolve_path(user_input, default_dir="dataset"):
    """
    Checks if path exists directly.
    If not, checks inside dataset/.
    """

    cleaned = user_input.strip().strip("'\"")

    if os.path.exists(cleaned):
        return cleaned

    candidate = os.path.join(
        default_dir,
        cleaned
    )

    if os.path.exists(candidate):
        return candidate

    return cleaned


# ============================================================
# Geographic Utilities
# ============================================================

def haversine_distance(
    lon1,
    lat1,
    lon2,
    lat2
):
    """
    Calculates real-world meters between
    two GPS coordinates.
    """

    R = 6371000

    phi_1 = math.radians(lat1)
    phi_2 = math.radians(lat2)

    delta_phi = math.radians(
        lat2 - lat1
    )

    delta_lambda = math.radians(
        lon2 - lon1
    )

    a = (
        math.sin(delta_phi / 2.0) ** 2
        +
        math.cos(phi_1)
        *
        math.cos(phi_2)
        *
        math.sin(delta_lambda / 2.0) ** 2
    )

    return R * (
        2
        *
        math.atan2(
            math.sqrt(a),
            math.sqrt(1 - a)
        )
    )


# ============================================================
# OBJ Export
# ============================================================

def export_obj_mesh(
    dsm_array,
    output_path,
    subsample=2
):
    """
    Exports DSM array to a 3D OBJ file
    with vertex coordinates and UVs.
    """

    print(
        f"Exporting 3D OBJ Mesh to "
        f"{output_path}..."
    )

    dsm = dsm_array[
        ::subsample,
        ::subsample
    ]

    h, w = dsm.shape

    with open(
        output_path,
        'w'
    ) as f:

        f.write(
            "o Terrain\n"
        )

        # ----------------------------------------------------
        # Vertices & UVs
        # ----------------------------------------------------

        for y in range(h):

            for x in range(w):

                z = dsm[y, x]

                pos_x = x - (w / 2)
                pos_y = z
                pos_z = y - (h / 2)

                f.write(
                    f"v {pos_x} "
                    f"{pos_y} "
                    f"{pos_z}\n"
                )

                u = x / (w - 1)
                v = 1.0 - (
                    y / (h - 1)
                )

                f.write(
                    f"vt {u} {v}\n"
                )

        # ----------------------------------------------------
        # Faces
        # ----------------------------------------------------

        for y in range(h - 1):

            for x in range(w - 1):

                i = (
                    y * w
                    + x
                    + 1
                )

                f.write(
                    f"f {i}/{i} "
                    f"{i+w}/{i+w} "
                    f"{i+1}/{i+1}\n"
                )

                f.write(
                    f"f {i+1}/{i+1} "
                    f"{i+w}/{i+w} "
                    f"{i+w+1}/{i+w+1}\n"
                )


# ============================================================
# Main Pipeline
# ============================================================

def run_pipeline():

    global TARGET_DIR
    global OUTPUT_OBJ
    global OUTPUT_HEIGHTMAP
    global OUTPUT_TEXTURE
    global OUTPUT_MANIFEST

    print(
        "=== DeepWizard: "
        "ML to Unity Master Pipeline ==="
    )

    # --------------------------------------------------------
    # Get Input Image
    # --------------------------------------------------------

    if len(sys.argv) > 1:

        raw_input = sys.argv[1]

    else:

        raw_input = input(
            "Enter image filepath "
            "(e.g., sample.jpg): "
        )

    img_path = resolve_path(
        raw_input
    )

    if not os.path.exists(img_path):

        raise FileNotFoundError(
            f"File not found: checked "
            f"'{raw_input}' and "
            f"'dataset/{raw_input}'"
        )


    # ========================================================
    # NEW MULTI-INPUT OUTPUT LOGIC
    # ========================================================

    # Example:
    #
    # Patch_43.tif
    #     -> Patch_43
    #
    # Patch_248.tif
    #     -> Patch_248

    run_id = os.path.splitext(
        os.path.basename(img_path)
    )[0]


    # Remove accidental leading/trailing
    # whitespace from the folder name.
    run_id = run_id.strip()


    if not run_id:

        raise ValueError(
            "Could not determine a valid "
            "scene ID from the input filename."
        )


    # --------------------------------------------------------
    # Current input gets its OWN directory
    # --------------------------------------------------------

    TARGET_DIR = os.path.join(
        TERRAIN_ROOT,
        run_id
    )


    print(
        f"\nInput image : {img_path}"
    )

    print(
        f"Scene ID    : {run_id}"
    )

    print(
        f"Output dir  : {TARGET_DIR}"
    )


    # --------------------------------------------------------
    # IMPORTANT:
    #
    # Only the current scene folder is cleared.
    #
    # Existing scenes remain untouched.
    # --------------------------------------------------------

    clear_target_dir(
        TARGET_DIR
    )


    # --------------------------------------------------------
    # Output file paths
    # --------------------------------------------------------

    OUTPUT_OBJ = os.path.join(
        TARGET_DIR,
        'terrain_mesh.obj'
    )

    OUTPUT_HEIGHTMAP = os.path.join(
        TARGET_DIR,
        'heightmap.png'
    )

    OUTPUT_TEXTURE = os.path.join(
        TARGET_DIR,
        'texture.jpg'
    )

    OUTPUT_MANIFEST = os.path.join(
        TARGET_DIR,
        'manifest.json'
    )


    # ========================================================
    # Determine Image Type
    # ========================================================

    ext = os.path.splitext(
        img_path
    )[1].lower()

    is_georeferenced = ext in [
        '.tif',
        '.tiff'
    ]


    # ========================================================
    # 1. Load Image Array
    # ========================================================

    print(
        "\n[1/4] Loading Image "
        "& Converting Texture..."
    )


    if is_georeferenced:

        with rasterio.open(
            img_path
        ) as src:

            bounds = src.bounds
            crs = src.crs

            if crs is None:

                print(
                    "Warning: TIFF lacks CRS "
                    "metadata. Falling back "
                    "to Relative Mode."
                )

                is_georeferenced = False

            r = src.read(1)
            g = src.read(2)
            b = src.read(3)

            rgb = np.dstack(
                (
                    r,
                    g,
                    b
                )
            )

            bgr_image = cv2.cvtColor(
                rgb,
                cv2.COLOR_RGB2BGR
            )

    else:

        bgr_image = cv2.imread(
            img_path
        )


    if bgr_image is None:

        raise RuntimeError(
            f"Could not load image: "
            f"{img_path}"
        )


    # ========================================================
    # 2. Normalize and Export Texture
    # ========================================================

    if bgr_image.dtype != np.uint8:

        texture_export = cv2.normalize(
            bgr_image,
            None,
            0,
            255,
            cv2.NORM_MINMAX
        ).astype(
            np.uint8
        )

    else:

        texture_export = bgr_image


    cv2.imwrite(
        OUTPUT_TEXTURE,
        texture_export,
        [
            int(
                cv2.IMWRITE_JPEG_QUALITY
            ),
            95
        ]
    )


    print(
        f"Exported texture to "
        f"{OUTPUT_TEXTURE}"
    )


    # ========================================================
    # 3. Run Foundation Model
    # ========================================================

    print(
        "Extracting AI Depth..."
    )


    model = DepthAnythingV2(
        encoder='vitl',
        features=256,
        out_channels=[
            256,
            512,
            1024,
            1024
        ]
    )


    model.load_state_dict(
        torch.load(
            CHECKPOINT_PATH,
            map_location='cpu'
        )
    )


    model = model.to(
        DEVICE
    ).eval()


    with torch.no_grad():

        raw_depth = model.infer_image(
            bgr_image
        )


    h, w = raw_depth.shape


    mesh_subsample = 2


    # ========================================================
    # Manifest
    # ========================================================

    manifest = {

        "id":
            run_id,

        "source_image":
            os.path.basename(
                img_path
            ),

        "texture":
            OUTPUT_TEXTURE.replace(
                "\\",
                "/"
            ),

        "heightmap":
            OUTPUT_HEIGHTMAP.replace(
                "\\",
                "/"
            ),

        "heightmap_encoding":
            "grayscale16",

        "heightmap_width_px":
            int(w),

        "heightmap_height_px":
            int(h),

        "mesh_width_px":
            int(
                w // mesh_subsample
            ),

        "mesh_height_px":
            int(
                h // mesh_subsample
            ),

        "mesh_subsample_factor":
            mesh_subsample
    }


    # ========================================================
    # 4. Elevation Processing Router
    # ========================================================

    if is_georeferenced:

        print(
            "\n[2/4] GEOTIFF DETECTED: "
            "Running Absolute Calibration "
            "Pipeline..."
        )


        gps_bounds = transform_bounds(
            crs,
            'EPSG:4326',
            bounds.left,
            bounds.bottom,
            bounds.right,
            bounds.top
        )


        print(
            "Downloading Copernicus "
            "GLO-30 Elevation data..."
        )


        dem_array, _ = stitch_dem(
            list(gps_bounds),
            dem_name='glo_30',
            dst_ellipsoidal_height=False
        )


        srtm_aligned = cv2.resize(
            dem_array,
            (w, h),
            interpolation=cv2.INTER_CUBIC
        )


        # Robust Scaling

        rel_min, rel_max = np.percentile(
            raw_depth,
            2
        ), np.percentile(
            raw_depth,
            98
        )


        dem_min, dem_max = np.percentile(
            srtm_aligned,
            2
        ), np.percentile(
            srtm_aligned,
            98
        )


        scale_factor = (
            dem_max - dem_min
        ) / (
            rel_max - rel_min
            + 1e-6
        )


        rel_scaled = (
            dem_min
            +
            (
                raw_depth
                - rel_min
            )
            * scale_factor
        )


        # High-Pass Filter

        sigma = max(
            h,
            w
        ) / 30.0


        rel_blurred = gaussian_filter(
            rel_scaled,
            sigma=sigma
        )


        high_freq = (
            rel_scaled
            - rel_blurred
        )


        # Fuse Absolute Terrain

        final_dsm = (
            srtm_aligned
            + high_freq
        )


        final_dsm = np.clip(
            final_dsm,
            dem_min - 15,
            dem_max + 50
        )


        true_min = float(
            final_dsm.min()
        )

        true_max = float(
            final_dsm.max()
        )


        manifest[
            "elevation_range_m"
        ] = {

            "min":
                round(
                    true_min,
                    2
                ),

            "max":
                round(
                    true_max,
                    2
                )
        }


        manifest[
            "reference_available"
        ] = True


        # Real-world metric size

        min_lon, min_lat, max_lon, max_lat = (
            gps_bounds
        )


        width_m = haversine_distance(
            min_lon,
            min_lat,
            max_lon,
            min_lat
        )


        height_m = haversine_distance(
            min_lon,
            min_lat,
            min_lon,
            max_lat
        )


        manifest[
            "real_world_size_m"
        ] = {

            "width":
                round(
                    width_m,
                    2
                ),

            "height":
                round(
                    height_m,
                    2
                )
        }


        mesh_dsm = final_dsm


    else:

        print(
            "\n[2/4] PNG/JPG DETECTED: "
            "Running Relative Flattening "
            "Pipeline..."
        )


        # High-Pass Filter

        sigma = max(
            h,
            w
        ) / 30.0


        tilt_map = gaussian_filter(
            raw_depth,
            sigma=sigma
        )


        flattened_depth = (
            raw_depth
            - tilt_map
        )


        flattened_depth = np.clip(
            flattened_depth,
            0,
            None
        )


        d_min = flattened_depth.min()
        d_max = flattened_depth.max()


        if d_max - d_min > 0:

            norm_depth = (
                flattened_depth
                - d_min
            ) / (
                d_max
                - d_min
            )

        else:

            norm_depth = flattened_depth


        final_dsm = (
            norm_depth
            * RELATIVE_FALLBACK_HEIGHT
        )


        manifest[
            "elevation_range_m"
        ] = {

            "min": 0.0,

            "max":
                RELATIVE_FALLBACK_HEIGHT
        }


        manifest[
            "reference_available"
        ] = False


        # 5 km default footprint
        # matching aspect ratio

        manifest[
            "real_world_size_m"
        ] = {

            "width":
                DEFAULT_NON_GEO_SIZE_M,

            "height":
                round(
                    DEFAULT_NON_GEO_SIZE_M
                    *
                    (
                        float(h)
                        /
                        float(w)
                    ),
                    2
                )
        }


        mesh_dsm = final_dsm


    # ========================================================
    # 5. Export Unity Assets
    # ========================================================

    print(
        "\n[3/4] Exporting Data Formats..."
    )


    # --------------------------------------------------------
    # A. 16-bit Heightmap PNG
    # --------------------------------------------------------

    dsm_min = final_dsm.min()
    dsm_max = final_dsm.max()


    normalized_for_png = (
        final_dsm
        - dsm_min
    ) / (
        dsm_max
        - dsm_min
        + 1e-6
    )


    heightmap_16bit = (
        normalized_for_png
        * 65535
    ).astype(
        np.uint16
    )


    cv2.imwrite(
        OUTPUT_HEIGHTMAP,
        heightmap_16bit
    )


    print(
        f"Exported heightmap to "
        f"{OUTPUT_HEIGHTMAP}"
    )


    # --------------------------------------------------------
    # B. Write manifest.json
    # --------------------------------------------------------

    with open(
        OUTPUT_MANIFEST,
        "w"
    ) as f:

        json.dump(
            manifest,
            f,
            indent=4
        )


    print(
        f"Exported manifest to "
        f"{OUTPUT_MANIFEST}"
    )


    # --------------------------------------------------------
    # C. Export OBJ Mesh
    # --------------------------------------------------------

    export_obj_mesh(
        mesh_dsm,
        OUTPUT_OBJ,
        subsample=mesh_subsample
    )


    # ========================================================
    # Complete
    # ========================================================

    print(
        "\n[4/4] Pipeline Complete!"
    )

    print(
        "Target folder populated with:"
    )

    print(
        f"  - {OUTPUT_OBJ}"
    )

    print(
        f"  - {OUTPUT_HEIGHTMAP}"
    )

    print(
        f"  - {OUTPUT_TEXTURE}"
    )

    print(
        f"  - {OUTPUT_MANIFEST}"
    )

    print(
        f"\nScene ID: {run_id}"
    )

    print(
        "\nExisting terrain folders "
        "were preserved."
    )


# ============================================================
# Entry Point
# ============================================================

if __name__ == '__main__':
    run_pipeline()
