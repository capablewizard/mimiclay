bl_info = {
    "name": "Boolean Combine",
    "author": "Mimiclay",
    "version": (1, 0),
    "blender": (3, 0, 0),
    "location": "Object > Boolean Combine",
    "description": "Create a new object from the boolean result of the selected mesh objects",
    "category": "Object",
}

import bpy


class OBJECT_OT_boolean_combine(bpy.types.Operator):
    """Create a new object from the boolean result of the selected mesh objects"""

    bl_idname = "object.boolean_combine"
    bl_label = "Boolean Combine"
    bl_options = {"REGISTER", "UNDO"}

    operation: bpy.props.EnumProperty(
        name="Operation",
        items=[
            ("UNION", "Union", "Combine all selected objects into one volume"),
            ("DIFFERENCE", "Difference", "Subtract the other selected objects from the active object"),
            ("INTERSECT", "Intersect", "Keep only the volume shared by all selected objects"),
        ],
        default="UNION",
    )

    solver: bpy.props.EnumProperty(
        name="Solver",
        items=[
            ("EXACT", "Exact", "Slower but handles overlapping geometry correctly"),
            ("FAST", "Fast", "Faster but requires clean, non-coplanar input"),
        ],
        default="EXACT",
    )

    triangulate: bpy.props.BoolProperty(
        name="Triangulate",
        description="Triangulate the combined result",
        default=True,
    )

    keep_originals: bpy.props.BoolProperty(
        name="Keep Originals",
        description="Keep the source objects (they stay selected in the outliner but are deselected in the viewport)",
        default=True,
    )

    @classmethod
    def poll(cls, context):
        if context.mode != "OBJECT":
            return False
        meshes = [o for o in context.selected_objects if o.type == "MESH"]
        return len(meshes) >= 2

    def execute(self, context):
        meshes = [o for o in context.selected_objects if o.type == "MESH"]

        # DIFFERENCE is order-dependent: the active object is the one the
        # others get subtracted from. For UNION/INTERSECT order is irrelevant.
        active = context.active_object
        base = active if active in meshes else meshes[0]
        others = [o for o in meshes if o is not base]

        skipped = len(context.selected_objects) - len(meshes)
        if skipped:
            self.report({"INFO"}, f"Ignored {skipped} non-mesh object(s)")

        # Work on a copy so the originals are untouched.
        result = base.copy()
        result.data = base.data.copy()
        result.name = f"{base.name} Combined"
        context.collection.objects.link(result)

        for obj in others:
            mod = result.modifiers.new(name="BooleanCombine", type="BOOLEAN")
            mod.operation = self.operation
            mod.solver = self.solver
            mod.object = obj

        if self.triangulate:
            tri = result.modifiers.new(name="Triangulate", type="TRIANGULATE")
            tri.quad_method = "BEAUTY"
            tri.ngon_method = "BEAUTY"

        # Bake the modifier stack into a real mesh via the evaluated depsgraph
        # (avoids bpy.ops.modifier_apply context juggling).
        depsgraph = context.evaluated_depsgraph_get()
        eval_result = result.evaluated_get(depsgraph)
        baked = bpy.data.meshes.new_from_object(
            eval_result, preserve_all_data_layers=True, depsgraph=depsgraph
        )
        baked.name = result.name

        old_mesh = result.data
        result.data = baked
        result.modifiers.clear()
        if old_mesh.users == 0:
            bpy.data.meshes.remove(old_mesh)

        if not self.keep_originals:
            for obj in [base] + others:
                bpy.data.objects.remove(obj, do_unlink=True)

        for obj in context.selected_objects:
            obj.select_set(False)
        result.select_set(True)
        context.view_layer.objects.active = result

        self.report({"INFO"}, f"Created '{result.name}' from {len(meshes)} objects")
        return {"FINISHED"}


def menu_func(self, context):
    self.layout.operator(OBJECT_OT_boolean_combine.bl_idname)


def register():
    bpy.utils.register_class(OBJECT_OT_boolean_combine)
    bpy.types.VIEW3D_MT_object.append(menu_func)


def unregister():
    bpy.types.VIEW3D_MT_object.remove(menu_func)
    bpy.utils.unregister_class(OBJECT_OT_boolean_combine)


if __name__ == "__main__":
    register()
