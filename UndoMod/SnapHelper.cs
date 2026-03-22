namespace UndoMod
{
    // shared postfix for all harmony patches - queues a debounced snapshot
    static class SnapHelper
    {
        /// <summary>Non-structural change — builds data.txt in memory.</summary>
        public static void Do()
        {
            if (!UndoMod.IsRestoring && UndoMod.InCraftEditor
                && UnityEngine.Time.time >= UndoMod.RestoreCooldownUntil)
                UndoMod.RequestSnapshot(structural: false);
        }

        /// <summary>Structural change (parts added/removed) — full SerializeCraft.</summary>
        public static void DoStructural()
        {
            if (!UndoMod.IsRestoring && UndoMod.InCraftEditor
                && UnityEngine.Time.time >= UndoMod.RestoreCooldownUntil)
                UndoMod.RequestSnapshot(structural: true);
        }
    }

    // catch-all postfix for dynamically patched part module Set methods
    // these are non-structural (property changes on existing modules)
    internal static class GenericSetPostfix
    {
        public static void Postfix()
        {
            if (!UndoMod.IsRestoring && UndoMod.InCraftEditor
                && UnityEngine.Time.time >= UndoMod.RestoreCooldownUntil)
                UndoMod.RequestSnapshot(structural: false);
        }
    }
}
