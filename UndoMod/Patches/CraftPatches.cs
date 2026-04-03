using System.Collections;
using System.IO;
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

    // nuclear defense: if IESaveCraft receives a scratch-dir path,
    // replace it with the real craft folder so the save goes to the right place.
    // this catches cases where the game caches the LoadCraft path internally.
    [HarmonyPatch(typeof(CEManager), nameof(CEManager.IESaveCraft))]
    static class Patch_IESaveCraft
    {
        static void Prefix(ref string __0, ref string __1)
        {
            if (!UndoMod.InCraftEditor) return;
            string realFolder = UndoMod.GetRealCraftFolder();
            if (realFolder == null) return;

            if (UndoMod.IsScratchPathPublic(__0))
            {
                Melon<UndoMod>.Logger.Msg($"IESaveCraft path override: {__0} -> {realFolder}");
                __0 = realFolder;
            }
        }
    }

    // also guard Persistence.SerializeCraft — if anything calls it with
    // a scratch path outside our own snapshot code, redirect to real path
    [HarmonyPatch(typeof(Persistence), nameof(Persistence.SerializeCraft))]
    static class Patch_SerializeCraft
    {
        static void Prefix(ref string __1)
        {
            if (!UndoMod.InCraftEditor || UndoMod.IsRestoring) return;
            // our own snapshot code sets IsRestoring=false but we can
            // identify our calls because they target the scratch dir
            // intentionally — skip if we're in the middle of a snapshot
            if (UndoMod.IsSnapshotting) return;

            string realFolder = UndoMod.GetRealCraftFolder();
            if (realFolder == null) return;

            if (UndoMod.IsScratchPathPublic(__1))
            {
                Melon<UndoMod>.Logger.Msg($"SerializeCraft path override: {__1} -> {realFolder}");
                __1 = realFolder;
            }
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
            UndoMod.ClearRestoreGuard();
            UndoMod.ForceRealPersistence();
        }

        // delay capture — SaveCraft starts IESaveCraft coroutine which
        // updates persistence fields when the actual write finishes
        static void Postfix()
        {
            if (!UndoMod.InCraftEditor) return;
            MelonCoroutines.Start(DelayCaptureAfterSave());
        }

        static IEnumerator DelayCaptureAfterSave()
        {
            yield return null;
            yield return null;
            yield return new WaitForSeconds(0.3f);
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
            UndoMod.ClearRestoreGuard();
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

    // new craft resets history, fresh start :)
    [HarmonyPatch(typeof(CEManager), nameof(CEManager.NewCraft))]
    static class Patch_NewCraft
    {
        static void Postfix()
        {
            if (UndoMod.IsRestoring || !UndoMod.InCraftEditor) return;
            UndoMod.ClearRestoreGuard();
            UndoMod.UndoStack.Clear();
            UndoMod.CurrentIndex = -1;
            UndoMod.SnapshotPending = false;
            UndoMod.InitialSnapshotDone = false;
            UndoMod.CaptureRealPersistence();
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

    // loading a craft resets history and snapshots the fresh state
    [HarmonyPatch(typeof(CEManager), nameof(CEManager.LoadCraft), typeof(string))]
    static class Patch_LoadCraft
    {
        static void Postfix(bool __result)
        {
            if (UndoMod.IsRestoring || !UndoMod.InCraftEditor || !__result) return;
            UndoMod.ClearRestoreGuard();
            // suppress Set* snapshots during part init, then take the real one
            UndoMod.UndoStack.Clear();
            UndoMod.CurrentIndex = -1;
            UndoMod.InitialSnapshotDone = false;
            UndoMod.SnapshotPending = false;
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
}
