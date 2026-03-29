using HarmonyLib;
using Il2CppCraftEditor;
using Il2Cpp;

namespace UndoMod
{
    // custom axis / input system patches

    // adding a new custom axis to the craft
    [HarmonyPatch(typeof(Craft), nameof(Craft.AddCustomAxis))]
    static class Patch_AddAxis { static void Postfix() => SnapHelper.DoNow(); }

    // "create new" button in the custom inputs panel
    [HarmonyPatch(typeof(CustomInputsPanel), nameof(CustomInputsPanel.CreateNew))]
    static class Patch_NewInput { static void Postfix() => SnapHelper.DoNow(); }

    // adding/removing input responses on a part's InputProcessor
    [HarmonyPatch(typeof(InputProcessor), nameof(InputProcessor.AddResponse),
        typeof(Channel))]
    static class Patch_AddResp1 { static void Postfix() => SnapHelper.DoNow(); }

    [HarmonyPatch(typeof(InputProcessor), nameof(InputProcessor.AddResponse),
        typeof(Channel), typeof(float))]
    static class Patch_AddResp2 { static void Postfix() => SnapHelper.DoNow(); }

    [HarmonyPatch(typeof(InputProcessor), nameof(InputProcessor.RemoveResponse))]
    static class Patch_RemoveResp { static void Postfix() => SnapHelper.DoNow(); }

    // input processor panel "set dirty" — fires when any input field changes
    [HarmonyPatch(typeof(InputProcessorPanel), nameof(InputProcessorPanel.SetDirty))]
    static class Patch_InputDirty { static void Postfix() => SnapHelper.Do(); }
}
