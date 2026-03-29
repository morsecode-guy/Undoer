namespace UndoMod
{
    // shared postfix for harmony patches — queues a debounced snapshot
    static class SnapHelper
    {
        public static void Do()
        {
            if (!UndoMod.IsRestoring && UndoMod.InCraftEditor
                && UnityEngine.Time.time >= UndoMod.RestoreCooldownUntil)
                UndoMod.RequestSnapshot();
        }

        // take a snapshot RIGHT NOW — used for structural ops (place, delete,
        // duplicate, etc.) so settings changes that follow don't merge with
        // them in the debounce window and cause accidental deletions on undo
        public static void DoNow()
        {
            if (!UndoMod.IsRestoring && UndoMod.InCraftEditor
                && UnityEngine.Time.time >= UndoMod.RestoreCooldownUntil
                && UndoMod.InitialSnapshotDone)
                UndoMod.TakeSnapshotImmediate();
        }
    }

    // catch-all postfix for dynamically patched Set methods on part modules
    internal static class GenericSetPostfix
    {
        public static void Postfix()
        {
            if (!UndoMod.IsRestoring && UndoMod.InCraftEditor
                && UnityEngine.Time.time >= UndoMod.RestoreCooldownUntil)
                UndoMod.RequestSnapshot();
        }
    }
}
