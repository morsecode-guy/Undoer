using HarmonyLib;
using Il2CppCraftEditor;

namespace UndoMod
{
    // missile obj panel — ValueChanged fires on any slider/input change
    // all missile panels inherit MissileObjPanel so this one patch covers
    // warhead, joint, separator, procmissile, etc.

    [HarmonyPatch(typeof(MissileObjPanel), nameof(MissileObjPanel.ValueChanged))]
    static class Patch_MissileValue { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(MissileObjPanel), nameof(MissileObjPanel.UpdValues))]
    static class Patch_MissileUpd { static void Postfix() => SnapHelper.Do(); }

    // sensor/EW panels — same pattern as MissileObjPanel
    // these PartModules have zero Set* methods so panel patches are the only way

    [HarmonyPatch(typeof(IrstPanel), nameof(IrstPanel.ValueChanged))]
    static class Patch_IrstValue { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(JammerPanel), nameof(JammerPanel.ValueChanged))]
    static class Patch_JammerValue { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(RadarPanel), nameof(RadarPanel.ValueChanged))]
    static class Patch_RadarValue { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(RwrPanel), nameof(RwrPanel.ValueChanged))]
    static class Patch_RwrValue { static void Postfix() => SnapHelper.Do(); }

    [HarmonyPatch(typeof(TargetingPodPanel), nameof(TargetingPodPanel.ValueChanged))]
    static class Patch_TargetPodValue { static void Postfix() => SnapHelper.Do(); }

    // smoke emitter panel — SetColor fires on color picker change
    [HarmonyPatch(typeof(SmokeEmitterPanel), nameof(SmokeEmitterPanel.SetColor))]
    static class Patch_SmokeColor { static void Postfix() => SnapHelper.Do(); }
}
