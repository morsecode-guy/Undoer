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

[assembly: MelonInfo(typeof(UndoMod.UndoMod), "Undoer", "2.0.0", "Morse Code Guy")]
[assembly: MelonGame("Stonext Games", "Flyout")]

namespace UndoMod
{
    public class UndoMod : MelonMod
    {
        // state
        internal static readonly List<string> UndoStack = new();
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
        internal static float SnapshotDelay = 0.35f;

        // config
        static readonly int[] MemoryPresets = { 50, 100, 200, 500 };
        static int _presetIndex = 2;
        static int MaxUndoSteps => MemoryPresets[_presetIndex];

        // hud
        static string _statusText = "";
        static float _statusTimer;

        // camera override after restore — stays active until user
        // actively moves the camera (right-click orbit or scroll zoom)
        internal static bool CameraOverrideActive;
        static Vector3 _camPos;
        static Quaternion _camRot;
        static Vector3 _camPivot;
        static float _camZoom, _camOrthoSize, _camOrthoZoom;

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
        static bool _mouseDownOnUI;  // true if the click started on a UI element

        public override void OnUpdate()
        {
            if (!InCraftEditor) return;

            // flush pending snapshot after debounce
            if (SnapshotPending && Time.time - LastSnapshotTime >= SnapshotDelay)
            {
                SnapshotPending = false;
                TakeSnapshot("action");
            }

            // detect paint stroke end: mouse released while in paint mode
            bool inPaintMode = CEManager.instance != null
                && CEManager.instance.Mode == Il2CppCraftEditor.Mode.Paint;

            if (inPaintMode)
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                bool mouseDown = mouse != null && mouse.leftButton.isPressed;

                // track whether this click started on the UI
                if (mouseDown && !_wasMouseDown)
                {
                    var es = UnityEngine.EventSystems.EventSystem.current;
                    _mouseDownOnUI = es != null && es.IsPointerOverGameObject();
                }

                // only snapshot if the stroke was on the 3D viewport, not UI
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

            // F7 cycles undo memory size
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

        // queue a snapshot after the debounce delay
        internal static void RequestSnapshot()
        {
            if (IsRestoring || Time.time < RestoreCooldownUntil) return;
            LastSnapshotTime = Time.time;
            SnapshotPending = true;
        }

        // serialize the entire craft to disk right now
        internal static void TakeSnapshot(string reason)
        {
            if (IsRestoring || Time.time < RestoreCooldownUntil) return;

            var mgr = CEManager.instance;
            if (mgr == null || mgr.craft == null) return;

            try
            {
                string name = $"undo_{DateTime.Now:yyyyMMdd_HHmmss_fff}.craft";
                string path = Path.Combine(UndoTempDir, name);

                // autosave=true uses the games faster serialization path
                Persistence.SerializeCraft(mgr.craft, path, true);

                // trim redo history
                if (CurrentIndex < UndoStack.Count - 1)
                    for (int i = UndoStack.Count - 1; i > CurrentIndex; i--)
                    {
                        TryDelete(UndoStack[i]);
                        UndoStack.RemoveAt(i);
                    }

                UndoStack.Add(path);
                CurrentIndex = UndoStack.Count - 1;

                // cap size
                while (UndoStack.Count > MaxUndoSteps)
                {
                    TryDelete(UndoStack[0]);
                    UndoStack.RemoveAt(0);
                    CurrentIndex--;
                }

                Melon<UndoMod>.Logger.Msg($"[{CurrentIndex}] {reason}: {name}");
            }
            catch (Exception ex)
            {
                Melon<UndoMod>.Logger.Error($"Snapshot failed: {ex}");
            }
        }

        // take one snapshot if theres nothing in the stack yet
        internal static void TakeInitialSnapshot()
        {
            if (UndoStack.Count == 0)
                TakeSnapshot("initial");
        }

        // -----------------------------------------------------------
        // undo / redo
        // -----------------------------------------------------------

        void Undo()
        {
            // flush any pending snapshot first
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

        void Restore(string path)
        {
            var mgr = CEManager.instance;
            if (mgr == null) return;

            if (!Directory.Exists(path))
            {
                LoggerInstance.Error($"Snapshot missing: {path}");
                return;
            }

            try
            {
                IsRestoring = true;

                // save persistence state before load overwrites it
                string prevLoaded = Persistence.currentlyLoaded;
                bool prevTemp = Persistence.isTemp;
                string prevRoot = Persistence.currentRootFolder;

                // remember current mode so we can re-enter after LoadCraft
                // (LoadCraft destroys/recreates GameObjects, breaking
                // TexturePaintMode's references to paintable surfaces)
                Mode prevMode = mgr.Mode;

                // save camera state: full transform + controller fields
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

                bool ok = mgr.LoadCraft(Path.Combine(path, "data.txt"));

                if (ok)
                {
                    LoggerInstance.Msg($"Restored {Path.GetFileName(path)}");

                    // keep forcing camera until the user moves it
                    _camPos = savedPos;
                    _camRot = savedRot;
                    _camPivot = savedPivot;
                    _camZoom = savedZoom;
                    _camOrthoSize = savedOrthoSize;
                    _camOrthoZoom = savedOrthoZoom;
                    CameraOverrideActive = true;

                    // immediately seed the game's internal state so the
                    // next CECamera.Update() starts from our values
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

                    // delay restoring persistence so texture coroutines
                    // can finish loading PNGs from the snapshot directory
                    MelonCoroutines.Start(
                        DelayedRestorePersistence(prevLoaded, prevTemp, prevRoot));

                    // re-enter the previous mode so paint targets etc get
                    // re-initialised against the newly loaded GameObjects
                    if (prevMode != Mode.Edit)
                    {
                        mgr.SetMode(Mode.Edit);
                        mgr.SetMode(prevMode);
                    }
                }
                else
                {
                    LoggerInstance.Error($"LoadCraft failed for {path}");
                }
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Restore failed: {ex}");
            }
            finally
            {
                IsRestoring = false;
                // suppress callbacks from async load (paintable coroutines etc)
                RestoreCooldownUntil = Time.time + 1.0f;
                SnapshotPending = false;
            }
        }

        // camera override is applied via Harmony postfix on
        // CECamera.LateUpdate (in FuselagePatches.cs) to guarantee
        // it runs AFTER the game finishes computing camera state.
        // See Patch_CECamLateUpdate.

        /// <summary>
        /// Called by Harmony postfix on CECamera.LateUpdate.
        /// </summary>
        internal static void ApplyCameraOverride(CECamera cam)
        {
            if (!CameraOverrideActive) return;

            // cancel override if user is actively moving the camera
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

        // waits for texture loading coroutines to finish before
        // restoring the persistence state back to the real craft
        static IEnumerator DelayedRestorePersistence(
            string loaded, bool temp, string root)
        {
            yield return null;
            yield return null;
            yield return new WaitForSeconds(0.5f);

            Persistence.currentlyLoaded = loaded;
            Persistence.isTemp = temp;
            Persistence.currentRootFolder = root;
        }

        void ClearHistory()
        {
            foreach (var p in UndoStack) TryDelete(p);
            UndoStack.Clear();
            CurrentIndex = -1;
            SnapshotPending = false;
        }

        static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        static void ShowStatus(string text)
        {
            _statusText = text;
            _statusTimer = 1.8f;
        }

        // -----------------------------------------------------------
        // dynamic part module patching
        // -----------------------------------------------------------
        // patches Set* methods on part modules so property panel
        // edits (drum mag ammo, engine bore, fuel fill, etc) get
        // caught without needing a patch class for each one

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