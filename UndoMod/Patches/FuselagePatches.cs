using HarmonyLib;
using Il2CppCraftEditor;
using Il2Cpp;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UndoMod
{
    // fuselage edits :3

    [HarmonyPatch(typeof(EditableFuselage), nameof(EditableFuselage.ApplyChanges))]
    static class Patch_FusApply { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(EditableFuselage), nameof(EditableFuselage.LoopCut))]
    static class Patch_FusLoopCut { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(EditableFuselage), nameof(EditableFuselage.AddSegment))]
    static class Patch_FusAddSeg { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(EditableFuselage), nameof(EditableFuselage.Split))]
    static class Patch_FusSplit { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(EditableFuselage), nameof(EditableFuselage.DeleteSegment))]
    static class Patch_FusDelSeg { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(EditableFuselage), nameof(EditableFuselage.SetSkinThickness))]
    static class Patch_FusSkin { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(EditableFuselage), nameof(EditableFuselage.ApplyMaterials))]
    static class Patch_FusMat { static void Postfix() => SnapHelper.Do(); }

    // cross section editor stuff

    [HarmonyPatch(typeof(CrossSectionEditor), nameof(CrossSectionEditor.Close))]
    static class Patch_CSEClose { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(CrossSectionEditor), nameof(CrossSectionEditor.Apply))]
    static class Patch_CSEApply { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(CEManager), nameof(CEManager.QuitCSE))]
    static class Patch_QuitCSE { static void Postfix() => SnapHelper.Do(); }

    // skip TexturePaintMode.Update on the exact frame ctrl+z/y is pressed
    // so the z key doesnt rotate the brush
    // only skip that one frame tho, holding ctrl is fine for painting
    [HarmonyPatch(typeof(TexturePaintMode), nameof(TexturePaintMode.Update))]
    static class Patch_PaintModeUpdate
    {
        static bool Prefix()
        {
            var kb = Keyboard.current;
            if (kb != null
                && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed)
                && (kb.zKey.wasPressedThisFrame || kb.yKey.wasPressedThisFrame))
                return false; // skip original Update this one frame
            return true;
        }
    }

    // camera override postfix — runs after the game computes camera
    // so our saved position actually sticks :)
    [HarmonyPatch(typeof(CECamera), nameof(CECamera.LateUpdate))]
    static class Patch_CECamLateUpdate
    {
        static void Postfix(CECamera __instance)
        {
            UndoMod.ApplyCameraOverride(__instance);
        }
    }
}
