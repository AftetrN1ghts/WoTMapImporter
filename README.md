# WoT Map Importer for Unity

A Unity Editor plugin that imports **World of Tanks** maps directly from `.pkg` files
into your Unity project. Inspired by [Simi4/WoT-Blender-Addons](https://github.com/Simi4/WoT-Blender-Addons)
`tank_viewer`, but for Unity.

## Features

- ✅ Reads `arena_defs/_list_.xml` to discover all maps (arenas + hangars)
- ✅ Decodes `*.cdata` chunks (heights PNG, normals, blend_textures DXT5, layer definitions)
- ✅ Stitches all chunks into a **single Unity Terrain** (with configurable resolution)
- ✅ Replicates WoT's blend-layer logic (new + old formats)
- ✅ Imports layer diffuse + normal textures (PNG/DDS)
- ✅ Loads static objects via `.primitives_processed` (basic geometry + diffuse)
- ⚠️ Atlas shaders / PBS_tiled_atlas: simplified (diffuse only, no per-pixel blend masks)
- ⚠️ Full CompiledSpace (space.bin) parsing not yet implemented; falls back to chunk name → bounds
- ⚠️ Water / sky: not implemented yet

## Requirements

- Unity **2022.3 LTS** or newer (also tested with 2023.x)
- An installed copy of **World of Tanks** (or access to its `res/packages/` folder)
- .NET Standard 2.1 / .NET 4.x scripting backend

## Installation

### Option A: as a local package (Unity Package Manager)
1. Copy the `WoTMapImporter` folder into your project's `Packages/` directory.
2. Unity will pick it up automatically.

### Option B: as Assets
1. Copy the `WoTMapImporter` folder into `Assets/` of your project.
2. The Editor scripts compile into the Editor assembly.

## Usage

1. Open **Tools → WoT Map Importer**.
2. Set the path to your **World of Tanks** installation (e.g. `C:\Games\World_of_Tanks`).
3. Set the **Output Folder** (e.g. `Assets/WoTImported`).
4. Click **"Parse WoT (load maps)"** — wait a few seconds while it reads `scripts.pkg`.
5. Pick a map from the list.
6. Click **"Import Selected Map"**.

The importer will create a Unity Terrain (with terrain layers from WoT's diffuse textures),
plus a prefab containing the terrain GameObject. All textures and the TerrainData asset are
saved under your Output Folder.

## How it works

The plugin reads `.pkg` files (which are just ZIP archives):

- `arena_defs/_list_.xml` — list of all maps
- `arena_defs/<map>.xml` — bounding box, geometry path
- `<map>.pkg` and `<map>_bin.pkg` — actual map data
- `particles.pkg`, `shared*.pkg` — additional resources (textures, models)

Each terrain chunk is a `*.cdata` file (also a ZIP) containing:

- `terrain2/heights1` — PNG with heights encoded in RGB channels
- `terrain2/normals` — PNG (v1) or DXT5 DDS (v2) normal map
- `terrain2/blend_textures` — DXT5 DDS RGBA weights (new format)
- `terrain2/layers` — layer definitions (new format) — u/v projection, name, normal map
- `terrain2/layer 1` … `layer N` — per-layer textures (old format)

### Height decoding (from `terrain_loader.py`)

```
height = (R + G*256 + B_signed*65536) / (1000/256)
where B_signed = B > 0.5 ? B - 1.0039216 : B
```

### Blending (matches the Blender addon)

Each blend texture has RGBA weights. For the **new format**:
- `blend[i].A → layer[2i].weight`
- `blend[i].G → layer[2i+1].weight`

The chain rule from the addon:
```
out[0] = color[0]*w0 + out[1]
out[1] = color[1]*w1 + out[2]
…
out[n-1] = color[n-1]*w_{n-1}
```

The plugin packs these weights into Unity's splat maps (each tile holds 4 layer weights).

## Limitations

| Feature | Status |
|---------|--------|
| Terrain heightmap | ✅ Full |
| Terrain normals | ✅ Full |
| Terrain blend layers (new) | ✅ Full |
| Terrain blend layers (old) | ✅ Full |
| Terrain tileable UV projection | ⚠️ Approximate — uses 10m default tile size |
| Wetness / global AM map | ⚠️ Disabled by default |
| Static objects (basic) | ✅ Simple diffuse + geometry |
| Atlas shaders (PBS_tiled_atlas) | ⚠️ Diffuse only, no proper blending |
| Water planes | ❌ Not implemented |
| Sky / lighting | ❌ Not implemented |
| CompiledSpace space.bin | ❌ Falls back to cdata-based bounds |

## License

This plugin is a derivative work of Simi4/WoT-Blender-Addons (MIT licensed). Use at your own
risk; respect Wargaming's terms of service.

## Credits

- Original Python importer: [Simi4](https://github.com/Simi4) — `tank_viewer` in WoT-Blender-Addons
- Ported to C# / Unity by Arena.ai
