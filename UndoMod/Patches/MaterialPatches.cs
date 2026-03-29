using HarmonyLib;
using Il2CppCraftEditor;
using Il2Cpp;

namespace UndoMod
{
    // material and paint stuff

    [HarmonyPatch(typeof(PartMaterials), nameof(PartMaterials.SetMaterial))]
    static class Patch_SetMat { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(CEManager), nameof(CEManager.PaintAll), typeof(PartMaterials))]
    static class Patch_PaintAll { static void Postfix() => SnapHelper.Do(); }
}
