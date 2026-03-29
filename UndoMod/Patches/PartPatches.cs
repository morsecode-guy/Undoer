using HarmonyLib;
using Il2CppCraftEditor;
using Il2Cpp;

namespace UndoMod
{
    // part operations :3

    [HarmonyPatch(typeof(CEManager), nameof(CEManager.DeletePart))]
    static class Patch_Delete { static void Postfix() => SnapHelper.DoNow(); }

    [HarmonyPatch(typeof(CEManager), nameof(CEManager.DuplicatePart))]
    static class Patch_Duplicate { static void Postfix() => SnapHelper.DoNow(); }

    [HarmonyPatch(typeof(CEManager), nameof(CEManager.PlaceParts))]
    static class Patch_Place { static void Postfix() => SnapHelper.DoNow(); }

    [HarmonyPatch(typeof(CEManager), nameof(CEManager.RotatePart))]
    static class Patch_Rotate { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(Part), nameof(Part.OnPartScaled))]
    static class Patch_Scale { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(CEManager), nameof(CEManager.CreateMirror))]
    static class Patch_Mirror { static void Postfix() => SnapHelper.DoNow(); }

    [HarmonyPatch(typeof(CEManager), nameof(CEManager.ToggleSymmetry))]
    static class Patch_Symmetry { static void Postfix() => SnapHelper.DoNow(); }

    [HarmonyPatch(typeof(CEManager), nameof(CEManager.ImportMesh))]
    static class Patch_Import { static void Postfix() => SnapHelper.DoNow(); }

    // gizmo drag end

    [HarmonyPatch(typeof(TransformationGizmo), nameof(TransformationGizmo.StopDrag))]
    static class Patch_StopDrag { static void Postfix() => SnapHelper.Do(); }
}
