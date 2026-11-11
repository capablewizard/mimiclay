"""Edge Bevel: a Bevel modifier that bevels ONLY the selected edges.

HardOps' "bevel selected" limits its Bevel modifier by a vertex group, so every edge
whose two verts are both in the group gets bevelled too, e.g. the short edge
between two selected parallel edges on a quad. This add-on stores the selection
as a per-edge float attribute ("EdgeBevel", "EdgeBevel.001", ...) and points a
Bevel modifier (Limit Method = Weight, Edge Weight = that attribute) at it, so the
result matches the interactive Bevel tool, but stays non-destructive.

Edit Mode -> select edges -> Q (HardOps menu) or Edge menu -> "Edge Bevel".
  Mouse: width   Wheel / +-: segments   Ctrl+Wheel: profile   Shift: precise
  LMB / Enter: confirm   RMB / Esc: cancel
With an Edge Bevel modifier active, the menu also offers Add / Remove Selected
Edges, which edit that modifier's attribute.

"Keep N-gon Ends" (on by default) first dissolves flat split edges that meet the ends of
the selection (e.g. a centre seam down an end cap), so the bevel ends in an n-gon rather
than a triangle fan. Only coplanar splits are removed, so the shape is unchanged.
"""

bl_info = {
    "name": "Edge Bevel (selected edges only)",
    "author": "Mimiclay",
    "version": (1, 0, 0),
    "blender": (4, 3, 0),
    "location": "Edit Mode > Q (HardOps) / Edge menu",
    "description": "Bevel modifier that bevels only the selected edges, via a per-edge attribute",
    "category": "Mesh",
}

import bpy
import bmesh
from bpy.props import BoolProperty, EnumProperty, FloatProperty, IntProperty

ATTR_BASE = "EdgeBevel"
MOD_BASE = "Edge Bevel"
# Modifiers that should stay below a new edge bevel (shading/cleanup passes).
_TAIL_TYPES = {"WEIGHTED_NORMAL", "TRIANGULATE", "DECIMATE"}


def _is_tail(mod):
    if mod.type in _TAIL_TYPES:
        return True
    return mod.type == "NODES" and mod.node_group is not None and "Smooth by Angle" in mod.node_group.name


def _unique_attr_name(mesh, bm):
    taken = set(mesh.attributes.keys()) | set(bm.edges.layers.float.keys())
    if ATTR_BASE not in taken:
        return ATTR_BASE
    i = 1
    while f"{ATTR_BASE}.{i:03d}" in taken:
        i += 1
    return f"{ATTR_BASE}.{i:03d}"


def dissolve_end_splits(bm, bevel_edges, max_angle=0.0175):
    """Dissolve unbevelled edges that touch the ends of `bevel_edges` and only split a flat face.

    A bevelled edge that stops at a vertex where a flat face is divided in two (a mirror seam
    or loop cut running down an end cap) makes the Bevel fill that end with a triangle fan.
    With the split gone the end becomes one n-gon. The shape doesn't change: only edges whose
    two faces are coplanar (within `max_angle`) are removed. Returns how many were dissolved."""
    bevel = set(bevel_edges)
    ends = {v for e in bevel for v in e.verts}
    victims = []
    for v in ends:
        for e in v.link_edges:
            if e in bevel or len(e.link_faces) != 2:
                continue
            f0, f1 = e.link_faces
            if f0.normal.angle(f1.normal, 0.0) <= max_angle:
                victims.append(e)
    victims = list(set(victims))
    if victims:
        bmesh.ops.dissolve_edges(bm, edges=victims, use_verts=False, use_face_split=False)
    return len(victims)


def edge_bevel_attr(obj, mod):
    """The attribute name if `mod` is an edge-attribute bevel on `obj`, else None."""
    if mod is None or mod.type != "BEVEL" or mod.limit_method != "WEIGHT":
        return None
    name = mod.edge_weight
    if not name or name == "bevel_weight_edge":
        return None
    if obj.mode == "EDIT":
        bm = bmesh.from_edit_mesh(obj.data)
        return name if bm.edges.layers.float.get(name) is not None else None
    a = obj.data.attributes.get(name)
    return name if a is not None and a.domain == "EDGE" else None


class MESH_OT_edge_bevel_modifier(bpy.types.Operator):
    """Bevel only the selected edges with a non-destructive Bevel modifier (per-edge attribute)"""
    bl_idname = "mesh.edge_bevel_modifier"
    bl_label = "Edge Bevel"
    bl_options = {"REGISTER", "UNDO", "GRAB_CURSOR", "BLOCKING"}

    mode: EnumProperty(
        items=[
            ("NEW", "New", "Add a new Edge Bevel modifier for the selected edges"),
            ("ADD", "Add Edges", "Add the selected edges to the active Edge Bevel modifier"),
            ("REMOVE", "Remove Edges", "Remove the selected edges from the active Edge Bevel modifier"),
        ],
        default="NEW",
    )
    width: FloatProperty(name="Width", default=4.0, min=0.0, soft_max=100.0)
    segments: IntProperty(name="Segments", default=3, min=1, max=64)
    profile: FloatProperty(name="Profile", default=0.7, min=0.0, max=1.0)
    keep_ngons: BoolProperty(
        name="Keep N-gon Ends",
        description="Dissolve flat split edges at the ends of the selected edges so the bevel "
                    "ends in an n-gon instead of a triangle fan (removes only coplanar splits)",
        default=True,
    )

    @classmethod
    def poll(cls, context):
        o = context.active_object
        return o is not None and o.type == "MESH" and context.mode == "EDIT_MESH"

    # -- attribute helpers ---------------------------------------------------
    def _write(self, obj, name, value, create=False):
        """Set `value` on the selected edges; returns the previous values."""
        bm = bmesh.from_edit_mesh(obj.data)
        if value > 0.0 and self.keep_ngons:
            # Before snapshotting: dissolving changes the edge list. (Not undone by RMB; harmless.)
            bm.normal_update()
            dissolve_end_splits(bm, [e for e in bm.edges if e.select])
        layer = bm.edges.layers.float.get(name)
        if layer is None:
            if not create:
                return None
            layer = bm.edges.layers.float.new(name)
        old = [e[layer] for e in bm.edges]
        for e in bm.edges:
            if e.select:
                e[layer] = value
        bmesh.update_edit_mesh(obj.data, loop_triangles=False, destructive=False)
        return old

    def _restore(self, obj, name, old):
        bm = bmesh.from_edit_mesh(obj.data)
        layer = bm.edges.layers.float.get(name)
        if layer is None:
            return
        if old is None:
            bm.edges.layers.float.remove(layer)
        else:
            bm.edges.ensure_lookup_table()
            for e, v in zip(bm.edges, old):
                e[layer] = v
        bmesh.update_edit_mesh(obj.data, loop_triangles=False, destructive=False)

    # -- run -----------------------------------------------------------------
    def invoke(self, context, event):
        obj = context.active_object
        obj.update_from_editmode()
        bm = bmesh.from_edit_mesh(obj.data)
        if not any(e.select for e in bm.edges):
            self.report({"WARNING"}, "Select some edges first")
            return {"CANCELLED"}

        if self.mode != "NEW":
            mod = obj.modifiers.active
            name = edge_bevel_attr(obj, mod)
            if name is None:
                self.report({"WARNING"}, "Active modifier is not an Edge Bevel")
                return {"CANCELLED"}
            self._write(obj, name, 1.0 if self.mode == "ADD" else 0.0)
            return {"FINISHED"}

        self.obj = obj
        self.attr = _unique_attr_name(obj.data, bm)
        self._write(obj, self.attr, 1.0, create=True)
        self.mod = self._add_modifier(obj)

        self.start_x = event.mouse_x
        self.start_width = self.width
        # Pixel -> width scale from the object's size so it feels the same on big and small props.
        self.px_scale = max(max(obj.dimensions) / 600.0, 1e-4)
        context.window_manager.modal_handler_add(self)
        self._header(context)
        return {"RUNNING_MODAL"}

    def execute(self, context):
        # Redo panel (F9) / non-interactive call: rebuild with the panel values.
        obj = context.active_object
        if self.mode != "NEW":
            mod = obj.modifiers.active
            name = edge_bevel_attr(obj, mod)
            if name is None:
                return {"CANCELLED"}
            self._write(obj, name, 1.0 if self.mode == "ADD" else 0.0)
            return {"FINISHED"}
        bm = bmesh.from_edit_mesh(obj.data)
        if not any(e.select for e in bm.edges):
            return {"CANCELLED"}
        self.attr = _unique_attr_name(obj.data, bm)
        self._write(obj, self.attr, 1.0, create=True)
        self.mod = self._add_modifier(obj)
        return {"FINISHED"}

    def _add_modifier(self, obj):
        mod = obj.modifiers.new(MOD_BASE, "BEVEL")
        mod.affect = "EDGES"
        mod.offset_type = "OFFSET"
        mod.limit_method = "WEIGHT"
        mod.edge_weight = self.attr
        mod.width = self.width
        mod.segments = self.segments
        mod.profile = self.profile
        mod.use_clamp_overlap = True
        mod.harden_normals = False
        mod.show_in_editmode = True
        mod.show_on_cage = False
        # Sit above the shading/cleanup tail (Weighted Normal, Smooth by Angle, Triangulate).
        mods = list(obj.modifiers)
        target = len(mods) - 1
        for i, m in enumerate(mods[:-1]):
            if _is_tail(m):
                target = i
                break
        if target != len(mods) - 1:
            with bpy.context.temp_override(object=obj):
                bpy.ops.object.modifier_move_to_index(modifier=mod.name, index=target)
        obj.modifiers.active = mod
        return mod

    def _header(self, context):
        context.area.header_text_set(
            f"Edge Bevel  Width: {self.width:.3f}   Segments: {self.segments}   Profile: {self.profile:.2f}"
            "    [Mouse] width  [Wheel] segments  [Ctrl+Wheel] profile  [Shift] precise  [LMB] confirm  [RMB] cancel"
        )

    def _apply(self):
        self.mod.width = self.width
        self.mod.segments = self.segments
        self.mod.profile = self.profile

    def modal(self, context, event):
        context.area.tag_redraw()
        if event.type == "MOUSEMOVE":
            k = 0.1 if event.shift else 1.0
            self.width = max(0.0, self.start_width + (event.mouse_x - self.start_x) * self.px_scale * k)
        elif event.type in {"WHEELUPMOUSE", "NUMPAD_PLUS", "EQUAL"} and event.value == "PRESS":
            if event.ctrl:
                self.profile = min(1.0, self.profile + 0.05)
            else:
                self.segments = min(64, self.segments + 1)
        elif event.type in {"WHEELDOWNMOUSE", "NUMPAD_MINUS", "MINUS"} and event.value == "PRESS":
            if event.ctrl:
                self.profile = max(0.0, self.profile - 0.05)
            else:
                self.segments = max(1, self.segments - 1)
        elif event.type in {"LEFTMOUSE", "RET", "NUMPAD_ENTER", "SPACE"} and event.value == "PRESS":
            self._apply()
            context.area.header_text_set(None)
            return {"FINISHED"}
        elif event.type in {"RIGHTMOUSE", "ESC"} and event.value == "PRESS":
            self.obj.modifiers.remove(self.mod)
            self._restore(self.obj, self.attr, None)
            context.area.header_text_set(None)
            return {"CANCELLED"}
        else:
            return {"RUNNING_MODAL"}
        self._apply()
        self._header(context)
        return {"RUNNING_MODAL"}


def menu_draw(self, context):
    if context.mode != "EDIT_MESH":
        return
    layout = self.layout
    layout.separator()
    layout.operator_context = "INVOKE_DEFAULT"
    layout.operator(MESH_OT_edge_bevel_modifier.bl_idname, text="Edge Bevel (selected only)", icon="MOD_BEVEL").mode = "NEW"
    obj = context.active_object
    if obj is not None and edge_bevel_attr(obj, obj.modifiers.active):
        layout.operator(MESH_OT_edge_bevel_modifier.bl_idname, text="Edge Bevel: Add Selected", icon="ADD").mode = "ADD"
        layout.operator(MESH_OT_edge_bevel_modifier.bl_idname, text="Edge Bevel: Remove Selected", icon="REMOVE").mode = "REMOVE"


_MENUS = ("VIEW3D_MT_edit_mesh_edges", "HOPS_MT_MainMenu")
_hooked = set()


def _hook_menus():
    """HardOps may register after us, so retry until its Q menu exists."""
    for name in _MENUS:
        if name in _hooked:
            continue
        menu = getattr(bpy.types, name, None)
        if menu is not None:
            menu.append(menu_draw)
            _hooked.add(name)
    return None if len(_hooked) == len(_MENUS) else 2.0


def register():
    bpy.utils.register_class(MESH_OT_edge_bevel_modifier)
    if _hook_menus() is not None:
        bpy.app.timers.register(_hook_menus, first_interval=2.0, persistent=True)


def unregister():
    if bpy.app.timers.is_registered(_hook_menus):
        bpy.app.timers.unregister(_hook_menus)
    for name in list(_hooked):
        menu = getattr(bpy.types, name, None)
        if menu is not None:
            menu.remove(menu_draw)
    _hooked.clear()
    bpy.utils.unregister_class(MESH_OT_edge_bevel_modifier)
