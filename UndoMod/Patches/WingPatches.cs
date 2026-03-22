using HarmonyLib;
using Il2CppCraftEditor;
using Il2Cpp;

namespace UndoMod
{
    // wing property edits

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetSpan))]
    static class Patch_WSpan { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetRootChord))]
    static class Patch_WRootChord { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetTipChord))]
    static class Patch_WTipChord { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetTipOffset))]
    static class Patch_WTipOff { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetRootThickness))]
    static class Patch_WRootThk { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetTipThickness))]
    static class Patch_WTipThk { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetRootTwist))]
    static class Patch_WRootTwist { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetTipTwist))]
    static class Patch_WTipTwist { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetRootReflex))]
    static class Patch_WRootRef { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetTipReflex))]
    static class Patch_WTipRef { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetStiffness))]
    static class Patch_WStiff { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetStrength))]
    static class Patch_WStrength { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetSoftJoint))]
    static class Patch_WSoftJoint { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetFuelTank))]
    static class Patch_WFuel { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetPushrodPosition))]
    static class Patch_WPushPos { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.SetPushrodSide))]
    static class Patch_WPushSide { static void Postfix() => SnapHelper.Do(); }

    // wing structural edits

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.AddWing))]
    static class Patch_WAdd { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.Split))]
    static class Patch_WSplit { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.ClearWing))]
    static class Patch_WClear { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.RemoveTrailingEdge))]
    static class Patch_WRemTE { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.RemoveLeadingEdge))]
    static class Patch_WRemLE { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.AddStandardLeadingEdge))]
    static class Patch_WAddLE { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.AddControlSurface))]
    static class Patch_WAddCS { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.AddFixedTrailingEdge))]
    static class Patch_WAddFTE { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.AddFowlerFlap))]
    static class Patch_WFowler { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(ProcWing2), nameof(ProcWing2.AddSplitFlap))]
    static class Patch_WSplitFlap { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(CEManager), nameof(CEManager.QuitWingEditor))]
    static class Patch_QuitWing { static void Postfix() => SnapHelper.Do(); }
}
