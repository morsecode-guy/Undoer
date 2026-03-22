using System.Collections;
using MelonLoader;
using HarmonyLib;
using UnityEngine;
using Il2CppCraftEditor;

namespace UndoMod
{
    // take initial snapshot after the editor finishes loading
    [HarmonyPatch(typeof(CEManager), nameof(CEManager.Start))]
    static class Patch_Start
    {
        static void Postfix()
        {
            if (UndoMod.InCraftEditor)
                MelonCoroutines.Start(WaitThenSnapshot());
        }

        static IEnumerator WaitThenSnapshot()
        {
            yield return null;
            yield return null;
            yield return new WaitForSeconds(0.5f);
            UndoMod.TakeInitialSnapshot();
        }
    }

    // new craft just resets history, no snapshot needed
    [HarmonyPatch(typeof(CEManager), nameof(CEManager.NewCraft))]
    static class Patch_NewCraft
    {
        static void Postfix()
        {
            if (UndoMod.IsRestoring || !UndoMod.InCraftEditor) return;
            UndoMod.UndoStack.Clear();
            UndoMod.CurrentIndex = -1;
            UndoMod.SnapshotPending = false;
        }
    }

    // loading a craft resets history and takes a fresh snapshot
    [HarmonyPatch(typeof(CEManager), nameof(CEManager.LoadCraft), typeof(string))]
    static class Patch_LoadCraft
    {
        static void Postfix(bool __result)
        {
            if (UndoMod.IsRestoring || !UndoMod.InCraftEditor || !__result) return;
            UndoMod.UndoStack.Clear();
            UndoMod.CurrentIndex = -1;
            UndoMod.TakeSnapshot("load", structural: true);
        }
    }
}
