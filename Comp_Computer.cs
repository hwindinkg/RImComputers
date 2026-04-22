using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimComputers
{
    // ════════════════════════════════════════════════════════════════════════════
    // Component slot definitions (expandable for future hardware tiers)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Represents the configuration of a single computer.</summary>
    public class ComputerHardware : IExposable
    {
        // Installed component tiers (0 = empty slot)
        public int cpuTier     = 1;   // 1-3
        public int ramTier     = 1;   // 1-3
        public int gpuTier     = 1;   // 1-3
        public int hddCount    = 1;   // 1-3 (number of HDD slots filled)
        public int expansionSlots = 2; // internet card, redstone, etc.

        // Labels shown in gizmo
        public string CpuLabel  => $"CPU T{cpuTier}";
        public string GpuLabel  => $"GPU T{gpuTier} ({GpuWidth}×{GpuHeight})";
        public string HddLabel  => $"HDD ×{hddCount} ({HddCapMB}MB each)";

        // RAM tier acts as a multiplier on top of the base ramBytes declared on
        // CompProperties_Computer (the physical memory fitted to the machine).
        // T1 = 1×, T2 = 2×, T3 = 4×. Actual MB is derived in Comp_Computer where
        // CompProperties are accessible; see Comp_Computer.EffectiveRamBytes.
        public int RamMultiplier => ramTier == 3 ? 4 : ramTier == 2 ? 2 : 1;

        public int GpuWidth   => gpuTier  == 3 ? 160 : gpuTier  == 2 ? 80 : 50;
        public int GpuHeight  => gpuTier  == 3 ?  50 : gpuTier  == 2 ? 25 : 16;
        public int HddCapMB   => hddCount == 3 ?   4 : hddCount == 2 ?  2 :  1;
        public double CpuHz   => cpuTier  == 3 ? 20.0 : cpuTier == 2 ? 10.0 : 5.0;

        public void ExposeData()
        {
            Scribe_Values.Look(ref cpuTier,      "cpuTier",      1);
            Scribe_Values.Look(ref ramTier,      "ramTier",      1);
            Scribe_Values.Look(ref gpuTier,      "gpuTier",      1);
            Scribe_Values.Look(ref hddCount,     "hddCount",     1);
            Scribe_Values.Look(ref expansionSlots,"expansionSlots",2);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Main computer component
    // ════════════════════════════════════════════════════════════════════════════

    public class Comp_Computer : ThingComp
    {
        // ── State ────────────────────────────────────────────────────────────
        private ComputerState  state  = ComputerState.Off;
        private ScreenBuffer   screen;
        private OCApi          ocApi;
        private List<string>   debugLog = new List<string>();

        // ── Hardware ─────────────────────────────────────────────────────────
        public ComputerHardware Hardware = new ComputerHardware();

        // ── Per-computer BIOS code (serialised to save game) ─────────────────
        // null → use ROM/BIOS/bios.lua default on next boot
        internal string _savedBiosCode = null;

        // ── Per-computer EEPROM user data (serialised; 256 B max) ────────────
        // Stores e.g. the boot-filesystem UUID that OpenOS writes via
        // computer.setBootAddress(). Kept SEPARATE from _savedBiosCode so that
        // writing the boot UUID does not corrupt the BIOS.
        internal string _savedEepromData = null;

        // ── Internet card ────────────────────────────────────────────────────
        private bool internetEnabled = false;

        // ── Legacy XML VFS (migration from old saves) ────────────────────────
        private Dictionary<string, string> persistentVfsB64 = new Dictionary<string, string>();

        // ── Boot timing ──────────────────────────────────────────────────────
        // Tier-dependent: a T1 CPU is slow to POST. RimWorld runs at 60 TPS on
        // 1× speed, so these map to ~4 s / 3 s / 2 s of real time on normal
        // speed. Previously a flat 60 ticks (~1 s) which felt instant at 3×/6×.
        private int bootTicksLeft;
        private int BootTicks =>
            Hardware?.cpuTier == 3 ? 120 :
            Hardware?.cpuTier == 2 ? 180 : 240;

        // ── Reboot / shutdown flags ──────────────────────────────────────────
        private volatile bool _pendingReboot;
        private volatile bool _pendingShutdown;

        // ── Public API ───────────────────────────────────────────────────────
        public CompProperties_Computer Props => (CompProperties_Computer)props;
        private CompPowerTrader PowerComp => parent.TryGetComp<CompPowerTrader>();
        public bool HasPower => PowerComp == null || PowerComp.PowerOn;

        public ComputerState State    => state;
        public ScreenBuffer  Screen   => screen;
        public List<string>  DebugLog => debugLog;
        public long RamUsed  => ocApi?.RamUsed  ?? 0L;
        public long RomUsed  => ocApi?.RomUsed  ?? 0L;
        public string LuaVerString => "NLua/5.3";

        // Actual RAM available to the Lua VM: base memory from Def × RAM-tier multiplier.
        public long  EffectiveRamBytes => Props.ramBytes * Hardware.RamMultiplier;
        public float EffectiveRamMB    => EffectiveRamBytes / 1048576f;
        public string RamLabel         => $"RAM T{Hardware.ramTier} ({EffectiveRamMB:0.#}MB)";

        // ── HDD folder on real disk ──────────────────────────────────────────
        public string HddFolderPath =>
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
            screen = new ScreenBuffer(Hardware.GpuWidth, Hardware.GpuHeight);
            Directory.CreateDirectory(HddFolderPath);
            if (state == ComputerState.Running) InitLua();
        }

        public override void CompTick()
        {
            base.CompTick();

            if (!HasPower && state == ComputerState.Running)
            { ForceOff("Power lost"); return; }

            if (_pendingShutdown)
            {
                _pendingShutdown = _pendingReboot = false;
                FlushHdd(); ForceOff("Lua shutdown"); return;
            }
            if (_pendingReboot)
            {
                _pendingReboot = false;
                FlushHdd(); ForceOff("Lua reboot");
                state = ComputerState.Booting;
                bootTicksLeft = BootTicks;
                screen.Clear(); screen.Println("Rebooting..."); return;
            }

            if (state == ComputerState.Booting)
            { if (--bootTicksLeft <= 0) FinishBoot(); return; }

            if (state == ComputerState.Running)
                ocApi?.Tick();
        }

        public void RequestShutdown(bool reboot)
        {
            if (reboot) _pendingReboot   = true;
            else        _pendingShutdown = true;
        }

        // ════════════════════════════════════════════════════════════════════
        // BIOS flash callback (called from OCApi when eeprom.setData is invoked)
        // ════════════════════════════════════════════════════════════════════

        public void OnBiosFlashed(string newBiosCode)
        {
            _savedBiosCode = newBiosCode;
            Log($"[BIOS] Flashed {newBiosCode?.Length ?? 0} bytes — will take effect on next reboot");
        }

        // Called from OCApi when eeprom.setData is invoked (e.g. OpenOS storing
        // the boot-filesystem UUID). Persisted to the save game so it survives
        // reboots — this is what `computer.setBootAddress(addr)` relies on.
        public void OnEepromDataChanged(string newData)
        {
            _savedEepromData = newData;
        }

        // ════════════════════════════════════════════════════════════════════
        // Power on / off
        // ════════════════════════════════════════════════════════════════════

        public void TryPowerOn()
        {
            if (state != ComputerState.Off && state != ComputerState.Error) return;
            if (!HasPower) { Log("No power"); return; }
            state = ComputerState.Booting;
            bootTicksLeft = BootTicks;
            screen.Clear();
            screen.Println("RimComputers BIOS");
            screen.Println($"CPU: T{Hardware.cpuTier}  RAM: {EffectiveRamMB:0.#}MB  GPU: T{Hardware.gpuTier}");
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
            screen?.Clear();
            screen?.Println("-- off --");
        }

        private void FinishBoot()
        {
            Log("FinishBoot");
            InitLua();
            if (state != ComputerState.Error)
            { state = ComputerState.Running; Log("State -> Running"); }
        }

        private void InitLua()
        {
            try
            {
                Log("InitLua start");
                screen.Clear();

                // Migrate old XML VFS → HDD folder (one-time)
                Dictionary<string, byte[]> legacyMigration = null;
                if (persistentVfsB64?.Count > 0)
                {
                    legacyMigration = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in persistentVfsB64)
                    {
                        try { legacyMigration[kv.Key] = Convert.FromBase64String(kv.Value); }
                        catch { }
                    }
                    persistentVfsB64.Clear();
                }

                ocApi = new OCApi(screen, Props, debugLog, parent,
                    hddFolderPath:     HddFolderPath,
                    legacyVfsMigration: legacyMigration,
                    savedBiosCode:     _savedBiosCode,
                    compComp:          this,
                    ramBytesOverride:  EffectiveRamBytes,
                    savedEepromData:   _savedEepromData);

                if (internetEnabled) ocApi.SetInternetEnabled(true);

                Log("OCApi created, starting BIOS");
                ocApi.StartBios();
                Log("InitLua OK");
            }
            catch (Exception ex)
            {
                state = ComputerState.Error;
                Log("Lua init error: " + ex.Message);
                Verse.Log.Error("[RimComputers] InitLua: " + ex);
                screen?.Println("INIT ERROR: " + ex.Message);
            }
        }

        private void ShutdownLua() { ocApi?.Dispose(); ocApi = null; }

        private void FlushHdd() => ocApi?.FlushHddToFolder();

        // ════════════════════════════════════════════════════════════════════
        // Public helpers
        // ════════════════════════════════════════════════════════════════════

        public string ExecLua(string code)
        {
            if (ocApi == null) return "[no runtime]";
            try { ocApi.ExecuteLua(code); return "ok"; }
            catch (Exception ex) { return "Error: " + ex.Message; }
        }

        public void SafeRestart() { TryPowerOff(); TryPowerOn(); }

        public void Log(string msg)
        {
            lock (debugLog)
            {
                debugLog.Add($"[{Find.TickManager?.TicksGame ?? 0}] {msg}");
                if (debugLog.Count > 10000) debugLog.RemoveAt(0);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Gizmos — computer control + hardware overview
        // ════════════════════════════════════════════════════════════════════

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            bool isOn = state != ComputerState.Off;

            // ── Terminal window ───────────────────────────────────────────────
            yield return new Command_Action
            {
                defaultLabel = "Screen",
                defaultDesc  = "Open/close the computer terminal.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", true),
                action = () =>
                {
                    var w = Find.WindowStack.WindowOfType<Dialog_ComputerScreen>();
                    if (w != null) w.Close();
                    else Find.WindowStack.Add(new Dialog_ComputerScreen(this));
                }
            };

            // ── Power ─────────────────────────────────────────────────────────
            yield return new Command_Action
            {
                defaultLabel = isOn ? "Power Off" : "Power On",
                defaultDesc  = isOn
                    ? "Shut down the computer (flushes HDD)."
                    : "Boot the computer.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/Halt", true),
                action = () => { if (isOn) TryPowerOff(); else TryPowerOn(); }
            };

            // ── Internet card ─────────────────────────────────────────────────
            yield return new Command_Action
            {
                defaultLabel = $"Internet: {(internetEnabled ? "ON" : "OFF")}",
                defaultDesc  = internetEnabled
                    ? "Internet card enabled. Click to disable."
                    : "Enable internet card (wget, pastebin, MineOS…).",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/AttackMelee", true),
                action = () =>
                {
                    internetEnabled = !internetEnabled;
                    ocApi?.SetInternetEnabled(internetEnabled);
                    Log($"[gizmo] Internet: {internetEnabled}");
                }
            };

            // ── Hardware overview (opens a dialog) ────────────────────────────
            yield return new Command_Action
            {
                defaultLabel = "Hardware",
                defaultDesc  = "View and manage installed hardware components.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/ViewQuest", true),
                action = () => Find.WindowStack.Add(new Dialog_ComputerHardware(this))
            };

            // ── Debug log ─────────────────────────────────────────────────────
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
            int hddFiles = 0;
            try
            {
                if (Directory.Exists(HddFolderPath))
                    hddFiles = Directory.GetFiles(HddFolderPath, "*", SearchOption.AllDirectories).Length;
            }
            catch { }

            return $"State: {state}  BIOS: {(_savedBiosCode != null ? "custom" : "default")}\n" +
                   $"{Hardware.CpuLabel}  {RamLabel}  {Hardware.GpuLabel}\n" +
                   $"HDD: {hddFiles} files  Net: {(internetEnabled ? "ON" : "OFF")}";
        }

        // ════════════════════════════════════════════════════════════════════
        // Save / Load
        // ════════════════════════════════════════════════════════════════════

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref state,           "rcState",           ComputerState.Off);
            Scribe_Values.Look(ref internetEnabled, "rcInternetEnabled", false);
            Scribe_Values.Look(ref _savedBiosCode,  "rcBiosCode",        null);
            Scribe_Values.Look(ref _savedEepromData,"rcEepromData",      null);

            Scribe_Deep.Look(ref Hardware, "rcHardware");
            if (Hardware == null) Hardware = new ComputerHardware();

            if (Scribe.mode == LoadSaveMode.Saving) FlushHdd();

            // Legacy VFS migration
            Scribe_Collections.Look(ref persistentVfsB64, "rcVfs",
                LookMode.Value, LookMode.Value);
            if (persistentVfsB64 == null)
                persistentVfsB64 = new Dictionary<string, string>();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Hardware dialog
    // ════════════════════════════════════════════════════════════════════════════

    public class Dialog_ComputerHardware : Window
    {
        private readonly Comp_Computer comp;

        public Dialog_ComputerHardware(Comp_Computer comp)
        {
            this.comp  = comp;
            forcePause = false;
            doCloseButton = true;
            resizeable    = false;
            draggable     = true;
        }

        public override Vector2 InitialSize => new Vector2(520f, 480f);

        public override void DoWindowContents(Rect inRect)
        {
            var hw = comp.Hardware;
            Text.Font = GameFont.Small;

            float y = inRect.y;

            // Title
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 26f),
                $"Hardware — {comp.parent.LabelCap}");
            y += 30f;

            Widgets.DrawLineHorizontal(inRect.x, y, inRect.width);
            y += 8f;

            // ── CPU ──────────────────────────────────────────────────────────
            DrawComponentSlot(ref y, inRect, "CPU", hw.CpuLabel,
                $"Tier {hw.cpuTier} · {hw.CpuHz:F0} Hz execution speed",
                ref hw.cpuTier, 1, 3, comp.State == ComputerState.Off);

            // ── RAM ───────────────────────────────────────────────────────────
            DrawComponentSlot(ref y, inRect, "RAM", comp.RamLabel,
                $"Tier {hw.ramTier} · {comp.EffectiveRamMB:0.#} MB addressable",
                ref hw.ramTier, 1, 3, comp.State == ComputerState.Off);

            // ── GPU ───────────────────────────────────────────────────────────
            DrawComponentSlot(ref y, inRect, "GPU", hw.GpuLabel,
                $"Tier {hw.gpuTier} · {hw.GpuWidth}×{hw.GpuHeight} character resolution",
                ref hw.gpuTier, 1, 3, comp.State == ComputerState.Off);

            // ── HDD ───────────────────────────────────────────────────────────
            DrawComponentSlot(ref y, inRect, "HDD", hw.HddLabel,
                $"{hw.hddCount} disk slot(s) · {hw.HddCapMB} MB each",
                ref hw.hddCount, 1, 3, comp.State == ComputerState.Off);

            y += 10f;
            Widgets.DrawLineHorizontal(inRect.x, y, inRect.width);
            y += 10f;

            // ── BIOS chip info ────────────────────────────────────────────────
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.7f, 0.9f, 1f);
            string biosStatus = comp._savedBiosCode != null
                ? $"Custom BIOS ({comp._savedBiosCode?.Length ?? 0} bytes) — use flash.lua to update"
                : "Default BIOS (from ROM/BIOS/bios.lua) — use flash.lua to customise";
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 20f), $"BIOS: {biosStatus}");
            y += 22f;

            if (comp._savedBiosCode != null)
            {
                GUI.color = new Color(1f, 0.8f, 0.5f);
                if (Widgets.ButtonText(new Rect(inRect.x, y, 160f, 22f), "Reset BIOS to Default"))
                {
                    comp._savedBiosCode = null;
                    comp.Log("[gizmo] BIOS reset to default");
                    Messages.Message("BIOS reset. Reboot to apply.", MessageTypeDefOf.SilentInput, false);
                }
                y += 26f;
            }

            GUI.color = Color.white;

            // ── HDD folder ────────────────────────────────────────────────────
            y += 4f;
            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 18f),
                $"HDD path: {comp.HddFolderPath}");
            y += 22f;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            if (Widgets.ButtonText(new Rect(inRect.x, y, 150f, 24f), "Open HDD Folder"))
            {
                Directory.CreateDirectory(comp.HddFolderPath);
                System.Diagnostics.Process.Start(comp.HddFolderPath);
            }

            if (Widgets.ButtonText(new Rect(inRect.x + 160f, y, 150f, 24f), "Clear HDD"))
            {
                if (comp.State != ComputerState.Off)
                    Messages.Message("Power off the computer first.", MessageTypeDefOf.RejectInput, false);
                else
                {
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "Delete ALL files on HDD? This cannot be undone.",
                        () =>
                        {
                            try
                            {
                                if (Directory.Exists(comp.HddFolderPath))
                                    Directory.Delete(comp.HddFolderPath, true);
                                Directory.CreateDirectory(comp.HddFolderPath);
                                comp.Log("[gizmo] HDD cleared");
                                Messages.Message("HDD cleared.", MessageTypeDefOf.SilentInput, false);
                            }
                            catch (Exception ex)
                            {
                                Messages.Message("Error clearing HDD: " + ex.Message,
                                    MessageTypeDefOf.RejectInput, false);
                            }
                        }, true));
                }
            }

            y += 30f;

            // Note about reboot
            if (comp.State != ComputerState.Off)
            {
                GUI.color = new Color(1f, 0.9f, 0.5f);
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 20f),
                    "⚠  Power off the computer to change hardware components.");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }
        }

        private void DrawComponentSlot(ref float y, Rect inRect,
            string label, string name, string desc,
            ref int tier, int min, int max, bool editable)
        {
            var rowRect = new Rect(inRect.x, y, inRect.width, 50f);
            Widgets.DrawBoxSolid(rowRect, new Color(0.12f, 0.12f, 0.12f));

            // Icon area placeholder
            Widgets.DrawBoxSolid(new Rect(rowRect.x + 4f, rowRect.y + 4f, 42f, 42f),
                new Color(0.2f, 0.2f, 0.3f));
            GUI.color = new Color(0.6f, 0.8f, 1f);
            Text.Font   = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(rowRect.x + 4f, rowRect.y + 4f, 42f, 42f), label);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color   = Color.white;

            // Name + description
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(rowRect.x + 52f, rowRect.y + 6f, 240f, 20f), name);
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            Widgets.Label(new Rect(rowRect.x + 52f, rowRect.y + 26f, 240f, 18f), desc);
            GUI.color = Color.white;

            // Tier selector (only when computer is off)
            if (editable)
            {
                float bx = rowRect.xMax - 90f;
                float by = rowRect.y + 14f;
                if (tier > min && Widgets.ButtonText(new Rect(bx, by, 24f, 22f), "◄"))
                    tier--;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(bx + 26f, by, 32f, 22f), $"T{tier}");
                Text.Anchor = TextAnchor.UpperLeft;
                if (tier < max && Widgets.ButtonText(new Rect(bx + 60f, by, 24f, 22f), "►"))
                    tier++;
            }
            else
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.5f, 0.5f, 0.5f);
                Widgets.Label(new Rect(rowRect.xMax - 90f, rowRect.y + 17f, 86f, 18f), "Power off to edit");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }

            y += 54f;
        }

        // Expose the field via internal accessor for gizmo
        private string _savedBiosAccess => comp._savedBiosCode;
    }
}
