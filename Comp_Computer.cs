using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimComputers
{
    public class Comp_Computer : ThingComp
    {
        private ComputerState state = ComputerState.Off;
        private ScreenBuffer  screen;
        private OCApi         ocApi;
        private List<string>  debugLog = new List<string>();

        private int   bootTicksLeft = 0;
        private const int BootTicks = 60;

        // ── Internet card ────────────────────────────────────────────────────
        private bool internetEnabled = false;

        // ── Legacy XML-based VFS (for migration only) ────────────────────────
        // New installs use HddFolderPath. Old saves have data here; on first boot
        // we migrate it to the folder and clear this dict.
        private Dictionary<string, string> persistentVfsB64 =
            new Dictionary<string, string>();

        // ── Reboot/shutdown flags (set from Lua thread) ──────────────────────
        private volatile bool _pendingReboot   = false;
        private volatile bool _pendingShutdown = false;

        // ── Public accessors ─────────────────────────────────────────────────
        public CompProperties_Computer Props => (CompProperties_Computer)props;
        private CompPowerTrader PowerComp => parent.TryGetComp<CompPowerTrader>();
        public bool HasPower  => PowerComp == null || PowerComp.PowerOn;
        public ComputerState State => state;
        public ScreenBuffer  Screen => screen;
        public List<string>  DebugLog => debugLog;
        public long RamUsed => ocApi?.RamUsed ?? 0L;
        public long RomUsed => ocApi?.RomUsed ?? 0L;
        public string LuaVerString => "NLua/5.3";

        // ── HDD folder ───────────────────────────────────────────────────────
        // Stored on the real file system alongside the save game.
        // Path: <SaveFolder>/RimComputers/<ThingID>/hdd/
        private string HddFolderPath =>
            Path.Combine(
                GenFilePaths.SaveDataFolderPath,
                "RimComputers",
                parent.ThingID ?? "unknown",
                "hdd");

        // ════════════════════════════════════════════════════════════════════
        // Life-cycle
        // ════════════════════════════════════════════════════════════════════

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            screen = new ScreenBuffer(Props.screenWidth, Props.screenHeight);
            // Ensure the HDD folder exists
            Directory.CreateDirectory(HddFolderPath);
            if (state == ComputerState.Running)
                InitLua();
        }

        public override void CompTick()
        {
            base.CompTick();

            if (!HasPower && state == ComputerState.Running)
            {
                ForceOff("Power lost");
                return;
            }

            if (_pendingShutdown)
            {
                _pendingShutdown = false;
                _pendingReboot   = false;
                Log("[tick] Lua requested shutdown");
                FlushHdd();
                ForceOff("Lua shutdown");
                return;
            }
            if (_pendingReboot)
            {
                _pendingReboot = false;
                Log("[tick] Lua requested reboot");
                FlushHdd();
                ForceOff("Lua reboot");
                state = ComputerState.Booting;
                bootTicksLeft = BootTicks;
                screen.Clear();
                screen.Println("Rebooting...");
                return;
            }

            if (state == ComputerState.Booting)
            {
                if (--bootTicksLeft <= 0) FinishBoot();
                return;
            }
            if (state == ComputerState.Running)
                ocApi?.Tick();
        }

        public void RequestShutdown(bool reboot)
        {
            if (reboot) _pendingReboot   = true;
            else        _pendingShutdown = true;
        }

        // ════════════════════════════════════════════════════════════════════
        // Power
        // ════════════════════════════════════════════════════════════════════

        public void TryPowerOn()
        {
            if (state != ComputerState.Off && state != ComputerState.Error) return;
            if (!HasPower) { Log("No power"); return; }
            state = ComputerState.Booting;
            bootTicksLeft = BootTicks;
            screen.Clear();
            screen.Println("RimComputers BIOS v1.0");
            screen.Println("NLua 5.3");
            screen.Println("Booting...");
            Log("Boot started");
        }

        public void TryPowerOff()
        {
            if (state == ComputerState.Off) return;
            FlushHdd();
            ForceOff("User shutdown");
        }

        private void ForceOff(string reason)
        {
            Log("Shutdown: " + reason);
            ShutdownLua();
            state = ComputerState.Off;
            screen.Clear();
            screen.Println("-- off --");
        }

        private void FinishBoot()
        {
            Log("FinishBoot");
            InitLua();
            if (state != ComputerState.Error)
            {
                state = ComputerState.Running;
                Log("State -> Running");
            }
        }

        private void InitLua()
        {
            try
            {
                Log("InitLua start");
                screen.Clear();

                // Migrate old XML-based VFS to HDD folder on first boot after update
                Dictionary<string, byte[]> legacyMigration = null;
                if (persistentVfsB64 != null && persistentVfsB64.Count > 0)
                {
                    legacyMigration = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in persistentVfsB64)
                    {
                        try { legacyMigration[kv.Key] = Convert.FromBase64String(kv.Value); }
                        catch { }
                    }
                    persistentVfsB64.Clear(); // migration done — don't re-use
                }

                ocApi = new OCApi(screen, Props, debugLog, parent,
                                  hddFolderPath:     HddFolderPath,
                                  legacyVfsMigration: legacyMigration,
                                  compComp:           this);

                if (internetEnabled) ocApi.SetInternetEnabled(true);

                Log("OCApi created, starting BIOS");
                ocApi.StartBios();
                Log("InitLua OK");
            }
            catch (Exception ex)
            {
                state = ComputerState.Error;
                Log("Lua init error: " + ex.Message);
                Log("Lua init stack: " + ex.StackTrace?.Substring(0, Math.Min(500, ex.StackTrace?.Length ?? 0)));
                Verse.Log.Error("[RimComputers] InitLua: " + ex);
                screen.Println("INIT ERROR: " + ex.Message);
            }
        }

        private void ShutdownLua()
        {
            ocApi?.Dispose();
            ocApi = null;
        }

        /// <summary>Flush HDD VFS to disk folder.</summary>
        private void FlushHdd()
        {
            ocApi?.FlushHddToFolder();
        }

        // ════════════════════════════════════════════════════════════════════
        // Public helpers
        // ════════════════════════════════════════════════════════════════════

        public string ExecLua(string code)
        {
            if (ocApi == null) return "[no runtime]";
            try { ocApi.ExecuteLua(code); return "ok"; }
            catch (Exception ex) { return "Error: " + ex.Message; }
        }

        public void PushKey(char ch, KeyCode keyCode)
        {
            if (ocApi == null || state != ComputerState.Running) return;
            ocApi.PushKeySignal(ch, keyCode);
        }

        public void PushClipboard(string text)
        {
            if (ocApi == null || state != ComputerState.Running) return;
            ocApi.PushClipboardSignal(text);
        }

        public void Log(string msg)
        {
            lock (debugLog)
            {
                debugLog.Add("[" + (Find.TickManager?.TicksGame ?? 0) + "] " + msg);
                if (debugLog.Count > 10000) debugLog.RemoveAt(0);
            }
        }

        public void SafeRestart()
        {
            TryPowerOff();
            TryPowerOn();
        }

        // ════════════════════════════════════════════════════════════════════
        // Gizmos
        // ════════════════════════════════════════════════════════════════════

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            // ── Toggle screen ─────────────────────────────────────────────
            yield return new Command_Action
            {
                defaultLabel = "Toggle Screen",
                defaultDesc  = "Open or close the terminal window.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", true),
                action = () =>
                {
                    var existing = Find.WindowStack.WindowOfType<Dialog_ComputerScreen>();
                    if (existing != null) existing.Close();
                    else Find.WindowStack.Add(new Dialog_ComputerScreen(this));
                }
            };

            // ── Power on/off ──────────────────────────────────────────────
            bool isOn = state != ComputerState.Off;
            yield return new Command_Action
            {
                defaultLabel = isOn ? "Power Off" : "Power On",
                defaultDesc  = "",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/Halt", true),
                action = () => { if (isOn) TryPowerOff(); else TryPowerOn(); }
            };

            // ── Internet card ─────────────────────────────────────────────
            yield return new Command_Action
            {
                defaultLabel = internetEnabled ? "Internet: ON" : "Internet: OFF",
                defaultDesc  = internetEnabled
                    ? "Internet card enabled. Click to disable."
                    : "Internet card disabled. Click to enable (wget, pastebin, MineOS installer…)",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/AttackMelee", true),
                action = () =>
                {
                    internetEnabled = !internetEnabled;
                    ocApi?.SetInternetEnabled(internetEnabled);
                    Log($"[gizmo] Internet: {internetEnabled}");
                }
            };

            // ── HDD folder ────────────────────────────────────────────────
            yield return new Command_Action
            {
                defaultLabel = "Open HDD Folder",
                defaultDesc  = $"Open the HDD data folder in Explorer:\n{HddFolderPath}",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/ViewQuest", true),
                action = () =>
                {
                    Directory.CreateDirectory(HddFolderPath);
                    System.Diagnostics.Process.Start(HddFolderPath);
                }
            };

            // ── Debug log ─────────────────────────────────────────────────
            yield return new Command_Action
            {
                defaultLabel = "Debug Log",
                defaultDesc  = "Open the debug console.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/ViewQuest", true),
                action = () => Find.WindowStack.Add(new Dialog_ComputerDebug(this))
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // Inspect string
        // ════════════════════════════════════════════════════════════════════

        public override string CompInspectStringExtra()
        {
            string hddInfo = "";
            try
            {
                if (Directory.Exists(HddFolderPath))
                {
                    int fc = Directory.GetFiles(HddFolderPath, "*", SearchOption.AllDirectories).Length;
                    hddInfo = $"  HDD: {fc} files";
                }
            }
            catch { }
            return $"State: {state}  Lua: {LuaVerString}\n" +
                   $"RAM: {RamUsed/1024}KB  ROM: {RomUsed/1024}KB  " +
                   $"Net: {(internetEnabled?"ON":"OFF")}{hddInfo}";
        }

        // ════════════════════════════════════════════════════════════════════
        // Save/Load
        // ════════════════════════════════════════════════════════════════════

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref state,           "rcState",           ComputerState.Off);
            Scribe_Values.Look(ref internetEnabled, "rcInternetEnabled", false);

            // Flush HDD to disk before saving (so the folder is up-to-date)
            if (Scribe.mode == LoadSaveMode.Saving)
                FlushHdd();

            // Keep the old XML VFS dict around only for migration of old saves.
            // New saves will have an empty dict here.
            Scribe_Collections.Look(ref persistentVfsB64, "rcVfs",
                LookMode.Value, LookMode.Value);

            if (persistentVfsB64 == null)
                persistentVfsB64 = new Dictionary<string, string>();
        }
    }
}
