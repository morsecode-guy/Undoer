using System;
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

[assembly: MelonInfo(typeof(UndoMod.UndoMod), "Undoer", "1.0.0", "Morse Code Guy")]
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

        public override void OnUpdate()
        {
            if (!InCraftEditor) return;

            // flush pending snapshot after debounce
            if (SnapshotPending && Time.time - LastSnapshotTime >= SnapshotDelay)
            {
                SnapshotPending = false;
                TakeSnapshot("action");
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
                if (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed)
                    Redo();
                else
                    Undo();
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
            if (IsRestoring) return;
            LastSnapshotTime = Time.time;
            SnapshotPending = true;
        }

        // serialize the entire craft to disk right now
        internal static void TakeSnapshot(string reason)
        {
            if (IsRestoring) return;

            var mgr = CEManager.instance;
            if (mgr == null || mgr.craft == null) return;

            try
            {
                string name = $"undo_{DateTime.Now:yyyyMMdd_HHmmss_fff}.craft";
                string path = Path.Combine(UndoTempDir, name);

                Persistence.SerializeCraft(mgr.craft, path, false);

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

                string prevLoaded = Persistence.currentlyLoaded;
                bool prevTemp = Persistence.isTemp;

                bool ok = mgr.LoadCraft(Path.Combine(path, "data.txt"));

                if (ok)
                {
                    // keep the original save name so it doesnt overwrite undo files
                    Persistence.currentlyLoaded = prevLoaded;
                    Persistence.isTemp = prevTemp;
                    LoggerInstance.Msg($"Restored {Path.GetFileName(path)}");
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
            }
        }

        // -----------------------------------------------------------
        // helpers
        // -----------------------------------------------------------

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