bl_info = {
    "name": "Batch Icon Render",
    "author": "Haydn",
    "version": (1, 0, 0),
    "blender": (3, 0, 0),
    "location": "View3D > N-Panel > Batch Icons",
    "description": "Render each mesh object in the scene individually to a chosen folder.",
    "category": "Render",
}

import os
import bpy


# --------------------------------------------------------------------------- #
# Properties
# --------------------------------------------------------------------------- #
class BatchIconSettings(bpy.types.PropertyGroup):
    output_dir: bpy.props.StringProperty(
        name="Output Folder",
        description="Folder to save the individual renders into",
        subtype="DIR_PATH",
        default="//icons/",
    )
    prefix: bpy.props.StringProperty(
        name="Prefix",
        description="Text added before each object name",
        default="",
    )
    suffix: bpy.props.StringProperty(
        name="Suffix",
        description="Text added after each object name",
        default="",
    )
    selected_only: bpy.props.BoolProperty(
        name="Selected Only",
        description="Only render the currently selected objects "
                    "(otherwise every mesh in the scene)",
        default=False,
    )


# --------------------------------------------------------------------------- #
# Operator
# --------------------------------------------------------------------------- #
class RENDER_OT_batch_icons(bpy.types.Operator):
    """Render each geometry object individually to the output folder"""
    bl_idname = "render.batch_icons"
    bl_label = "Render Each Object"
    bl_options = {"REGISTER"}

    def execute(self, context):
        scene = context.scene
        settings = scene.batch_icon_settings

        out_dir = bpy.path.abspath(settings.output_dir)
        if not out_dir:
            self.report({"ERROR"}, "No output folder set.")
            return {"CANCELLED"}

        try:
            os.makedirs(out_dir, exist_ok=True)
        except OSError as err:
            self.report({"ERROR"}, f"Could not create output folder: {err}")
            return {"CANCELLED"}

        # Collect the geometry objects we want to render.
        candidates = (
            context.selected_objects if settings.selected_only else scene.objects
        )
        geo_objects = [obj for obj in candidates if obj.type == "MESH"]

        if not geo_objects:
            self.report({"ERROR"}, "No mesh objects found to render.")
            return {"CANCELLED"}

        # Remember the original render-visibility of every object so we can
        # restore the scene exactly as it was afterwards.
        original_hide = {obj: obj.hide_render for obj in scene.objects}

        # Hide every mesh; lights / cameras / world stay as they are so the
        # geometry is still lit. We only toggle one mesh visible at a time.
        for obj in scene.objects:
            if obj.type == "MESH":
                obj.hide_render = True

        original_filepath = scene.render.filepath
        ext = scene.render.file_extension  # e.g. ".png", honours user's format

        rendered = 0
        try:
            for obj in geo_objects:
                obj.hide_render = False

                name = f"{settings.prefix}{obj.name}{settings.suffix}"
                # Strip any extension Blender would otherwise double up on.
                scene.render.filepath = os.path.join(out_dir, name)

                bpy.ops.render.render(write_still=True)
                rendered += 1

                obj.hide_render = True  # re-hide before the next one
        finally:
            # Restore original visibility and output path no matter what.
            for obj, hidden in original_hide.items():
                obj.hide_render = hidden
            scene.render.filepath = original_filepath

        self.report(
            {"INFO"},
            f"Rendered {rendered} object(s) to {out_dir} (ext: {ext})",
        )
        return {"FINISHED"}


# --------------------------------------------------------------------------- #
# UI Panel
# --------------------------------------------------------------------------- #
class VIEW3D_PT_batch_icons(bpy.types.Panel):
    bl_label = "Batch Icon Render"
    bl_idname = "VIEW3D_PT_batch_icons"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Batch Icons"

    def draw(self, context):
        layout = self.layout
        settings = context.scene.batch_icon_settings

        layout.prop(settings, "output_dir")
        layout.prop(settings, "prefix")
        layout.prop(settings, "suffix")
        layout.prop(settings, "selected_only")

        layout.separator()
        layout.operator(
            RENDER_OT_batch_icons.bl_idname,
            icon="RENDER_STILL",
        )


# --------------------------------------------------------------------------- #
# Registration
# --------------------------------------------------------------------------- #
classes = (
    BatchIconSettings,
    RENDER_OT_batch_icons,
    VIEW3D_PT_batch_icons,
)


def register():
    for cls in classes:
        bpy.utils.register_class(cls)
    bpy.types.Scene.batch_icon_settings = bpy.props.PointerProperty(
        type=BatchIconSettings
    )


def unregister():
    del bpy.types.Scene.batch_icon_settings
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)


if __name__ == "__main__":
    register()
