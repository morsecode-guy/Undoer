using System.Collections;
using MelonLoader;
using HarmonyLib;
using UnityEngine;
using Il2CppCraftEditor;
using Il2Cpp;

namespace UndoMod
{
    // grab initial snapshot after the editor finishes loading
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

    // make sure persistence points to the real craft before any save
    // this is the main defense against save corruption — catches any
    // stale scratch dir refs that slipped through the per-frame guard
    [HarmonyPatch(typeof(CEManager), nameof(CEManager.SaveCraft))]
    static class Patch_SaveCraft
    {
        static void Prefix()
        {
            if (!UndoMod.InCraftEditor) return;
            UndoMod.ForceRealPersistence();
        }

        // after a real save, recapture persistence in case the game
        // changed something (e.g. savedTextureCount)
        static void Postfix()
        {
            if (!UndoMod.InCraftEditor) return;
            UndoMod.CaptureRealPersistence();
        }
    }

    // same for save-as — force real state, then recapture after
    [HarmonyPatch(typeof(CEManager), nameof(CEManager.SaveCraftAs))]
    static class Patch_SaveCraftAs
    {
        static void Prefix()
        {
            if (!UndoMod.InCraftEditor) return;
            UndoMod.ForceRealPersistence();
        }

        static void Postfix()
        {
            if (!UndoMod.InCraftEditor) return;
            // delay capture — SaveCraftAs starts a coroutine that
            // updates persistence when it finishes
            MelonCoroutines.Start(DelayCaptureRealPersistence());
        }

        static IEnumerator DelayCaptureRealPersistence()
        {
            yield return null;
            yield return null;
            UndoMod.CaptureRealPersistence();
        }
    }

    // also guard the static SerializeCraft to catch auto-saves etc
    [HarmonyPatch(typeof(Persistence), nameof(Persistence.SerializeCraft))]
    static class Patch_SerializeCraft
    {
        // after any SerializeCraft call (game save, auto-save, etc),
        // if it wrote to a non-scratch path, recapture persistence
        static void Postfix(string __1)
        {
            if (!UndoMod.InCraftEditor || UndoMod.IsRestoring) return;
            if (!UndoMod.IsScratchPathPublic(__1))
                UndoMod.CaptureRealPersistence();
        }
    }

    // new craft resets history, fresh start :)
    [HarmonyPatch(typeof(CEManager), nameof(CEManager.NewCraft))]
    static class Patch_NewCraft
    {
        static void Postfix()
        {
            if (UndoMod.IsRestoring || !UndoMod.InCraftEditor) return;
            UndoMod.UndoStack.Clear();
            UndoMod.CurrentIndex = -1;
            UndoMod.SnapshotPending = false;
            UndoMod.CaptureRealPersistence();
        }
    }

    // loading a craft resets history and snapshots the fresh state
    [HarmonyPatch(typeof(CEManager), nameof(CEManager.LoadCraft), typeof(string))]
    static class Patch_LoadCraft
    {
        static void Postfix(bool __result)
        {
            if (UndoMod.IsRestoring || !UndoMod.InCraftEditor || !__result) return;
            UndoMod.UndoStack.Clear();
            UndoMod.CurrentIndex = -1;
            UndoMod.CaptureRealPersistence();
            UndoMod.TakeSnapshot("load");
        }
    }
}
