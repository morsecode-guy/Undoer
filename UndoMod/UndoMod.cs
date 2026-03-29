using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using Il2CppCraftEditor;
using Il2Cpp;

[assembly: MelonInfo(typeof(UndoMod.UndoMod), "Undoer", "3.0.0", "Morse Code Guy")]
[assembly: MelonGame("Stonext Games", "Flyout")]

namespace UndoMod
{
    // one undo/redo entry, lives entirely in memory :3
    internal class UndoEntry
    {
        // all files in the .craft folder, keyed by relative path
        // e.g. "data.txt", "Textures/part_0.png", "Blueprints/bp.png"
        public Dictionary<string, byte[]> Files;

        // prev textures dict ref for sharing between non-paint entries
        public Dictionary<string, byte[]> SharedTextures;
    }

    public class UndoMod : MelonMod
    {
        internal static readonly List<UndoEntry> UndoStack = new();
        internal static int CurrentIndex = -1;
        internal static bool IsRestoring;
        internal static bool InCraftEditor;
        internal static string ScratchDir; // single reusable temp folder for serialize/load
        static bool _patchesDone;

        // the "real" persistence state — game's SerializeCraft and LoadCraft
        // both overwrite these to our temp dir, so we track + restore them :)
        internal static string RealCurrentlyLoaded;
        internal static bool RealIsTemp;
        internal static string RealCurrentRootFolder;
        internal static bool RealSaveAs;
        internal static bool RealIsAutoSave;
        internal static int RealSavedTextureCount;
        static bool _realPersistenceValid;

        // cooldown after restore so we dont snapshot the restore itself
        internal static float RestoreCooldownUntil;

        // snapshot debounce
        internal static float LastSnapshotTime;
        internal static bool SnapshotPending;
        internal static float SnapshotDelay = 0.5f;

        // undo memory config (F7 to cycle)
        static readonly int[] MemoryPresets = { 50, 100, 200, 500 };
        static int _presetIndex = 2;
        static int MaxUndoSteps => MemoryPresets[_presetIndex];

        // hud toast
        static string _statusText = "";
        static float _statusTimer;

        // camera override — keeps camera still after restore
        internal static bool CameraOverrideActive;
        static Vector3 _camPos;
        static Quaternion _camRot;
        static Vector3 _camPivot;
        static float _camZoom, _camOrthoSize, _camOrthoZoom;

        // --- init ---

        public override void OnInitializeMelon()
        {
            ScratchDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Flyout", "UndoMod", "scratch.craft");

            if (Directory.Exists(ScratchDir))
                Directory.Delete(ScratchDir, true);

            Directory.CreateDirectory(ScratchDir);
            LoggerInstance.Msg("Undoer ready  |  in-memory snapshots");
        }

        // --- scene tracking ---

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

        // --- per-frame update ---

        // paint stroke tracking
        static bool _wasMouseDown;
        static bool _mouseDownOnUI;

        public override void OnUpdate()
        {
            if (!InCraftEditor) return;

            // flush pending snapshot after debounce
            if (SnapshotPending && Time.time - LastSnapshotTime >= SnapshotDelay)
            {
                SnapshotPending = false;
                TakeSnapshot("action");
            }

            // persistence guard: if the game drifted to our scratch dir, fix it
            if (_realPersistenceValid && !IsRestoring)
            {
                var cl = Persistence.currentlyLoaded;
                if (IsScratchPath(cl))
                {
                    Persistence.currentlyLoaded = RealCurrentlyLoaded;
                    Persistence.isTemp = RealIsTemp;
                    Persistence.currentRootFolder = RealCurrentRootFolder;
                    Persistence.saveAs = RealSaveAs;
                    Persistence.isAutoSave = RealIsAutoSave;
                    Persistence.savedTextureCount = RealSavedTextureCount;
                }
                else if (cl != null && cl != RealCurrentlyLoaded)
                {
                    CaptureRealPersistence();
                }
            }

            // detect paint strokes ending (mouse release in paint mode)
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

                // paint needs full serialize for the textures
                if (_wasMouseDown && !mouseDown && !_mouseDownOnUI)
                    RequestSnapshot();

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

        // --- hud overlay ---

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

        // --- snapshots ---

        // queue a debounced snapshot
        internal static void RequestSnapshot()
        {
            if (IsRestoring || Time.time < RestoreCooldownUntil) return;
            LastSnapshotTime = Time.time;
            SnapshotPending = true;
        }

        // serialize the whole craft to disk, skipping png encoding
        // for non-paint snapshots (reuses textures from prev entry)
        internal static void TakeSnapshot(string reason)
        {
            if (IsRestoring || Time.time < RestoreCooldownUntil) return;

            var mgr = CEManager.instance;
            if (mgr == null || mgr.craft == null) return;

            try
            {
                var entry = TakeFullSnapshot(mgr);
                if (entry == null) return;

                // trim redo history ahead of us
                if (CurrentIndex < UndoStack.Count - 1)
                    for (int i = UndoStack.Count - 1; i > CurrentIndex; i--)
                        UndoStack.RemoveAt(i);

                UndoStack.Add(entry);
                CurrentIndex = UndoStack.Count - 1;

                // cap stack size
                while (UndoStack.Count > MaxUndoSteps)
                {
                    UndoStack.RemoveAt(0);
                    CurrentIndex--;
                }

                Melon<UndoMod>.Logger.Msg(
                    $"[{CurrentIndex}] {reason}  ({UndoStack.Count} in memory)");
            }
            catch (Exception ex)
            {
                Melon<UndoMod>.Logger.Error($"Snapshot failed: {ex}");
            }
        }

        // full serialize via Persistence.SerializeCraft into the scratch
        // folder, slurp everything into memory, then wipe the folder :3
        static UndoEntry TakeFullSnapshot(CEManager mgr)
        {
            string savedLoaded = _realPersistenceValid ? RealCurrentlyLoaded : Persistence.currentlyLoaded;
            bool savedTemp = _realPersistenceValid ? RealIsTemp : Persistence.isTemp;
            string savedRoot = _realPersistenceValid ? RealCurrentRootFolder : Persistence.currentRootFolder;
            bool savedSaveAs = _realPersistenceValid ? RealSaveAs : Persistence.saveAs;
            bool savedIsAutoSave = _realPersistenceValid ? RealIsAutoSave : Persistence.isAutoSave;
            int savedTexCount = _realPersistenceValid ? RealSavedTextureCount : Persistence.savedTextureCount;

            bool isPaint = mgr.Mode == Il2CppCraftEditor.Mode.Paint;

            // reuse prev entry's textures if we're not painting
            Dictionary<string, byte[]> prevTex = null;
            if (!isPaint)
            {
                for (int i = UndoStack.Count - 1; i >= 0; i--)
                {
                    if (UndoStack[i].SharedTextures != null && UndoStack[i].SharedTextures.Count > 0)
                    { prevTex = UndoStack[i].SharedTextures; break; }
                }
            }

            // fake hasBeenModified to skip expensive png encoding
            List<Paintable> modified = null;
            if (prevTex != null)
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

            // wipe scratch folder before writing
            WipeScratch();

            try
            {
                Persistence.SerializeCraft(mgr.craft, ScratchDir, true);
            }
            finally
            {
                // put hasBeenModified back
                if (modified != null)
                    foreach (var p in modified)
                        p.hasBeenModified = true;

                // put persistence back where it belongs
                Persistence.currentlyLoaded = savedLoaded;
                Persistence.isTemp = savedTemp;
                Persistence.currentRootFolder = savedRoot;
                Persistence.saveAs = savedSaveAs;
                Persistence.isAutoSave = savedIsAutoSave;
                Persistence.savedTextureCount = savedTexCount;
            }

            // slurp ALL files into memory
            string dataFile = Path.Combine(ScratchDir, "data.txt");
            if (!File.Exists(dataFile)) return null;

            var files = new Dictionary<string, byte[]>();
            SlurpDirectory(ScratchDir, ScratchDir, files);

            // figure out shared textures for this entry
            // (either freshly encoded or reused from prev)
            var texFiles = new Dictionary<string, byte[]>();
            foreach (var kv in files)
                if (kv.Key.StartsWith("Textures/") || kv.Key.StartsWith("Textures\\"))
                    texFiles[kv.Key] = kv.Value;
            var sharedTex = texFiles.Count > 0 ? texFiles : prevTex;

            // if we skipped encoding, patch in the prev textures
            if (prevTex != null && texFiles.Count == 0)
                foreach (var kv in prevTex)
                    files[kv.Key] = kv.Value;

            // done with disk, wipe it
            WipeScratch();

            return new UndoEntry
            {
                Files = files,
                SharedTextures = sharedTex
            };
        }

        // recursively read all files in a directory into the dict
        static void SlurpDirectory(string root, string dir, Dictionary<string, byte[]> files)
        {
            foreach (var f in Directory.GetFiles(dir))
            {
                string rel = f.Substring(root.Length + 1).Replace('\\', '/');
                files[rel] = File.ReadAllBytes(f);
            }
            foreach (var d in Directory.GetDirectories(dir))
                SlurpDirectory(root, d, files);
        }

        // capture the real craft path so we can restore it later
        internal static void CaptureRealPersistence()
        {
            RealCurrentlyLoaded = Persistence.currentlyLoaded;
            RealIsTemp = Persistence.isTemp;
            RealCurrentRootFolder = Persistence.currentRootFolder;
            RealSaveAs = Persistence.saveAs;
            RealIsAutoSave = Persistence.isAutoSave;
            RealSavedTextureCount = Persistence.savedTextureCount;
            _realPersistenceValid = true;
            Melon<UndoMod>.Logger.Msg($"Real persistence: {RealCurrentlyLoaded}");
        }

        // force persistence back to real state — used before saves
        internal static void ForceRealPersistence()
        {
            if (!_realPersistenceValid) return;
            Persistence.currentlyLoaded = RealCurrentlyLoaded;
            Persistence.isTemp = RealIsTemp;
            Persistence.currentRootFolder = RealCurrentRootFolder;
            Persistence.saveAs = RealSaveAs;
            Persistence.isAutoSave = RealIsAutoSave;
            Persistence.savedTextureCount = RealSavedTextureCount;
        }

        static bool IsScratchPath(string path)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(ScratchDir)) return false;
            var parent = Path.GetDirectoryName(ScratchDir);
            return path.Replace('\\', '/').StartsWith(parent.Replace('\\', '/'));
        }

        // public version for patches to use
        internal static bool IsScratchPathPublic(string path) => IsScratchPath(path);

        // grab initial snapshot when the stack is empty
        internal static void TakeInitialSnapshot()
        {
            if (UndoStack.Count == 0)
            {
                CaptureRealPersistence();
                TakeSnapshot("initial");
            }
        }

        // --- undo / redo ---

        void Undo()
        {
            if (SnapshotPending)
            {
                SnapshotPending = false;
                TakeSnapshot("pre-undo");
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
            if (mgr == null || entry == null || entry.Files == null) return;

            try
            {
                IsRestoring = true;

                string prevLoaded = _realPersistenceValid ? RealCurrentlyLoaded : Persistence.currentlyLoaded;
                bool prevTemp = _realPersistenceValid ? RealIsTemp : Persistence.isTemp;
                string prevRoot = _realPersistenceValid ? RealCurrentRootFolder : Persistence.currentRootFolder;
                bool prevSaveAs = _realPersistenceValid ? RealSaveAs : Persistence.saveAs;
                bool prevIsAutoSave = _realPersistenceValid ? RealIsAutoSave : Persistence.isAutoSave;
                int prevTexCount = _realPersistenceValid ? RealSavedTextureCount : Persistence.savedTextureCount;
                Mode prevMode = mgr.Mode;

                // save camera state before LoadCraft nukes it
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

                // save blueprints before LoadCraft clears them
                var savedBlueprints = new List<Il2CppSystem.Collections.Generic.List<string>>();
                var bpToolPre = mgr.blueprintTool;
                if (bpToolPre != null && bpToolPre.blueprints != null)
                {
                    foreach (var bp in bpToolPre.blueprints)
                    {
                        if (bp == null) continue;
                        string serialized = bp.Serialize();
                        if (string.IsNullOrEmpty(serialized)) continue;
                        var lines = new Il2CppSystem.Collections.Generic.List<string>();
                        foreach (var line in serialized.Split('\n'))
                            lines.Add(line);
                        savedBlueprints.Add(lines);
                    }
                }

                // write all files from memory to scratch
                WipeScratch();
                foreach (var kv in entry.Files)
                {
                    string dest = Path.Combine(ScratchDir, kv.Key);
                    string dir = Path.GetDirectoryName(dest);
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllBytes(dest, kv.Value);
                }

                bool ok = mgr.LoadCraft(Path.Combine(ScratchDir, "data.txt"));

                // dont wipe scratch here — game may load images async
                // it gets wiped at the start of the next snapshot or restore

                if (ok)
                {
                    LoggerInstance.Msg($"Restored snapshot {CurrentIndex}");

                    // restore blueprints — LoadCraft wipes them but they're
                    // not part of the craft data, they're a separate system
                    if (savedBlueprints != null && savedBlueprints.Count > 0)
                    {
                        var bpTool = mgr.blueprintTool;
                        if (bpTool != null)
                        {
                            foreach (var bpData in savedBlueprints)
                                bpTool.LoadBluePrintFromData(bpData);
                        }
                    }

                    // keep the camera where it was
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

                    // fix persistence so saves go to the right place
                    Persistence.currentlyLoaded = prevLoaded;
                    Persistence.isTemp = prevTemp;
                    Persistence.currentRootFolder = prevRoot;
                    Persistence.saveAs = prevSaveAs;
                    Persistence.isAutoSave = prevIsAutoSave;
                    Persistence.savedTextureCount = prevTexCount;

                    // go back to paint mode if we were painting
                    if (prevMode != Mode.Edit)
                        MelonCoroutines.Start(DelayedModeRestore(prevMode));
                }
                else
                {
                    LoggerInstance.Error("LoadCraft failed during restore");
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

        // --- camera override (postfix on CECamera.LateUpdate) ---

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

        // --- helpers ---

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
            UndoStack.Clear();
            CurrentIndex = -1;
            SnapshotPending = false;
            _realPersistenceValid = false;
        }

        // wipe and recreate the single scratch folder
        static void WipeScratch()
        {
            try
            {
                if (Directory.Exists(ScratchDir))
                    Directory.Delete(ScratchDir, true);
            }
            catch { }
            Directory.CreateDirectory(ScratchDir);
        }

        static void ShowStatus(string text)
        {
            _statusText = text;
            _statusTimer = 1.8f;
        }

        // --- dynamic part module patching ---

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
