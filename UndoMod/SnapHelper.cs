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
