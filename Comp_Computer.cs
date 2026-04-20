using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimComputers
{
    public class Comp_Computer : ThingComp
    {
        private ComputerState state = ComputerState.Off;
        private ScreenBuffer screen;
        private OCApi ocApi;
        private List<string> debugLog = new List<string>();

        private int bootTicksLeft = 0;
        private const int BootTicks = 60;

        // ── Persistent writable VFS ──────────────────────────────────────────
        // ROM files are re-loaded each boot from disk (read-only source of truth).
        // Files created/modified by OpenOS at runtime are stored here and survive
        // reboots and save/load cycles.
        // Key   = normalised VFS path (no leading /)
        // Value = UTF-8 bytes encoded as Base64 for XML serialisation
        private Dictionary<string, string> persistentVfsB64 =
            new Dictionary<string, string>();

        // ── Pending reboot flag ──────────────────────────────────────────────
        // Set by OCApi when computer.shutdown(true) is called from Lua.
        // Checked each game tick to schedule a clean reboot via the game thread.
        private volatile bool _pendingReboot = false;
        private volatile bool _pendingShutdown = false;

        public CompProperties_Computer Props => (CompProperties_Computer)props;
        private CompPowerTrader PowerComp => parent.TryGetComp<CompPowerTrader>();
        public bool HasPower => PowerComp == null || PowerComp.PowerOn;

        public ComputerState State => state;
        public ScreenBuffer Screen => screen;
        public List<string> DebugLog => debugLog;
        public long RamUsed => ocApi?.RamUsed ?? 0L;
        public long RomUsed => ocApi?.RomUsed ?? 0L;
        public string LuaVerString => "NLua/5.3";

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            screen = new ScreenBuffer(Props.screenWidth, Props.screenHeight);
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

            // ── Handle reboot/shutdown requested by Lua via computer.shutdown() ──
            if (_pendingShutdown)
            {
                _pendingShutdown = false;
                _pendingReboot = false;
                Log("[tick] Lua requested shutdown");
                FlushVfsFromApi();
                ForceOff("Lua shutdown");
                return;
            }
            if (_pendingReboot)
            {
                _pendingReboot = false;
                Log("[tick] Lua requested reboot");
                FlushVfsFromApi();
                ForceOff("Lua reboot");
                // Start fresh boot cycle
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
            {
                ocApi?.Tick();
            }
        }

        // Called by OCApi when computer.shutdown(reboot) fires from Lua thread
        public void RequestShutdown(bool reboot)
        {
            if (reboot)
                _pendingReboot = true;
            else
                _pendingShutdown = true;
        }

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
            FlushVfsFromApi();
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
                var persistentVfs = DeserialiseVfs();
                ocApi = new OCApi(screen, Props, debugLog, parent, persistentVfs, this);
                Log("OCApi created, starting BIOS");
                ocApi.StartBios();
                Log("InitLua OK");
            }
            catch (Exception ex)
            {
                state = ComputerState.Error;
                Log("Lua init error: " + ex.Message);
                Log("Lua init stack: " + ex.StackTrace?.Substring(0, 500));
                Verse.Log.Error("[RimComputers] InitLua: " + ex);
                screen.Println("INIT ERROR: " + ex.Message);
            }
        }

        private void ShutdownLua()
        {
            ocApi?.Dispose();
            ocApi = null;
        }

        // Snapshot writable VFS from the running OCApi into persistentVfsB64.
        private void FlushVfsFromApi()
        {
            if (ocApi == null) return;
            var writeable = ocApi.GetWriteableVfsSnapshot();
            persistentVfsB64.Clear();
            foreach (var kv in writeable)
                persistentVfsB64[kv.Key] = Convert.ToBase64String(kv.Value);
            Log($"[vfs] flushed {persistentVfsB64.Count} writable entries");
        }

        private Dictionary<string, byte[]> DeserialiseVfs()
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in persistentVfsB64)
            {
                try { result[kv.Key] = Convert.FromBase64String(kv.Value); }
                catch { /* corrupt entry — skip */ }
            }
            return result;
        }

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
                if (debugLog.Count > 2000) debugLog.RemoveAt(0);
            }
        }

        public void SafeRestart()
        {
            TryPowerOff();
            TryPowerOn();
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            yield return new Command_Action
            {
                defaultLabel = "Toggle Screen",
                defaultDesc = "Open or close terminal.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", true),
                action = () =>
                {
                    var existing = Find.WindowStack.WindowOfType<Dialog_ComputerScreen>();
                    if (existing != null)
                        existing.Close();
                    else
                        Find.WindowStack.Add(new Dialog_ComputerScreen(this));
                }
            };
            bool isOn = state != ComputerState.Off;
            yield return new Command_Action
            {
                defaultLabel = isOn ? "Power Off" : "Power On",
                defaultDesc = "",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/Halt", true),
                action = () => { if (isOn) TryPowerOff(); else TryPowerOn(); }
            };
            yield return new Command_Action
            {
                defaultLabel = "Debug Log",
                defaultDesc = "Open debug console.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/ViewQuest", true),
                action = () => Find.WindowStack.Add(new Dialog_ComputerDebug(this))
            };
        }

        public override string CompInspectStringExtra()
            => $"State: {state}  Lua: {LuaVerString}\nRAM: {RamUsed / 1024}KB  ROM: {RomUsed / 1024}KB";

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref state, "rcState", ComputerState.Off);

            // Flush before saving so latest changes are included
            if (Scribe.mode == LoadSaveMode.Saving)
                FlushVfsFromApi();

            Scribe_Collections.Look(ref persistentVfsB64, "rcVfs",
                LookMode.Value, LookMode.Value);

            if (persistentVfsB64 == null)
                persistentVfsB64 = new Dictionary<string, string>();
        }
    }
}