bl_info = {
    "name": "s&box Bridge (Mimiclay)",
    "author": "Mimiclay",
    "version": (1, 0),
    "blender": (4, 2, 0),
    "location": "3D Viewport > Sidebar (N) > s&box",
    "description": "Pull s&box level geometry into the scene, push the scene back as FBX, and rescale objects with their modifiers",
    "category": "Import-Export",
}

"""
Blender half of the Mimiclay level bridge.

  s&box  ->  Blender : the editor's "Mimiclay > Blender > Send Scene Geometry" menu (or the MCP tool
                       blender_send_scene) writes Tools/blender/bridge/scene.obj + scene.json. "Pull" here
                       imports that into a locked 'sbox_scene' collection, scaled from s&box units into
                       Blender units and placed in the FBX importer's frame, so it lines up with the
                       objects you model. Auto-pull polls the manifest and re-imports when it changes.
  Blender -> s&box : "Push FBX" exports every mesh that is NOT bridge geometry to the project's FBX
                       (defaults to Assets/Prefabs/Kitchen/Kitchen.fbx), with the settings the existing
                       exports used (UnitScaleFactor 1, object scale kept, -Z forward / Y up).

  Rescale Objects   : scales mesh data (optionally folding object scale in), locations, and every
                       modifier value that is a length — bevel width, solidify thickness, array constant
                       offset, weld/mirror/screw merge distances, displace strength, geometry-nodes
                       DISTANCE inputs... — so the result LOOKS identical, just resized. Percent-type
                       bevels and angle inputs are left alone. The operator verifies itself: every
                       object's evaluated bounding box must grow by exactly the factor.
"""

import bpy
import json
import math
import os
import time
from mathutils import Vector, Matrix

BRIDGE_COLLECTION = "sbox_scene"
BRIDGE_PROP = "sbox_bridge"
DEFAULT_PROJECT = r"D:\SBox\Projects\mimiclay"


# ----------------------------------------------------------------------------------------------
# settings
# ----------------------------------------------------------------------------------------------

def _default_bridge_dir():
    return os.path.join(DEFAULT_PROJECT, "Tools", "blender", "bridge")


def _default_fbx_path():
    return os.path.join(DEFAULT_PROJECT, "Assets", "Prefabs", "Kitchen", "Kitchen.fbx")


class SboxBridgeSettings(bpy.types.PropertyGroup):
    bridge_dir: bpy.props.StringProperty(
        name="Bridge Folder", subtype="DIR_PATH", default=_default_bridge_dir(),
        description="Folder the s&box editor writes scene.obj / scene.json into")
    units_per_bu: bpy.props.FloatProperty(
        name="s&box units per BU", default=3.0 / 2.54, precision=5, min=0.0001,
        description="How many s&box units one Blender unit becomes after export + import "
                    "(FBX importer ImportScale / 2.54 for the usual UnitScaleFactor-1 exports). "
                    "Pull reads the value the editor reports; edit it here to override")
    use_manifest_units: bpy.props.BoolProperty(
        name="Use editor's unit factor", default=True,
        description="Take the unit factor from scene.json on every pull instead of the value above")
    auto_pull: bpy.props.BoolProperty(
        name="Auto-pull", default=False,
        description="Poll the bridge folder and re-import whenever the editor sends new geometry",
        update=lambda self, ctx: _auto_pull_toggle(self.auto_pull))
    lock_selection: bpy.props.BoolProperty(
        name="Unselectable", default=False,
        description="Make the imported s&box geometry unselectable so it can't be nudged by accident")
    spawn_markers: bpy.props.BoolProperty(
        name="Spawn markers", default=True,
        description="Add an empty per spawn point and a player-sized capsule for scale")
    fit_clipping: bpy.props.BoolProperty(
        name="Fit viewport clipping", default=True,
        description="After a pull, set every 3D viewport's (and camera's) clip start/end to suit the scene's size")
    last_pull: bpy.props.StringProperty(name="Last pull", default="")
    last_manifest_mtime: bpy.props.FloatProperty(name="mtime", default=0.0)
    fbx_path: bpy.props.StringProperty(
        name="FBX", subtype="FILE_PATH", default=_default_fbx_path(),
        description="Where Push writes the FBX the s&box FbxBlockoutImporter reads")

    # rescale operator defaults (kept on the scene so they persist)
    rescale_factor: bpy.props.FloatProperty(name="Factor", default=3.0 / 2.54, precision=5, min=0.0001)
    rescale_apply_object_scale: bpy.props.BoolProperty(
        name="Fold object scale into mesh", default=True,
        description="Bake each object's Scale into its mesh (and its modifier values) so Scale ends at 1")
    rescale_locations: bpy.props.BoolProperty(
        name="Scale locations", default=True,
        description="Move objects so the whole layout scales about the world origin, not just each mesh about its pivot")
    rescale_selected_only: bpy.props.BoolProperty(name="Selected only", default=True)


# ----------------------------------------------------------------------------------------------
# pull: s&box -> Blender
# ----------------------------------------------------------------------------------------------

def _manifest_path(settings):
    return os.path.join(bpy.path.abspath(settings.bridge_dir), "scene.json")


def _obj_path(settings):
    return os.path.join(bpy.path.abspath(settings.bridge_dir), "scene.obj")


def _get_or_make_collection(name, parent):
    coll = bpy.data.collections.get(name)
    if coll is None:
        coll = bpy.data.collections.new(name)
    if coll.name not in parent.children:
        try:
            parent.children.link(coll)
        except RuntimeError:
            pass
    return coll


def _clear_collection(coll):
    for obj in list(coll.objects):
        data = obj.data
        bpy.data.objects.remove(obj, do_unlink=True)
        if data is not None and data.users == 0:
            if isinstance(data, bpy.types.Mesh):
                bpy.data.meshes.remove(data)
    for child in list(coll.children):
        _clear_collection(child)
        bpy.data.collections.remove(child)


def _bridge_material():
    mat = bpy.data.materials.get("sbox_scene")
    if mat is None:
        mat = bpy.data.materials.new("sbox_scene")
        mat.diffuse_color = (0.55, 0.6, 0.65, 1.0)
        mat.roughness = 0.9
    # per-material culling only applies in Material Preview / Rendered; Solid mode uses the viewport flag
    mat.use_backface_culling = True
    return mat


def set_viewport_backface_culling(enabled):
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type != "VIEW_3D":
                continue
            for space in area.spaces:
                if space.type == "VIEW_3D":
                    space.shading.show_backface_culling = enabled


class SBOX_OT_backface_culling(bpy.types.Operator):
    """Toggle Solid-mode backface culling in every 3D viewport (a viewport setting, so it affects all objects).
Lets you look into the room through the walls that face away from you"""
    bl_idname = "sbox.backface_culling"
    bl_label = "Viewport Backface Culling"

    enabled: bpy.props.BoolProperty(name="Enabled", default=True)

    def execute(self, context):
        set_viewport_backface_culling(self.enabled)
        return {"FINISHED"}


def pull(context, report=None):
    settings = context.scene.sbox_bridge
    manifest_file = _manifest_path(settings)
    obj_file = _obj_path(settings)
    if not os.path.isfile(manifest_file) or not os.path.isfile(obj_file):
        return False, "Nothing in the bridge folder yet — use Mimiclay > Blender > Send Scene Geometry in the s&box editor"

    with open(manifest_file, "r", encoding="utf-8") as f:
        manifest = json.load(f)

    if settings.use_manifest_units and manifest.get("sbox_units_per_blender_unit"):
        settings.units_per_bu = float(manifest["sbox_units_per_blender_unit"])
    upb = settings.units_per_bu
    scale = 1.0 / upb

    # remember the view layer's active collection + selection so the import doesn't disturb the user
    view_layer = context.view_layer
    prev_active = view_layer.active_layer_collection
    prev_selected = [o for o in context.selected_objects]
    prev_active_obj = view_layer.objects.active

    root = _get_or_make_collection(BRIDGE_COLLECTION, context.scene.collection)
    _clear_collection(root)

    # import into the bridge collection by making it active for the operator
    layer_coll = _find_layer_collection(view_layer.layer_collection, root.name)
    if layer_coll is not None:
        view_layer.active_layer_collection = layer_coll

    before = set(bpy.data.objects)
    # the OBJ is already Z-up / same handedness as Blender: no axis conversion, only the unit scale
    bpy.ops.wm.obj_import(filepath=obj_file, forward_axis="Y", up_axis="Z", global_scale=scale,
                          use_split_objects=True, use_split_groups=False, validate_meshes=True)
    imported = [o for o in bpy.data.objects if o not in before]

    mat = _bridge_material()
    by_obj_name = {o.get("obj_name"): o for o in manifest.get("objects", [])}
    for obj in imported:
        obj[BRIDGE_PROP] = True
        obj.hide_render = True
        obj.hide_select = settings.lock_selection
        info = by_obj_name.get(obj.name)
        if info is not None:
            obj["sbox_path"] = info.get("path", "")
            obj["sbox_kind"] = info.get("kind", "")
        if obj.type == "MESH":
            obj.data.materials.clear()
            obj.data.materials.append(mat)
            for poly in obj.data.polygons:
                poly.use_smooth = False
        for coll in list(obj.users_collection):
            if coll is not root:
                coll.objects.unlink(obj)
        if obj.name not in root.objects:
            root.objects.link(obj)

    if settings.spawn_markers:
        _add_spawn_markers(context, manifest, scale, root)

    # restore the user's context
    if prev_active is not None:
        try:
            view_layer.active_layer_collection = prev_active
        except Exception:
            pass
    for o in imported:
        o.select_set(False)
    for o in prev_selected:
        try:
            o.select_set(True)
        except Exception:
            pass
    view_layer.objects.active = prev_active_obj

    if settings.fit_clipping:
        fit_clipping(context)

    settings.last_manifest_mtime = os.path.getmtime(manifest_file)
    settings.last_pull = "%s — %d objects @ %.4g u/BU" % (time.strftime("%H:%M:%S"), len(imported), upb)
    return True, "Pulled %d objects from '%s' (%.4g s&box units per Blender unit)" % (
        len(imported), manifest.get("scene", "?"), upb)


def _find_layer_collection(layer_coll, name):
    if layer_coll.collection.name == name:
        return layer_coll
    for child in layer_coll.children:
        found = _find_layer_collection(child, name)
        if found is not None:
            return found
    return None


def _add_spawn_markers(context, manifest, scale, root):
    markers = _get_or_make_collection(BRIDGE_COLLECTION + "_markers", root)
    player = manifest.get("player", {})
    height = float(player.get("body_height", 70.0)) * scale
    radius = float(player.get("body_radius", 16.0)) * scale
    for i, spawn in enumerate(manifest.get("spawns", [])):
        pos = Vector(spawn.get("position", (0, 0, 0))) * scale
        name = "spawn_%s" % spawn.get("name", i)
        empty = bpy.data.objects.new(name, None)
        empty.empty_display_type = "SINGLE_ARROW" if spawn.get("hunter") else "PLAIN_AXES"
        empty.empty_display_size = height
        empty.location = pos
        empty.rotation_euler = (0.0, 0.0, math.radians(float(spawn.get("yaw", 0.0))))
        empty[BRIDGE_PROP] = True
        empty.hide_render = True
        markers.objects.link(empty)
    if manifest.get("spawns"):
        # one player-sized capsule at the first spawn so the scale reads at a glance
        spawn = manifest["spawns"][0]
        pos = Vector(spawn.get("position", (0, 0, 0))) * scale
        mesh = bpy.data.meshes.new("sbox_player")
        _capsule(mesh, radius, height)
        obj = bpy.data.objects.new("sbox_player (%.0fu tall)" % float(player.get("body_height", 70.0)), mesh)
        obj.location = pos
        obj[BRIDGE_PROP] = True
        obj.hide_render = True
        obj.display_type = "WIRE"
        markers.objects.link(obj)


def _capsule(mesh, radius, height, segments=12, rings=4):
    # cylinder from 0..height with hemispherical ends folded inside the height (player hull)
    verts, faces = [], []
    cyl_h = max(height - 2 * radius, 0.0)
    rows = []
    for r in range(rings + 1):
        a = -math.pi / 2 + (math.pi / 2) * r / rings
        z = radius + radius * math.sin(a)
        rr = radius * math.cos(a)
        rows.append((z, rr))
    for r in range(rings + 1):
        a = (math.pi / 2) * r / rings
        z = radius + cyl_h + radius * math.sin(a)
        rr = radius * math.cos(a)
        rows.append((z, rr))
    for z, rr in rows:
        for s in range(segments):
            t = 2 * math.pi * s / segments
            verts.append((rr * math.cos(t), rr * math.sin(t), z))
    nrows = len(rows)
    for r in range(nrows - 1):
        for s in range(segments):
            a = r * segments + s
            b = r * segments + (s + 1) % segments
            c = (r + 1) * segments + (s + 1) % segments
            d = (r + 1) * segments + s
            faces.append((a, b, c, d))
    mesh.from_pydata(verts, [], faces)
    mesh.update()


# viewport clipping -----------------------------------------------------------------------------

def _round_sig(x, sig=2):
    if x <= 0:
        return x
    mag = 10 ** math.floor(math.log10(x))
    return round(x / mag * 10 ** (sig - 1)) * mag / 10 ** (sig - 1)


def scene_extent(context):
    """Diagonal of the box around every visible object (bridge geometry included)."""
    lo = Vector((math.inf,) * 3)
    hi = Vector((-math.inf,) * 3)
    for obj in context.view_layer.objects:
        if obj.type not in ("MESH", "CURVE", "EMPTY") or not obj.visible_get():
            continue
        if obj.type == "EMPTY":
            pts = [obj.matrix_world.translation]
        else:
            pts = [obj.matrix_world @ Vector(c) for c in obj.bound_box]
        for p in pts:
            lo = Vector((min(lo.x, p.x), min(lo.y, p.y), min(lo.z, p.z)))
            hi = Vector((max(hi.x, p.x), max(hi.y, p.y), max(hi.z, p.z)))
    if not math.isfinite(lo.x):
        return 0.0
    return (hi - lo).length


def fit_clipping(context):
    """Clip end = 10x the scene diagonal (room to orbit out), clip start = end / 1e5 (keeps depth precision),
    applied to every 3D viewport in every workspace and to every camera. Returns (start, end)."""
    diag = scene_extent(context)
    if diag <= 0:
        return None
    end = max(_round_sig(diag * 10.0), 100.0)
    start = min(max(_round_sig(end / 1e5), 0.01), 1.0)
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type != "VIEW_3D":
                continue
            for space in area.spaces:
                if space.type == "VIEW_3D":
                    space.clip_start = start
                    space.clip_end = end
    for cam in bpy.data.cameras:
        cam.clip_start = start
        cam.clip_end = end
    return start, end


class SBOX_OT_fit_clipping(bpy.types.Operator):
    """Set every 3D viewport's and camera's clip start/end to suit the size of what's in the scene"""
    bl_idname = "sbox.fit_clipping"
    bl_label = "Fit Clipping to Scene"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        res = fit_clipping(context)
        if res is None:
            self.report({"WARNING"}, "Nothing visible to measure")
            return {"CANCELLED"}
        self.report({"INFO"}, "Clip start %.3g, end %.4g" % res)
        return {"FINISHED"}


# auto-pull timer -------------------------------------------------------------------------------

_timer_running = False


def _auto_pull_tick():
    global _timer_running
    scene = bpy.context.scene
    settings = getattr(scene, "sbox_bridge", None)
    if settings is None or not settings.auto_pull:
        _timer_running = False
        return None
    try:
        path = _manifest_path(settings)
        if os.path.isfile(path):
            mtime = os.path.getmtime(path)
            if mtime > settings.last_manifest_mtime and time.time() - mtime > 0.5:
                ok, msg = pull(bpy.context)
                print("[sbox_bridge]", msg)
    except Exception as ex:
        print("[sbox_bridge] auto-pull failed:", ex)
    return 1.0


def _auto_pull_toggle(enabled):
    global _timer_running
    if enabled and not _timer_running:
        _timer_running = True
        bpy.app.timers.register(_auto_pull_tick, first_interval=1.0)


class SBOX_OT_pull(bpy.types.Operator):
    """Import the geometry the s&box editor last sent (Mimiclay > Blender > Send Scene Geometry)"""
    bl_idname = "sbox.pull"
    bl_label = "Pull from s&box"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        ok, msg = pull(context)
        self.report({"INFO"} if ok else {"WARNING"}, msg)
        return {"FINISHED"} if ok else {"CANCELLED"}


class SBOX_OT_clear(bpy.types.Operator):
    """Remove the imported s&box geometry"""
    bl_idname = "sbox.clear"
    bl_label = "Clear"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        coll = bpy.data.collections.get(BRIDGE_COLLECTION)
        if coll is not None:
            _clear_collection(coll)
        return {"FINISHED"}


# ----------------------------------------------------------------------------------------------
# push: Blender -> s&box (FBX for FbxBlockoutImporter)
# ----------------------------------------------------------------------------------------------

def is_bridge_object(obj):
    return bool(obj.get(BRIDGE_PROP, False))


def exportable_objects(context, selected_only=False):
    objs = context.selected_objects if selected_only else context.view_layer.objects
    return [o for o in objs if o.type == "MESH" and not is_bridge_object(o) and o.visible_get()]


class SBOX_OT_push(bpy.types.Operator):
    """Export every visible, non-bridge mesh to the FBX the s&box FbxBlockoutImporter reads.
Settings match the project's existing exports: units untouched (UnitScaleFactor 1), object scale kept, -Z forward / Y up, modifiers applied"""
    bl_idname = "sbox.push"
    bl_label = "Push FBX to s&box"

    selected_only: bpy.props.BoolProperty(name="Selected only", default=False)

    def execute(self, context):
        settings = context.scene.sbox_bridge
        path = bpy.path.abspath(settings.fbx_path)
        objs = exportable_objects(context, self.selected_only)
        if not objs:
            self.report({"WARNING"}, "No exportable meshes (bridge geometry is excluded)")
            return {"CANCELLED"}
        prev = [o for o in context.selected_objects]
        prev_active = context.view_layer.objects.active
        bpy.ops.object.select_all(action="DESELECT")
        for o in objs:
            o.select_set(True)
        context.view_layer.objects.active = objs[0]
        try:
            # Blender's exporter always multiplies the root transforms by a unit factor of 100 under
            # FBX_SCALE_NONE (a fixed 100 when apply_unit_scale is off), so global_scale 0.01 cancels it:
            # UnitScaleFactor 1, vertices in raw Blender units, object scale untouched — the layout the
            # project's existing Kitchen.fbx has and FbxBlockoutImporter's ImportScale is tuned for.
            bpy.ops.export_scene.fbx(
                filepath=path, use_selection=True, object_types={"MESH"},
                global_scale=0.01, apply_unit_scale=False, apply_scale_options="FBX_SCALE_NONE",
                use_space_transform=True, bake_space_transform=False,
                axis_forward="-Z", axis_up="Y",
                use_mesh_modifiers=True, use_mesh_modifiers_render=False,
                mesh_smooth_type="OFF", use_tspace=False, use_triangles=False,
                add_leaf_bones=False, bake_anim=False, use_custom_props=False, path_mode="AUTO")
        finally:
            bpy.ops.object.select_all(action="DESELECT")
            for o in prev:
                try:
                    o.select_set(True)
                except Exception:
                    pass
            context.view_layer.objects.active = prev_active
        self.report({"INFO"}, "Pushed %d meshes to %s" % (len(objs), path))
        return {"FINISHED"}


# ----------------------------------------------------------------------------------------------
# rescale: mesh + locations + every length-valued modifier setting
# ----------------------------------------------------------------------------------------------

# modifier float properties that are lengths (scaled by the per-object factor) ------------------
_LENGTH_PROPS = {
    "BEVEL": ["width"],                       # unless offset_type == 'PERCENT'
    "SOLIDIFY": ["thickness"],                # offset / thickness_clamp are relative
    "ARRAY": ["merge_threshold", "constant_offset_displace"],  # relative offset untouched
    "MIRROR": ["merge_threshold", "bisect_threshold"],
    "WELD": ["merge_threshold"],
    "SCREW": ["screw_offset", "merge_threshold"],
    "DISPLACE": ["strength"],
    "SHRINKWRAP": ["offset", "project_limit"],
    "WIREFRAME": ["thickness"],
    "REMESH": ["voxel_size"],
    "CAST": ["radius"],
    "BOOLEAN": ["double_threshold"],
    "EDGE_SPLIT": [], "TRIANGULATE": [], "WEIGHTED_NORMAL": [], "SUBSURF": [], "DECIMATE": [],
    "SMOOTH": [], "SMOOTH_BY_ANGLE": [], "DATA_TRANSFER": [], "NORMAL_EDIT": [],
}


def _scale_mesh_data(mesh, m):
    """Scale vertex positions (and shape keys) by matrix m (may be non-uniform when folding object scale)."""
    for v in mesh.vertices:
        v.co = m @ v.co
    if mesh.shape_keys:
        for kb in mesh.shape_keys.key_blocks:
            for pt in kb.data:
                pt.co = m @ pt.co
    if mesh.skin_vertices:
        s = abs(m.to_scale().x)
        for layer in mesh.skin_vertices:
            for sv in layer.data:
                sv.radius = (sv.radius[0] * s, sv.radius[1] * s)
    mesh.update()


def _scale_curve_data(curve, m, s):
    for spline in curve.splines:
        for p in spline.bezier_points:
            p.co = m @ p.co
            p.handle_left = m @ p.handle_left
            p.handle_right = m @ p.handle_right
        for p in spline.points:
            x, y, z, w = p.co
            v = m @ Vector((x, y, z))
            p.co = (v.x, v.y, v.z, w)
    curve.bevel_depth *= s
    curve.extrude *= s
    curve.offset *= s


def _scale_modifiers(obj, s, notes):
    for mod in obj.modifiers:
        if mod.type == "NODES":
            _scale_geometry_nodes(obj, mod, s, notes)
            continue
        props = _LENGTH_PROPS.get(mod.type)
        if props is None:
            notes.append("%s: modifier '%s' (%s) not handled — check its values by hand" % (obj.name, mod.name, mod.type))
            continue
        for prop in props:
            if mod.type == "BEVEL" and prop == "width" and mod.offset_type == "PERCENT":
                continue
            if mod.type == "ARRAY" and prop == "constant_offset_displace":
                if mod.use_constant_offset:
                    mod.constant_offset_displace = [c * s for c in mod.constant_offset_displace]
                continue
            if not hasattr(mod, prop):
                continue
            val = getattr(mod, prop)
            try:
                setattr(mod, prop, val * s)
            except Exception as ex:
                notes.append("%s: %s.%s could not be scaled (%s)" % (obj.name, mod.name, prop, ex))


def _scale_geometry_nodes(obj, mod, s, notes):
    ng = mod.node_group
    if ng is None:
        return
    for item in ng.interface.items_tree:
        if getattr(item, "item_type", "") != "SOCKET" or item.in_out != "INPUT":
            continue
        ident = item.identifier
        if ident not in mod.keys():
            continue
        subtype = getattr(item, "subtype", "NONE")
        if item.socket_type == "NodeSocketFloat" and subtype == "DISTANCE":
            mod[ident] = float(mod[ident]) * s
        elif item.socket_type == "NodeSocketVector" and subtype in ("TRANSLATION", "XYZ"):
            mod[ident] = [float(c) * s for c in mod[ident]]
        elif item.socket_type == "NodeSocketFloat" and subtype == "NONE":
            notes.append("%s: geometry nodes '%s' input '%s' is a plain float (left alone — scale it if it is a length)" % (
                obj.name, ng.name, item.name))


def _evaluated_dims(obj, depsgraph):
    ev = obj.evaluated_get(depsgraph)
    if ev.type != "MESH" or ev.data is None:
        return None
    mesh = ev.to_mesh() if hasattr(ev, "to_mesh") else ev.data
    try:
        if len(mesh.vertices) == 0:
            return None
        mw = ev.matrix_world
        xs = [(mw @ v.co) for v in mesh.vertices]
        lo = Vector((min(p.x for p in xs), min(p.y for p in xs), min(p.z for p in xs)))
        hi = Vector((max(p.x for p in xs), max(p.y for p in xs), max(p.z for p in xs)))
        return hi - lo, lo
    finally:
        if hasattr(ev, "to_mesh_clear"):
            ev.to_mesh_clear()


def rescale(context, objects, factor, apply_object_scale=True, scale_locations=True):
    """Scale `objects` by `factor` so everything LOOKS the same but bigger/smaller.

    Returns (notes, checks): notes = things to look at by hand; checks = per-object
    (name, expected_ratio, measured_ratio_xyz) from the evaluated bounding boxes."""
    notes = []
    objects = [o for o in objects if not is_bridge_object(o)]
    if not objects:
        return ["nothing to rescale"], []
    depsgraph = context.evaluated_depsgraph_get()
    before = {o.name: _evaluated_dims(o, depsgraph) for o in objects}

    done_data = set()
    obj_set = set(objects)
    for obj in objects:
        osc = obj.scale.copy()
        uniform = abs(osc.x - osc.y) < 1e-6 and abs(osc.x - osc.z) < 1e-6
        if apply_object_scale and not uniform and obj.modifiers:
            notes.append("%s: NON-UNIFORM object scale %s with modifiers — folded into the mesh, but the "
                         "bevel/solidify widths were scaled by the mean axis; check it" % (obj.name, tuple(round(c, 4) for c in osc)))
        fold = apply_object_scale
        if fold and obj.data is not None and obj.data.users > 1:
            others = [o for o in bpy.data.objects if o.data == obj.data and o is not obj]
            if any(o not in obj_set or (o.scale - osc).length > 1e-6 for o in others):
                notes.append("%s: shares mesh '%s' with objects of a different scale/selection — object scale NOT folded" % (obj.name, obj.data.name))
                fold = False

        # per-object modifier factor: the data factor plus the object scale we fold in
        data_scale = Vector((factor, factor, factor))
        mod_factor = factor
        if fold:
            data_scale = Vector((factor * osc.x, factor * osc.y, factor * osc.z))
            mod_factor = factor * ((osc.x + osc.y + osc.z) / 3.0)
        m = Matrix.Diagonal(data_scale).to_4x4()

        if obj.data is not None and obj.data.name not in done_data:
            if obj.type == "MESH":
                _scale_mesh_data(obj.data, m)
            elif obj.type == "CURVE":
                _scale_curve_data(obj.data, m, mod_factor)
            else:
                notes.append("%s: %s data not scaled (only mesh/curve data handled)" % (obj.name, obj.type))
            done_data.add(obj.data.name)
        if obj.type == "EMPTY":
            obj.empty_display_size *= factor

        _scale_modifiers(obj, mod_factor, notes)

        if fold:
            obj.scale = (1.0, 1.0, 1.0)
        if obj.constraints:
            notes.append("%s: has constraints (%s) — distances inside them are not scaled" % (obj.name, ", ".join(c.type for c in obj.constraints)))

    if scale_locations:
        for obj in objects:
            obj.location = obj.location * factor
            obj.delta_location = obj.delta_location * factor
            if obj.parent is not None:
                mpi = obj.matrix_parent_inverse.copy()
                mpi.translation = mpi.translation * factor
                obj.matrix_parent_inverse = mpi

    context.view_layer.update()
    depsgraph = context.evaluated_depsgraph_get()
    checks = []
    for obj in objects:
        b = before.get(obj.name)
        a = _evaluated_dims(obj, depsgraph)
        if b is None or a is None:
            continue
        ratios = tuple((a[0][i] / b[0][i]) if b[0][i] > 1e-6 else float("nan") for i in range(3))
        checks.append((obj.name, factor, ratios))
        bad = [r for r in ratios if not math.isnan(r) and abs(r - factor) > factor * 0.005]
        if bad:
            notes.append("%s: evaluated size changed by %s, expected %.5f — a modifier value probably needs a look" % (
                obj.name, tuple(round(r, 5) for r in ratios), factor))
    return notes, checks


class SBOX_OT_rescale(bpy.types.Operator):
    """Scale objects AND every length in their modifiers (bevel width, solidify thickness, array offsets,
merge distances...) so they look the same, just bigger or smaller. Verifies each object's evaluated size afterwards"""
    bl_idname = "sbox.rescale"
    bl_label = "Rescale Objects"
    bl_options = {"REGISTER", "UNDO"}

    factor: bpy.props.FloatProperty(name="Factor", default=1.0, precision=5, min=0.0001)
    apply_object_scale: bpy.props.BoolProperty(name="Fold object scale into mesh", default=True)
    scale_locations: bpy.props.BoolProperty(name="Scale locations", default=True)
    selected_only: bpy.props.BoolProperty(name="Selected only", default=True)

    def invoke(self, context, event):
        s = context.scene.sbox_bridge
        self.factor = s.rescale_factor
        self.apply_object_scale = s.rescale_apply_object_scale
        self.scale_locations = s.rescale_locations
        self.selected_only = s.rescale_selected_only
        return self.execute(context)

    def execute(self, context):
        if context.mode != "OBJECT":
            bpy.ops.object.mode_set(mode="OBJECT")
        objs = list(context.selected_objects) if self.selected_only else [o for o in context.view_layer.objects]
        notes, checks = rescale(context, objs, self.factor, self.apply_object_scale, self.scale_locations)
        for n in notes:
            print("[sbox_bridge] NOTE:", n)
            self.report({"WARNING"}, n)
        self.report({"INFO"}, "Rescaled %d objects by %.5f (%d notes — see console)" % (len(checks), self.factor, len(notes)))
        return {"FINISHED"}


# ----------------------------------------------------------------------------------------------
# UI
# ----------------------------------------------------------------------------------------------

class SBOX_PT_bridge(bpy.types.Panel):
    bl_label = "s&box Bridge"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "s&box"

    def draw(self, context):
        s = context.scene.sbox_bridge
        layout = self.layout

        box = layout.box()
        box.label(text="s&box → Blender", icon="IMPORT")
        box.prop(s, "bridge_dir", text="")
        row = box.row(align=True)
        row.operator("sbox.pull", icon="FILE_REFRESH")
        row.operator("sbox.clear", icon="TRASH", text="")
        box.prop(s, "auto_pull")
        box.prop(s, "use_manifest_units")
        sub = box.row()
        sub.enabled = not s.use_manifest_units
        sub.prop(s, "units_per_bu")
        box.prop(s, "lock_selection")
        box.prop(s, "spawn_markers")
        row = box.row(align=True)
        row.prop(s, "fit_clipping")
        row.operator("sbox.fit_clipping", text="", icon="VIEW_CAMERA")
        row = box.row(align=True)
        row.label(text="Backface cull (Solid)")
        row.operator("sbox.backface_culling", text="On").enabled = True
        row.operator("sbox.backface_culling", text="Off").enabled = False
        if s.last_pull:
            box.label(text=s.last_pull, icon="INFO")

        box = layout.box()
        box.label(text="Blender → s&box", icon="EXPORT")
        box.prop(s, "fbx_path", text="")
        box.operator("sbox.push", icon="EXPORT")
        box.operator("sbox.push", text="Push Selected Only").selected_only = True

        box = layout.box()
        box.label(text="Rescale (modifier-aware)", icon="FULLSCREEN_ENTER")
        box.prop(s, "rescale_factor")
        box.prop(s, "rescale_apply_object_scale")
        box.prop(s, "rescale_locations")
        box.prop(s, "rescale_selected_only")
        box.operator("sbox.rescale", icon="CON_SIZELIKE")
        row = box.row(align=True)
        row.operator("sbox.rescale_preset", text="→ 1 BU = 1 unit").preset = "NORMALISE"
        row.operator("sbox.rescale_preset", text="÷ 2").preset = "HALF"
        row.operator("sbox.rescale_preset", text="× 2").preset = "DOUBLE"


class SBOX_OT_rescale_preset(bpy.types.Operator):
    """Fill the factor: 'normalise' = current unit factor so 1 Blender unit becomes 1 s&box unit (then set the importer's ImportScale to 2.54)"""
    bl_idname = "sbox.rescale_preset"
    bl_label = "Preset"
    preset: bpy.props.StringProperty(default="NORMALISE")

    def execute(self, context):
        s = context.scene.sbox_bridge
        if self.preset == "NORMALISE":
            s.rescale_factor = s.units_per_bu
        elif self.preset == "HALF":
            s.rescale_factor = 0.5
        elif self.preset == "DOUBLE":
            s.rescale_factor = 2.0
        return {"FINISHED"}


classes = (
    SboxBridgeSettings,
    SBOX_OT_pull,
    SBOX_OT_clear,
    SBOX_OT_push,
    SBOX_OT_rescale,
    SBOX_OT_rescale_preset,
    SBOX_OT_fit_clipping,
    SBOX_OT_backface_culling,
    SBOX_PT_bridge,
)


def register():
    for c in classes:
        bpy.utils.register_class(c)
    bpy.types.Scene.sbox_bridge = bpy.props.PointerProperty(type=SboxBridgeSettings)


def unregister():
    global _timer_running
    _timer_running = False
    del bpy.types.Scene.sbox_bridge
    for c in reversed(classes):
        bpy.utils.unregister_class(c)


if __name__ == "__main__":
    register()
