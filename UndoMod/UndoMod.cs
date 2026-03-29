using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
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
        static string _scratchDirA, _scratchDirB; // two alternating dirs for background slurp
        static bool _useScratchA = true;
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

        // suppress Set* snapshots until the initial snapshot is done
        // (parts fire Set* during loading which pollutes the stack)
        internal static bool InitialSnapshotDone;

        // snapshot debounce
        internal static float LastSnapshotTime;
        internal static bool SnapshotPending;
        internal static float SnapshotDelay = 0.5f;

        // background snapshot threading
        static Task<UndoEntry> _bgTask;

        // undo memory config (F7 to cycle)
        static readonly int[] MemoryPresets = { 50, 100, 200, 500 };
        static int _presetIndex = 2;
        static int MaxUndoSteps => MemoryPresets[_presetIndex];

        // hud toast
        static string _statusText = "";
        static float _statusTimer;

        // camera override — keeps camera still after restore
        internal static bool CameraOverrideActive;
        static int _camOverrideFrames;
        static Vector3 _camPos;
        static Quaternion _camRot;
        static Vector3 _camPivot;
        static float _camZoom, _camOrthoSize, _camOrthoZoom;

        // --- init ---

        public override void OnInitializeMelon()
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Flyout", "UndoMod");

            _scratchDirA = Path.Combine(baseDir, "scratch_a.craft");
            _scratchDirB = Path.Combine(baseDir, "scratch_b.craft");
            ScratchDir = _scratchDirA;

            foreach (var d in new[] { _scratchDirA, _scratchDirB })
            {
                if (Directory.Exists(d))
                    Directory.Delete(d, true);
                Directory.CreateDirectory(d);
            }

            LoggerInstance.Msg("Undoer ready  |  in-memory snapshots (threaded)");
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
            if (SnapshotPending && InitialSnapshotDone && Time.time - LastSnapshotTime >= SnapshotDelay)
            {
                SnapshotPending = false;
                TakeSnapshot("action");
            }

            // poll for completed background snapshot
            if (_bgTask != null && _bgTask.IsCompleted)
            {
                try
                {
                    var entry = _bgTask.Result;
                    if (entry != null)
                        PushEntry(entry, "action");
                }
                catch (Exception ex)
                {
                    Melon<UndoMod>.Logger.Error($"Background snapshot failed: {ex}");
                }
                _bgTask = null;
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
            if (!InitialSnapshotDone) return; // suppress during loading
            LastSnapshotTime = Time.time;
            SnapshotPending = true;
        }

        // take a snapshot right now — called by structural operations
        // (place, delete, duplicate, etc.) so the post-op state is always
        // a separate undo step from any settings changes that follow
        internal static void TakeSnapshotImmediate()
        {
            if (IsRestoring || Time.time < RestoreCooldownUntil) return;

            // drain any in-flight background snapshot first
            DrainBgTask();

            // flush any pending debounced snapshot first (captures pre-op state)
            if (SnapshotPending)
            {
                SnapshotPending = false;
                TakeSnapshotSync("pre-action");
            }

            TakeSnapshotSync("action");
        }

        // serialize the whole craft to disk, then slurp into memory
        // on a background thread so the main thread isn't blocked by I/O
        internal static void TakeSnapshot(string reason)
        {
            if (IsRestoring || Time.time < RestoreCooldownUntil) return;

            var mgr = CEManager.instance;
            if (mgr == null || mgr.craft == null) return;

            // if a bg task is still running, wait for it first
            DrainBgTask();

            try
            {
                // --- main thread: serialize to disk ---
                var serializeDir = SerializeToDisk(mgr, out var prevTex);
                if (serializeDir == null) return;

                // kick off background slurp
                var capturedDir = serializeDir;
                var capturedPrev = prevTex;
                _bgTask = Task.Run(() => SlurpIntoEntry(capturedDir, capturedPrev));
            }
            catch (Exception ex)
            {
                Melon<UndoMod>.Logger.Error($"Snapshot failed: {ex}");
            }
        }

        // synchronous version — used by TakeSnapshotImmediate where we
        // need the entry on the stack before the next operation
        static void TakeSnapshotSync(string reason)
        {
            if (IsRestoring || Time.time < RestoreCooldownUntil) return;

            var mgr = CEManager.instance;
            if (mgr == null || mgr.craft == null) return;

            try
            {
                var serializeDir = SerializeToDisk(mgr, out var prevTex);
                if (serializeDir == null) return;

                var entry = SlurpIntoEntry(serializeDir, prevTex);
                if (entry != null)
                    PushEntry(entry, reason);
            }
            catch (Exception ex)
            {
                Melon<UndoMod>.Logger.Error($"Snapshot failed: {ex}");
            }
        }

        // add a completed entry to the undo stack
        static void PushEntry(UndoEntry entry, string reason)
        {
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

        // wait for any in-flight background task and push its result
        static void DrainBgTask()
        {
            if (_bgTask == null) return;
            try
            {
                var entry = _bgTask.Result; // blocks until done
                if (entry != null)
                    PushEntry(entry, "action");
            }
            catch (Exception ex)
            {
                Melon<UndoMod>.Logger.Error($"Background snapshot failed: {ex}");
            }
            _bgTask = null;
        }

        // main-thread part: serialize craft to disk, returns the scratch dir used
        static string SerializeToDisk(CEManager mgr, out Dictionary<string, byte[]> prevTex)
        {
            string savedLoaded = _realPersistenceValid ? RealCurrentlyLoaded : Persistence.currentlyLoaded;
            bool savedTemp = _realPersistenceValid ? RealIsTemp : Persistence.isTemp;
            string savedRoot = _realPersistenceValid ? RealCurrentRootFolder : Persistence.currentRootFolder;
            bool savedSaveAs = _realPersistenceValid ? RealSaveAs : Persistence.saveAs;
            bool savedIsAutoSave = _realPersistenceValid ? RealIsAutoSave : Persistence.isAutoSave;
            int savedTexCount = _realPersistenceValid ? RealSavedTextureCount : Persistence.savedTextureCount;

            bool isPaint = mgr.Mode == Il2CppCraftEditor.Mode.Paint;

            prevTex = null;
            if (!isPaint)
            {
                for (int i = UndoStack.Count - 1; i >= 0; i--)
                {
                    if (UndoStack[i].SharedTextures != null && UndoStack[i].SharedTextures.Count > 0)
                    { prevTex = UndoStack[i].SharedTextures; break; }
                }
            }

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

            // pick which scratch dir to use (alternate so bg reads don't conflict)
            string useDir = _useScratchA ? _scratchDirA : _scratchDirB;
            _useScratchA = !_useScratchA;
            ScratchDir = useDir; // restore also uses ScratchDir
            WipeScratch();

            try
            {
                Persistence.SerializeCraft(mgr.craft, useDir, true);
            }
            finally
            {
                if (modified != null)
                    foreach (var p in modified)
                        p.hasBeenModified = true;

                Persistence.currentlyLoaded = savedLoaded;
                Persistence.isTemp = savedTemp;
                Persistence.currentRootFolder = savedRoot;
                Persistence.saveAs = savedSaveAs;
                Persistence.isAutoSave = savedIsAutoSave;
                Persistence.savedTextureCount = savedTexCount;
            }

            string dataFile = Path.Combine(useDir, "data.txt");
            return File.Exists(dataFile) ? useDir : null;
        }

        // background-safe: reads files from disk into an UndoEntry
        static UndoEntry SlurpIntoEntry(string scratchDir, Dictionary<string, byte[]> prevTex)
        {
            var files = new Dictionary<string, byte[]>();
            SlurpDirectory(scratchDir, scratchDir, files);

            var texFiles = new Dictionary<string, byte[]>();
            foreach (var kv in files)
                if (kv.Key.StartsWith("Textures/") || kv.Key.StartsWith("Textures\\"))
                    texFiles[kv.Key] = kv.Value;
            var sharedTex = texFiles.Count > 0 ? texFiles : prevTex;

            if (prevTex != null && texFiles.Count == 0)
                foreach (var kv in prevTex)
                    files[kv.Key] = kv.Value;

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

        // grab initial snapshot once loading is done
        internal static void TakeInitialSnapshot()
        {
            // clear any garbage snapshots from Set* calls during loading
            UndoStack.Clear();
            CurrentIndex = -1;
            SnapshotPending = false;
            CaptureRealPersistence();
            TakeSnapshot("initial");
            InitialSnapshotDone = true;
        }

        // --- undo / redo ---

        void Undo()
        {
            DrainBgTask();

            if (SnapshotPending)
            {
                SnapshotPending = false;
                TakeSnapshotSync("pre-undo");
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
            DrainBgTask();

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

                // close any open sub-editors before loading so they don't
                // get stuck open after the craft is replaced
                try { mgr.QuitCSE(); } catch { }
                try { mgr.QuitWingEditor(); } catch { }

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

                // destroy any floating/picked parts so they dont survive the
                // LoadCraft call (fixes subassembly undo leaving ghost parts)
                try
                {
                    var floating = mgr.floatingParts;
                    if (floating != null)
                    {
                        for (int i = floating.childCount - 1; i >= 0; i--)
                            UnityEngine.Object.Destroy(floating.GetChild(i).gameObject);
                    }
                    mgr.pickedPart = null;
                    mgr.pickedParts = null;
                }
                catch { }

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
                    _camOverrideFrames = 3;

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

                    // force back to Edit mode so the user can select parts
                    mgr.SetMode(Mode.Edit);
                    mgr.Target = null;
                    mgr.ClearOutlines();
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

            // only override for a few frames after restore, then stop
            _camOverrideFrames--;
            if (_camOverrideFrames <= 0)
            {
                CameraOverrideActive = false;
                return;
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



        void ClearHistory()
        {
            DrainBgTask();
            UndoStack.Clear();
            CurrentIndex = -1;
            SnapshotPending = false;
            _realPersistenceValid = false;
            InitialSnapshotDone = false;
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

        // patches every Set* method on every PartModule subclass
        // so literally any setting change is undoable :3
        void PatchPartModuleSetMethods()
        {
            var harmony = HarmonyInstance;
            var postfix = new HarmonyMethod(
                typeof(GenericSetPostfix).GetMethod(
                    nameof(GenericSetPostfix.Postfix),
                    BindingFlags.Public | BindingFlags.Static));

            // skip methods that arent editor settings — runtime/physics/loading stuff
            var skipSuffixes = new[] {
                "NextFrame", "Internal", "Velocity", "Transform", "Mesh",
                "Vertices", "Connected", "OnFire", "Master", "Parent",
                "Previous", "AsRoot", "Effects", "Preview", "CircuitEnergy",
                "Collider", "MirrorHandles", "MirrorHandlePositions",
                "Script", "Clip",
            };

            // find the PartModule base type from the game assembly
            var partModuleType = typeof(Il2Cpp.FuelTank).BaseType; // PartModule

            // only scan the game assembly, not everything in the appdomain
            var gameAsm = partModuleType.Assembly;
            Type[] allTypes;
            try { allTypes = gameAsm.GetTypes(); }
            catch { LoggerInstance.Error("Cant get types from game assembly"); return; }

            int ok = 0, fail = 0;

            foreach (var type in allTypes)
            {
                if (type.IsAbstract || type.IsGenericType) continue;

                // must inherit from PartModule (directly or indirectly)
                bool isPartModule = false;
                try
                {
                    var bt = type.BaseType;
                    while (bt != null)
                    {
                        if (bt == partModuleType) { isPartModule = true; break; }
                        bt = bt.BaseType;
                    }
                }
                catch { continue; }

                if (!isPartModule) continue;

                PatchSetMethods(harmony, postfix, type, skipSuffixes, ref ok, ref fail);
            }

            // also patch types that arent PartModules but have editor settings
            var extraTypes = new Type[]
            {
                typeof(Il2Cpp.PartMaterials),
                typeof(Il2Cpp.EditableFuselage),
                typeof(Il2Cpp.ProcWing2),
                typeof(Il2Cpp.WingEdge),
                typeof(Il2Cpp.EditableRotor),
                typeof(Il2Cpp.CustomAxis),
            };

            foreach (var type in extraTypes)
                PatchSetMethods(harmony, postfix, type, skipSuffixes, ref ok, ref fail);

            // patch Select* methods on ProceduralProp (blade/cone type switching
            // uses "Select" prefix instead of "Set")
            try
            {
                var propType = typeof(Il2Cpp.ProceduralProp);
                foreach (var name in new[] { "SelectBlade", "SelectCone" })
                {
                    var m = propType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
                    if (m != null)
                    {
                        try { harmony.Patch(m, postfix: postfix); ok++; }
                        catch { fail++; }
                    }
                }
            }
            catch { }

            // patch GunSolver property setters for tracer rate and muzzle velocity
            // (these have no Set* wrappers — CannonPanel sets them via property setters)
            try
            {
                var gunSolverType = typeof(Il2Cpp.GunSolver);
                foreach (var propName in new[] { "tracerRate", "muzzleVelocity" })
                {
                    var prop = gunSolverType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                    if (prop?.SetMethod != null)
                    {
                        try { harmony.Patch(prop.SetMethod, postfix: postfix); ok++; }
                        catch { fail++; }
                    }
                }
            }
            catch { }

            // patch SmokeEmitter property setters — has zero Set* methods
            // color changes are handled by SmokeEmitterPanel.SetColor patch,
            // but rate is set directly via property setter from slider closure
            try
            {
                var smokeType = typeof(Il2Cpp.SmokeEmitter);
                foreach (var propName in new[] { "color", "rate" })
                {
                    var prop = smokeType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                    if (prop?.SetMethod != null)
                    {
                        try { harmony.Patch(prop.SetMethod, postfix: postfix); ok++; }
                        catch { fail++; }
                    }
                }
            }
            catch { }

            LoggerInstance.Msg($"Patched {ok} part module Set methods ({fail} failed)");
        }

        void PatchSetMethods(HarmonyLib.Harmony harmony, HarmonyMethod postfix,
            Type type, string[] skipSuffixes, ref int ok, ref int fail)
        {
            try
            {
                var methods = type
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name.StartsWith("Set") && !m.IsAbstract && !m.IsGenericMethod);

                foreach (var m in methods)
                {
                    // skip runtime/loading methods
                    bool skip = false;
                    foreach (var suffix in skipSuffixes)
                        if (m.Name.Length > 3 && m.Name.Substring(3).Contains(suffix))
                        { skip = true; break; }
                    if (skip) continue;

                    try
                    {
                        harmony.Patch(m, postfix: postfix);
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        LoggerInstance.Warning($"Cant patch {type.Name}.{m.Name}: {ex.Message}");
                        fail++;
                    }
                }
            }
            catch { }
        }
    }
}
