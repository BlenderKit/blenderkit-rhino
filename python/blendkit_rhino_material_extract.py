"""Headless: BlenderKit material .blend -> JSON manifest for the Rhino plug-in.

Run as a Blender background script:
    blender --background <blend> --python <this> -- <out_json>

Output (one Principled BSDF flattened):
    {
      "name": "...",
      "base_color_texture": "C:/.../tex.jpg" | null,
      "base_color_rgba": [r,g,b,a]          | null,
      "metallic_texture":  ...,  "metallic":          ...,
      "roughness_texture": ...,  "roughness":         ...,
      "normal_texture":    ...,
      "emission_texture":  ...,  "emission_rgba":     ...,
                                 "emission_strength": ...,
      "alpha_texture":     ...,  "alpha":             ...
    }

For BlenderKit material assets the texture files are usually packed
inside the .blend (single-file delivery). We use the BlenderKit Blender
addon's canonical unpacking pattern (lifted from
blenderkit/unpack_asset_bg.py: get_texture_filepath +
image.unpack(method='WRITE_ORIGINAL')) so the disk layout matches what
the addon itself produces - //textures_<res>/<filename>.

Once images are unpacked, bpy.path.abspath resolves the per-image
filepath to a real file we can pass back to the Rhino C# importer;
that side rebuilds the equivalent Rhino PBR Material with bitmap
texture children.

Note: an earlier iteration of this script also emitted a Cycles
standalone-XML shader graph, intending to load it via RhinoCyclesCore's
XmlMaterial. That path turned out not to work - RhinoCycles' XML
parser ignores image-texture filenames; the relevant code is wrapped
in `#if DISABLEFORNOW` in CCSycles/ShaderNodes/TextureNode.cs. So we
write only JSON now and the Rhino side does the wiring through the
standard RenderContent API.
"""
import bpy
import json
import os
import sys
import traceback


# ---- Blender addon canonical unpacker (lifted from
#      blenderkit/unpack_asset_bg.py + paths.py) ----

# blenderkit/paths.py:resolution_suffix
RESOLUTION_SUFFIX = {
    'blend':            '',
    'resolution_0_5K':  '_05k',
    'resolution_1K':    '_1k',
    'resolution_2K':    '_2k',
    'resolution_4K':    '_4k',
    'resolution_8K':    '_8k',
}


def get_resolution_from_file_path(file_path):
    """blenderkit/unpack_asset_bg.py:get_resolution_from_file_path."""
    file_path = file_path or ''
    for tag, name in (('_0_5K_', 'resolution_0_5K'),
                      ('_1K_',   'resolution_1K'),
                      ('_2K_',   'resolution_2K'),
                      ('_4K_',   'resolution_4K'),
                      ('_8K_',   'resolution_8K')):
        if tag in file_path:
            return name
    return 'blend'


def get_texture_filepath(tex_dir_path, image, resolution='blend', source_path=''):
    """blenderkit/unpack_asset_bg.py:get_texture_filepath (verbatim)."""
    if source_path:
        path = source_path
    elif len(image.packed_files) > 0:
        path = image.packed_files[0].filepath
    else:
        path = image.filepath
    path = (path or '').replace('\\', '/')
    image_file_name = bpy.path.basename(path)
    if image_file_name == '':
        image_file_name = image.name.split('.')[0]
    file_path_original = os.path.join(tex_dir_path, image_file_name)
    file_path_final = file_path_original
    i = 0
    while True:
        is_solo = True
        for image1 in bpy.data.images:
            if image is not image1 and image1.filepath == file_path_final:
                is_solo = False
                fpleft, fpext = os.path.splitext(file_path_original)
                file_path_final = fpleft + str(i).zfill(3) + fpext
                i += 1
        if is_solo:
            break
    return file_path_final


def unpack_all_images_addon_style():
    """Subset of blenderkit/unpack_asset_bg.py:unpack_asset (textures only).

    Always unpacks - the Rhino render path needs real on-disk textures.
    """
    resolution = get_resolution_from_file_path(bpy.data.filepath)
    tex_dir_path = '//textures' + RESOLUTION_SUFFIX.get(resolution, '') + '/'
    tex_dir_abs = bpy.path.abspath(tex_dir_path)
    if not os.path.exists(tex_dir_abs):
        try:
            os.makedirs(tex_dir_abs, exist_ok=True)
        except Exception:
            traceback.print_exc()
    bpy.data.use_autopack = False
    for image in bpy.data.images:
        if image.name == 'Render Result':
            continue
        try:
            if len(image.packed_files) > 0:
                # Per-packed-file paths preserved for UDIM/sequence support.
                for pf in image.packed_files:
                    pf_path = get_texture_filepath(tex_dir_path, image,
                                                   resolution=resolution,
                                                   source_path=pf.filepath)
                    pf.filepath = pf_path
                image_path = get_texture_filepath(tex_dir_path, image,
                                                  resolution=resolution,
                                                  source_path=image.filepath)
                image.filepath = image_path
                image.filepath_raw = image_path
                image.unpack(method='WRITE_ORIGINAL')
            else:
                fp = get_texture_filepath(tex_dir_path, image,
                                          resolution=resolution,
                                          source_path=image.filepath)
                image.filepath = fp
                image.filepath_raw = fp
        except Exception:
            traceback.print_exc()


# ---- Principled BSDF -> dict ----


def find_principled(material):
    if not material.use_nodes or material.node_tree is None:
        return None
    for node in material.node_tree.nodes:
        if node.type == 'BSDF_PRINCIPLED':
            return node
    return None


def upstream_image(socket):
    """Walk back to the nearest TEX_IMAGE node from this socket."""
    if socket is None or not socket.is_linked:
        return None
    visited = set()
    stack = [socket.links[0].from_node]
    while stack:
        node = stack.pop()
        if node in visited:
            continue
        visited.add(node)
        if node.type == 'TEX_IMAGE' and node.image:
            return resolve_image_path(node.image)
        for inp in node.inputs:
            if inp.is_linked:
                stack.append(inp.links[0].from_node)
    return None


def resolve_image_path(image):
    if image is None:
        return None
    try:
        p = bpy.path.abspath(image.filepath, library=image.library)
    except Exception:
        return None
    if not p:
        return None
    p = os.path.normpath(p)
    return p if os.path.isfile(p) else None


def value_of(socket):
    if socket is None or socket.is_linked:
        return None
    v = socket.default_value
    try:
        return list(v)
    except TypeError:
        return float(v)


def extract_principled(material):
    bsdf = find_principled(material)
    if bsdf is None:
        return None
    ins = bsdf.inputs

    def take(name):
        s = ins.get(name)
        return upstream_image(s), value_of(s)

    bc_tex, bc_val = take('Base Color')
    m_tex,  m_val  = take('Metallic')
    r_tex,  r_val  = take('Roughness')
    n_tex,  _      = take('Normal')
    # Blender 3.x uses 'Emission'; 4.x renamed to 'Emission Color'.
    e_tex, e_val   = take('Emission Color')
    if e_tex is None and e_val is None:
        e_tex, e_val = take('Emission')
    es_tex, es_val = take('Emission Strength')
    a_tex, a_val   = take('Alpha')
    return {
        'name': material.name,
        'base_color_texture': bc_tex, 'base_color_rgba': bc_val,
        'metallic_texture':   m_tex,  'metallic':        m_val,
        'roughness_texture':  r_tex,  'roughness':       r_val,
        'normal_texture':     n_tex,
        'emission_texture':   e_tex,  'emission_rgba':   e_val,
        'emission_strength':  es_val,
        'alpha_texture':      a_tex,  'alpha':           a_val,
    }


# ---- Main ----

argv = sys.argv
if '--' not in argv:
    print('material_extract: missing -- separator', file=sys.stderr)
    sys.exit(2)
argv = argv[argv.index('--') + 1:]
if not argv:
    print('material_extract: no output path given', file=sys.stderr)
    sys.exit(2)

out_json = argv[0]

unpack_all_images_addon_style()

material = None
for m in bpy.data.materials:
    if find_principled(m):
        material = m
        break
if material is None:
    print('material_extract: no Principled BSDF material found', file=sys.stderr)
    sys.exit(3)

values = extract_principled(material)

with open(out_json, 'w', encoding='utf-8') as f:
    json.dump(values, f, indent=2)
print('material_extract: wrote', out_json)
