using HarmonyLib;
using Il2CppCraftEditor;
using Il2Cpp;

namespace UndoMod
{
    // fuselage edits

    [HarmonyPatch(typeof(EditableFuselage), nameof(EditableFuselage.ApplyChanges))]
    static class Patch_FusApply { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(EditableFuselage), nameof(EditableFuselage.LoopCut))]
    static class Patch_FusLoopCut { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(EditableFuselage), nameof(EditableFuselage.AddSegment))]
    static class Patch_FusAddSeg { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(EditableFuselage), nameof(EditableFuselage.DeleteSegment))]
    static class Patch_FusDelSeg { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(EditableFuselage), nameof(EditableFuselage.Split))]
    static class Patch_FusSplit { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(EditableFuselage), nameof(EditableFuselage.SetSkinThickness))]
    static class Patch_FusSkin { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(EditableFuselage), nameof(EditableFuselage.ApplyMaterials))]
    static class Patch_FusMat { static void Postfix() => SnapHelper.Do(); }

    // cross section editor

    [HarmonyPatch(typeof(CrossSectionEditor), nameof(CrossSectionEditor.Close))]
    static class Patch_CSEClose { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(CrossSectionEditor), nameof(CrossSectionEditor.Apply))]
    static class Patch_CSEApply { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(CEManager), nameof(CEManager.QuitCSE))]
    static class Patch_QuitCSE { static void Postfix() => SnapHelper.Do(); }
}
