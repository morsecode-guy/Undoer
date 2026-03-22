using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MelonLoader;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using Il2CppCraftEditor;
using Il2Cpp;

[assembly: MelonInfo(typeof(UndoMod.UndoMod), "Undoer", "2.1.0", "Morse Code Guy")]
[assembly: MelonGame("Stonext Games", "Flyout")]

namespace UndoMod
{
    // ---------------------------------------------------------------
    // Undo entry — holds complete craft state either on disk or in
    // memory. Every entry stores the full data.txt text so we never
    // need to reconstruct state from deltas.
    // ---------------------------------------------------------------

    internal class UndoEntry
    {
        /// <summary>data.txt content (always set)</summary>
        public string CraftText;

        /// <summary>
        /// Path to a Textures folder on disk. Shared between entries
        /// that don't change paint. Null when no paint data exists.
        /// </summary>
        public string TexturePath;

        /// <summary>
        /// If this entry was created via full SerializeCraft the
        /// folder lives on disk and we clean it up in ClearHistory.
        /// Null for in-memory-only entries.
        /// </summary>
        public string DiskFolder;
    }

    // ---------------------------------------------------------------
    // Main mod class
    // ---------------------------------------------------------------

    public class UndoMod : MelonMod
    {
        // state
        internal static readonly List<UndoEntry> UndoStack = new();
        internal static int CurrentIndex = -1;
        internal static bool IsRestoring;
        internal static bool InCraftEditor;
        internal static string UndoTempDir;
        static bool _patchesDone;

        // cooldown after restore to suppress spurious callbacks
        internal static float RestoreCooldownUntil;

        // snapshot debounce
        internal static float LastSnapshotTime;
        internal static bool SnapshotPending;
        internal static float SnapshotDelay = 0.5f;

        // whether the pending snapshot is structural (parts added/removed)
        internal static bool PendingStructural;

        // config
        static readonly int[] MemoryPresets = { 50, 100, 200, 500 };
        static int _presetIndex = 2;
        static int MaxUndoSteps => MemoryPresets[_presetIndex];

        // hud
        static string _statusText = "";
        static float _statusTimer;

        // camera override after restore
        internal static bool CameraOverrideActive;
        static Vector3 _camPos;
        static Quaternion _camRot;
        static Vector3 _camPivot;
        static float _camZoom, _camOrthoSize, _camOrthoZoom;

        // cached header/footer for building data.txt in memory
        static string _cachedHeader;   // everything before first Part block
        static string _cachedFooter;   // the |-----| line at end

        // -----------------------------------------------------------
        // init
        // -----------------------------------------------------------

        public override void OnInitializeMelon()
        {
            UndoTempDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Flyout", "UndoMod");

            if (Directory.Exists(UndoTempDir))
                Directory.Delete(UndoTempDir, true);

            Directory.CreateDirectory(UndoTempDir);
            LoggerInstance.Msg("Undoer ready  |  temp: " + UndoTempDir);
        }

        // -----------------------------------------------------------
        // scene tracking
        // -----------------------------------------------------------

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            bool was = InCraftEditor;
            InCraftEditor = sceneName == "CraftEditor";

            if (InCraftEditor && !was)
            {
                if (!_patchesDone)
                {
                    _patchesDone = true;
                    PatchPartModuleSetMethods();
                }

                LoggerInstance.Msg("Entered craft editor");
                ClearHistory();
            }
            else if (!InCraftEditor && was)
            {
                LoggerInstance.Msg("Left craft editor");
                ClearHistory();
            }
        }

        // -----------------------------------------------------------
        // per-frame update
        // -----------------------------------------------------------

        // paint stroke tracking via mouse release in paint mode
        static bool _wasMouseDown;
        static bool _mouseDownOnUI;

        public override void OnUpdate()
        {
            if (!InCraftEditor) return;

            // flush pending snapshot after debounce
            if (SnapshotPending && Time.time - LastSnapshotTime >= SnapshotDelay)
            {
                SnapshotPending = false;
                bool structural = PendingStructural;
                PendingStructural = false;
                TakeSnapshot("action", structural);
            }

            // detect paint stroke end: mouse released in paint mode
            bool inPaintMode = CEManager.instance != null
                && CEManager.instance.Mode == Il2CppCraftEditor.Mode.Paint;

            if (inPaintMode)
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                bool mouseDown = mouse != null && mouse.leftButton.isPressed;

                if (mouseDown && !_wasMouseDown)
                {
                    var es = UnityEngine.EventSystems.EventSystem.current;
                    _mouseDownOnUI = es != null && es.IsPointerOverGameObject();
                }

                // paint strokes are structural (need full serialize for textures)
                if (_wasMouseDown && !mouseDown && !_mouseDownOnUI)
                    RequestSnapshot(structural: true);

                _wasMouseDown = mouseDown;
            }
            else
            {
                _wasMouseDown = false;
                _mouseDownOnUI = false;
            }

            if (_statusTimer > 0f)
                _statusTimer -= Time.deltaTime;

            // keybinds
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.f7Key.wasPressedThisFrame)
            {
                _presetIndex = (_presetIndex + 1) % MemoryPresets.Length;
                ShowStatus($"Undo memory: {MaxUndoSteps} steps");
                LoggerInstance.Msg($"Undo memory set to {MaxUndoSteps} steps");
                return;
            }

            bool ctrl = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
            if (!ctrl) return;

            if (kb.zKey.wasPressedThisFrame)
            {
                bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
                if (shift) Redo(); else Undo();
            }
            else if (kb.yKey.wasPressedThisFrame)
            {
                Redo();
            }
        }

        // -----------------------------------------------------------
        // hud overlay
        // -----------------------------------------------------------

        public override void OnGUI()
        {
            if (!InCraftEditor || _statusTimer <= 0f || string.IsNullOrEmpty(_statusText))
                return;

            float a = Mathf.Clamp01(_statusTimer / 0.5f);

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperCenter,
            };

            float x = Screen.width / 2f - 200;

            GUI.color = new Color(0, 0, 0, a * 0.7f);
            GUI.Label(new Rect(x + 1, 49, 400, 40), _statusText, style);

            GUI.color = new Color(1, 1, 1, a);
            GUI.Label(new Rect(x, 48, 400, 40), _statusText, style);

            GUI.color = Color.white;
        }

        // -----------------------------------------------------------
        // snapshot management
        // -----------------------------------------------------------

        /// <summary>
        /// Queue a debounced snapshot.
        /// structural = true  → parts were added/removed, or paint
        ///                      changed → needs full Persistence.SerializeCraft
        /// structural = false → only existing part data changed →
        ///                      build data.txt in memory from Part.SerializeData()
        /// </summary>
        internal static void RequestSnapshot(bool structural = false)
        {
            if (IsRestoring || Time.time < RestoreCooldownUntil) return;
            LastSnapshotTime = Time.time;
            SnapshotPending = true;
            if (structural) PendingStructural = true;
        }

        /// <summary>
        /// Build a snapshot. For structural changes (or the very first
        /// snapshot) we use Persistence.SerializeCraft for correctness.
        /// For non-structural changes we bypass SerializeCraft entirely
        /// and build data.txt in memory by calling each Part's
        /// SerializeData() — zero disk I/O, zero PNG encoding.
        /// </summary>
        internal static void TakeSnapshot(string reason, bool structural = true)
        {
            if (IsRestoring || Time.time < RestoreCooldownUntil) return;

            var mgr = CEManager.instance;
            if (mgr == null || mgr.craft == null) return;

            try
            {
                UndoEntry entry;

                if (structural || _cachedHeader == null)
                {
                    entry = TakeFullSnapshot(mgr);
                }
                else
                {
                    entry = TakeMemorySnapshot(mgr);
                }

                if (entry == null) return;

                // trim redo history
                if (CurrentIndex < UndoStack.Count - 1)
                    for (int i = UndoStack.Count - 1; i > CurrentIndex; i--)
                    {
                        CleanupEntry(UndoStack[i]);
                        UndoStack.RemoveAt(i);
                    }

                UndoStack.Add(entry);
                CurrentIndex = UndoStack.Count - 1;

                // cap size
                while (UndoStack.Count > MaxUndoSteps)
                {
                    CleanupEntry(UndoStack[0]);
                    UndoStack.RemoveAt(0);
                    CurrentIndex--;
                }

                Melon<UndoMod>.Logger.Msg(
                    $"[{CurrentIndex}] {reason}" +
                    (entry.DiskFolder != null ? " (full)" : " (mem)"));
            }
            catch (Exception ex)
            {
                Melon<UndoMod>.Logger.Error($"Snapshot failed: {ex}");
            }
        }

        /// <summary>
        /// Full serialization via Persistence.SerializeCraft.
        /// Creates a .craft folder on disk with data.txt + Textures/.
        /// Caches the header for subsequent in-memory snapshots.
        /// Uses the hasBeenModified trick: for non-paint snapshots
        /// we skip PNG encoding and copy textures from the previous entry.
        /// </summary>
        static UndoEntry TakeFullSnapshot(CEManager mgr)
        {
            string savedLoaded = Persistence.currentlyLoaded;
            bool savedTemp = Persistence.isTemp;
            string savedRoot = Persistence.currentRootFolder;

            string name = $"undo_{DateTime.Now:yyyyMMdd_HHmmss_fff}.craft";
            string path = Path.Combine(UndoTempDir, name);

            bool isPaint = mgr.Mode == Il2CppCraftEditor.Mode.Paint;

            // find previous entry's texture folder for reuse
            string prevTexDir = null;
            if (!isPaint)
            {
                for (int i = UndoStack.Count - 1; i >= 0; i--)
                {
                    var tp = UndoStack[i].TexturePath;
                    if (tp != null && Directory.Exists(tp))
                    { prevTexDir = tp; break; }
                }
            }

            // temporarily suppress PNG encoding for non-paint snapshots
            List<Paintable> modified = null;
            if (prevTexDir != null)
            {
                modified = new List<Paintable>();
                foreach (var part in mgr.craft.parts)
                {
                    if (part == null) continue;
                    var p = part.GetComponent<Paintable>();
                    if (p != null && p.hasBeenModified)
                    {
                        modified.Add(p);
                        p.hasBeenModified = false;
                    }
                }
            }

            try
            {
                Persistence.SerializeCraft(mgr.craft, path, true);
            }
            finally
            {
                // restore hasBeenModified flags
                if (modified != null)
                    foreach (var p in modified)
                        p.hasBeenModified = true;

                // restore persistence state
                Persistence.currentlyLoaded = savedLoaded;
                Persistence.isTemp = savedTemp;
                Persistence.currentRootFolder = savedRoot;
            }

            // copy textures from previous snapshot if we skipped PNGs
            if (prevTexDir != null)
            {
                string newTexDir = Path.Combine(path, "Textures");
                Directory.CreateDirectory(newTexDir);
                foreach (var file in Directory.GetFiles(prevTexDir))
                    File.Copy(file, Path.Combine(newTexDir, Path.GetFileName(file)), true);
            }

            string dataFile = Path.Combine(path, "data.txt");
            if (!File.Exists(dataFile)) return null;

            string craftText = File.ReadAllText(dataFile);

            // cache header/footer for future in-memory snapshots
            CacheHeaderFooter(craftText);

            // figure out texture path for this entry
            string texDir2 = Path.Combine(path, "Textures");
            string texPath = Directory.Exists(texDir2) &&
                             Directory.GetFiles(texDir2).Length > 0
                             ? texDir2
                             : prevTexDir;

            return new UndoEntry
            {
                CraftText = craftText,
                TexturePath = texPath,
                DiskFolder = path
            };
        }

        /// <summary>
        /// Fast in-memory snapshot — bypasses Persistence.SerializeCraft.
        /// Builds data.txt by calling each Part.SerializeData() and
        /// prepending the cached header. No disk I/O, no PNG encoding.
        /// </summary>
        static UndoEntry TakeMemorySnapshot(CEManager mgr)
        {
            var sb = new StringBuilder(_cachedHeader.Length + 4096);
            sb.Append(_cachedHeader);

            foreach (var go in mgr.craft.parts)
            {
                if (go == null) continue;
                var part = go.GetComponent<Part>();
                if (part == null) continue;
                sb.Append(part.SerializeData(false));
            }

            sb.Append(_cachedFooter);

            // reuse textures from most recent entry
            string texPath = null;
            for (int i = UndoStack.Count - 1; i >= 0; i--)
            {
                var tp = UndoStack[i].TexturePath;
                if (tp != null && Directory.Exists(tp))
                { texPath = tp; break; }
            }

            return new UndoEntry
            {
                CraftText = sb.ToString(),
                TexturePath = texPath,
                DiskFolder = null   // not on disk
            };
        }

        /// <summary>
        /// Parse header (everything before the first Part block) and
        /// footer from a full data.txt string.
        /// </summary>
        static void CacheHeaderFooter(string craftText)
        {
            // header: everything up to and including the newline before "Part"
            int idx = craftText.IndexOf("\nPart\n");
            if (idx < 0) idx = craftText.IndexOf("\nPart\r\n");
            if (idx >= 0)
                _cachedHeader = craftText.Substring(0, idx + 1);

            // footer: last line starting with |
            int lastPipe = craftText.LastIndexOf('|');
            if (lastPipe >= 0)
                _cachedFooter = craftText.Substring(lastPipe);
            else
                _cachedFooter = "|---------------------------------------------------------------------|\n";
        }

        // take one snapshot if there is nothing in the stack yet
        internal static void TakeInitialSnapshot()
        {
            if (UndoStack.Count == 0)
                TakeSnapshot("initial", structural: true);
        }

        // -----------------------------------------------------------
        // undo / redo
        // -----------------------------------------------------------

        void Undo()
        {
            if (SnapshotPending)
            {
                SnapshotPending = false;
                bool structural = PendingStructural;
                PendingStructural = false;
                TakeSnapshot("pre-undo", structural);
            }

            if (CurrentIndex <= 0)
            {
                ShowStatus("Nothing to undo");
                return;
            }

            CurrentIndex--;
            Restore(UndoStack[CurrentIndex]);
            ShowStatus($"Undo ({CurrentIndex}/{UndoStack.Count - 1})");
        }

        void Redo()
        {
            if (CurrentIndex >= UndoStack.Count - 1)
            {
                ShowStatus("Nothing to redo");
                return;
            }

            CurrentIndex++;
            Restore(UndoStack[CurrentIndex]);
            ShowStatus($"Redo ({CurrentIndex}/{UndoStack.Count - 1})");
        }

        void Restore(UndoEntry entry)
        {
            var mgr = CEManager.instance;
            if (mgr == null || entry == null) return;

            try
            {
                IsRestoring = true;

                string prevLoaded = Persistence.currentlyLoaded;
                bool prevTemp = Persistence.isTemp;
                string prevRoot = Persistence.currentRootFolder;
                Mode prevMode = mgr.Mode;

                // save camera state
                var ceCam = mgr.camera;
                Vector3 savedPos = Vector3.zero;
                Quaternion savedRot = Quaternion.identity;
                Vector3 savedPivot = Vector3.zero;
                float savedZoom = 0f, savedOrthoSize = 0f, savedOrthoZoom = 0f;
                bool hasCam = ceCam != null && ceCam.camera != null;
                if (hasCam)
                {
                    savedPos = ceCam.camera.transform.position;
                    savedRot = ceCam.camera.transform.rotation;
                    savedPivot = ceCam.Pivot;
                    savedZoom = ceCam.zoom;
                    savedOrthoSize = ceCam.orthoSize;
                    savedOrthoZoom = ceCam.orthoZoom;
                }

                // write craft data to a temp folder for LoadCraft
                // (LoadCraft requires a file path, not a string)
                string loadFolder;
                bool isTempFolder = false;

                if (entry.DiskFolder != null && Directory.Exists(entry.DiskFolder))
                {
                    // entry lives on disk already
                    loadFolder = entry.DiskFolder;
                }
                else
                {
                    // entry is memory-only; write to temp folder
                    loadFolder = Path.Combine(UndoTempDir, "_restore_tmp.craft");
                    if (Directory.Exists(loadFolder))
                        Directory.Delete(loadFolder, true);
                    Directory.CreateDirectory(loadFolder);

                    File.WriteAllText(
                        Path.Combine(loadFolder, "data.txt"),
                        entry.CraftText);

                    // copy textures
                    if (entry.TexturePath != null && Directory.Exists(entry.TexturePath))
                    {
                        string destTex = Path.Combine(loadFolder, "Textures");
                        Directory.CreateDirectory(destTex);
                        foreach (var f in Directory.GetFiles(entry.TexturePath))
                            File.Copy(f, Path.Combine(destTex, Path.GetFileName(f)), true);
                    }

                    isTempFolder = true;
                }

                bool ok = mgr.LoadCraft(Path.Combine(loadFolder, "data.txt"));

                if (ok)
                {
                    LoggerInstance.Msg($"Restored snapshot {CurrentIndex}");

                    // camera override
                    _camPos = savedPos;
                    _camRot = savedRot;
                    _camPivot = savedPivot;
                    _camZoom = savedZoom;
                    _camOrthoSize = savedOrthoSize;
                    _camOrthoZoom = savedOrthoZoom;
                    CameraOverrideActive = true;

                    var cam2 = mgr.camera;
                    if (cam2 != null && cam2.camera != null)
                    {
                        cam2.Pivot = savedPivot;
                        cam2.zoom = savedZoom;
                        cam2.orthoSize = savedOrthoSize;
                        cam2.orthoZoom = savedOrthoZoom;
                        cam2.camera.transform.position = savedPos;
                        cam2.camera.transform.rotation = savedRot;
                        cam2.ResetVelocity();
                    }

                    // restore persistence
                    Persistence.currentlyLoaded = prevLoaded;
                    Persistence.isTemp = prevTemp;
                    Persistence.currentRootFolder = prevRoot;

                    // re-cache header from the restored craft so future
                    // in-memory snapshots use the correct control law /
                    // fuel system state from this undo point
                    CacheHeaderFooter(entry.CraftText);

                    // delayed mode restore for paint
                    if (prevMode != Mode.Edit)
                        MelonCoroutines.Start(DelayedModeRestore(prevMode));
                }
                else
                {
                    LoggerInstance.Error("LoadCraft failed during restore");
                }

                // clean up the throw-away temp folder
                if (isTempFolder)
                {
                    try { Directory.Delete(loadFolder, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Restore failed: {ex}");
            }
            finally
            {
                IsRestoring = false;
                RestoreCooldownUntil = Time.time + 1.0f;
                SnapshotPending = false;
            }
        }

        // -----------------------------------------------------------
        // camera override (postfix on CECamera.LateUpdate)
        // -----------------------------------------------------------

        internal static void ApplyCameraOverride(CECamera cam)
        {
            if (!CameraOverrideActive) return;

            var mouse = Mouse.current;
            if (mouse != null)
            {
                bool orbiting = mouse.rightButton.isPressed;
                bool panning  = mouse.middleButton.isPressed;
                bool zooming  = Mathf.Abs(mouse.scroll.ReadValue().y) > 0.01f;
                if (orbiting || panning || zooming)
                {
                    CameraOverrideActive = false;
                    return;
                }
            }

            if (cam != null && cam.camera != null)
            {
                cam.Pivot = _camPivot;
                cam.zoom = _camZoom;
                cam.orthoSize = _camOrthoSize;
                cam.orthoZoom = _camOrthoZoom;
                cam.camera.transform.position = _camPos;
                cam.camera.transform.rotation = _camRot;
                cam.ResetVelocity();
            }
        }

        // -----------------------------------------------------------
        // helpers
        // -----------------------------------------------------------

        static IEnumerator DelayedModeRestore(Mode targetMode)
        {
            yield return null;
            yield return null;
            yield return new WaitForSeconds(0.5f);

            var mgr = CEManager.instance;
            if (mgr != null)
            {
                mgr.SetMode(Mode.Edit);
                mgr.SetMode(targetMode);
            }
        }

        void ClearHistory()
        {
            foreach (var e in UndoStack) CleanupEntry(e);
            UndoStack.Clear();
            CurrentIndex = -1;
            SnapshotPending = false;
            PendingStructural = false;
            _cachedHeader = null;
            _cachedFooter = null;
        }

        static void CleanupEntry(UndoEntry entry)
        {
            if (entry?.DiskFolder != null)
            {
                try
                {
                    if (Directory.Exists(entry.DiskFolder))
                        Directory.Delete(entry.DiskFolder, true);
                }
                catch { }
            }
        }

        static void ShowStatus(string text)
        {
            _statusText = text;
            _statusTimer = 1.8f;
        }

        // -----------------------------------------------------------
        // dynamic part module patching
        // -----------------------------------------------------------

        void PatchPartModuleSetMethods()
        {
            var harmony = HarmonyInstance;
            var postfix = new HarmonyMethod(
                typeof(GenericSetPostfix).GetMethod(
                    nameof(GenericSetPostfix.Postfix),
                    BindingFlags.Public | BindingFlags.Static));

            var targets = new Dictionary<Type, string[]>
            {
                { typeof(DrumMagazine),     new[] { "SetRows", "SetCaliber" } },
                { typeof(PGun),             new[] { "SetBarrelCount", "SetBarrelLength", "SetCaliber", "SetRpm" } },
                { typeof(PMunition),        new[] { "SetRadius", "SetDiameter", "SetNoseCone", "SetNoseConeAspect",
                                                    "SetGuidance", "SetGuidanceAspect", "SetPayload", "SetPayloadAspect",
                                                    "SetFuelTank", "SetFuelTankAspect", "SetMotor", "SetDetonator" } },
                { typeof(FuelTank),         new[] { "SetFill", "SetFuelSystem", "SetPriority", "SetFuel" } },
                { typeof(Controller),       new[] { "SetResponsiveness", "SetMin", "SetMax", "SetOffset", "SetMachResp" } },
                { typeof(Connector),        new[] { "SetForce" } },
                { typeof(LandingGear),      new[] { "SetPower", "SetSteerAngle", "SetBrakeTorque", "SetWheelHeight" } },
                { typeof(ProceduralEngine), new[] { "SetArrangement", "SetRows", "SetCylindersPerBank", "SetBore",
                                                    "SetStroke", "SetCpr", "SetValveCount", "SetValveDiameter",
                                                    "SetScale", "SetIdleThrottle", "SetAFR", "SetRpmLimit",
                                                    "SetTurbo", "SetSuper", "SetTurboPrat", "SetSuperPrat",
                                                    "SetSuperAlt", "SetSuperGearKey" } },
                { typeof(ProceduralProp),   new[] { "SetPusher", "SetBladeCount", "SetBladeLength", "SetBladeChord",
                                                    "SetBladeTwist", "SetScale", "SetDirection", "SetConstantSpeedMode",
                                                    "SetConstantRPM", "SetConstantSpring", "SetConstantPow",
                                                    "SetConstantMach", "SetPitch", "SetRangeMin", "SetRangeMax" } },
                { typeof(ProceduralLG),     new[] { "SetWheelSetup", "SetBrakeSteering", "SetWheelSize", "SetScale", "SetLength" } },
                { typeof(DuctedFan),        new[] { "SetDiameter", "SetBladeAngle" } },
                { typeof(ElectricMotor),    new[] { "SetRadius", "SetLength", "SetTorque", "SetMaxRPM" } },
                { typeof(BleedAirNozzle),   new[] { "SetRadius", "SetConeRadius", "SetPosition", "SetLength" } },
                { typeof(CoveredWheel),     new[] { "SetRadius", "SetWidth", "SetBrakeForce" } },
                { typeof(DropTank),         new[] { "SetLength", "SetFrontCone", "SetRearCone" } },
                { typeof(GeneralLight),     new[] { "SetPower", "SetAngle" } },
                { typeof(NavLight),         new[] { "SetPower" } },
                { typeof(LiftFan),          new[] { "SetDiameter" } },
                { typeof(Parachute),        new[] { "SetRadius", "SetRopeLength", "SetDelay" } },
                { typeof(Paintable),        new[] { "SetResolution" } },
                { typeof(ImportSettings),   new[] { "SetMass", "SetMirror", "SetCollision", "SetDrag" } },
                { typeof(ExhaustEffect),    new[] { "SetEngine", "SetExhaustCount" } },
                { typeof(Gear),             new[] { "SetGearCount", "SetGear", "SetGearRatio", "SetUpKey", "SetDownKey" } },
            };

            int ok = 0, fail = 0;

            foreach (var (type, methods) in targets)
            foreach (var name in methods)
            {
                try
                {
                    var found = type
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .Where(m => m.Name == name && !m.IsAbstract && !m.IsGenericMethod);

                    foreach (var m in found)
                    {
                        harmony.Patch(m, postfix: postfix);
                        ok++;
                    }
                }
                catch (Exception ex)
                {
                    LoggerInstance.Warning($"Cant patch {type.Name}.{name}: {ex.Message}");
                    fail++;
                }
            }

            LoggerInstance.Msg($"Patched {ok} part module Set methods ({fail} failed)");
        }
    }
}
