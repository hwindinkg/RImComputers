using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NLua;
// NOTE: do NOT `using KeraLua;` — both NLua and KeraLua expose `Lua` and
// `LuaFunction` types and we end up with CS0104 ambiguity. We only need
// KeraLua's `Lua` / `LuaFunction` / `LuaType` in two places (the raw
// fs.read/write CFunctions); fully-qualified `KeraLua.X` is fine there.
using UnityEngine;
using Verse;

namespace RimComputers
{
    /// <summary>
    /// OpenComputers-compatible Lua runtime for RimComputers.
    ///
    /// Architecture:
    ///   - Lua runs on a dedicated background thread (luaThread).
    ///   - The game thread feeds signals via PushSignal() and calls Tick().
    ///   - pullSignal blocks *only* the Lua thread via SemaphoreSlim; the game
    ///     thread is never blocked.
    ///   - CRITICAL: lua.DoString() / lua.LoadString() must NEVER be called from
    ///     a C# callback that is itself invoked from the Lua thread, because NLua
    ///     holds an internal mutex and the call would deadlock.  All compilation is
    ///     done via a pre-compiled LuaFunction stored as __compile_chunk__.
    /// </summary>
    public class OCApi
    {
        // ── Core state ───────────────────────────────────────────────────────
        private readonly Lua lua;
        private readonly ScreenBuffer screen;
        private readonly CompProperties_Computer props;
        private readonly List<string> log;
        private readonly Thing building;
        private Comp_Computer compComp; // back-reference for RequestShutdown on reboot

        // ROM key set — tracks which VFS paths came from ROM (for writable snapshot)
        private readonly HashSet<string> romKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Two separate virtual filesystems ────────────────────────────────────
        // romVfs  = read-only, loaded from ROM folder (floppy disk)
        // hddVfs  = writable, persisted to a real folder on the host OS
        private readonly Dictionary<string, byte[]> romVfs =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> hddVfs =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        // Legacy unified VFS — kept for operations that don't care which disk
        // (e.g. loadfile, require). Merges romVfs + hddVfs, hdd wins on conflict.
        private Dictionary<string, byte[]> vfs =>
            BuildMergedVfs();

        private Dictionary<string, byte[]> _mergedVfsCache;
        private bool _mergedVfsDirty = true;

        private Dictionary<string, byte[]> BuildMergedVfs()
        {
            if (!_mergedVfsDirty && _mergedVfsCache != null) return _mergedVfsCache;
            _mergedVfsCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in romVfs)  _mergedVfsCache[kv.Key] = kv.Value;
            foreach (var kv in hddVfs)  _mergedVfsCache[kv.Key] = kv.Value;
            _mergedVfsDirty = false;
            return _mergedVfsCache;
        }

        private void InvalidateMergedVfs() => _mergedVfsDirty = true;

        // HDD folder on the host OS (persisted between sessions)
        private readonly string hddFolderPath;

        // Signal queue (game thread writes, Lua thread reads)
        private readonly Queue<object[]> signalQueue = new Queue<object[]>();
        private readonly SemaphoreSlim signalReady = new SemaphoreSlim(0, int.MaxValue);
        private readonly object signalLock = new object();
        private readonly object logLock = new object();

        // Component addresses
        private readonly string gpuAddr, scrnAddr, eepromAddr, compAddr, kbdAddr;
        private readonly string romAddr;   // ROM floppy (read-only)
        private readonly string hddAddr;   // HDD (writable, persisted)
        private readonly string tmpAddr;   // tmpfs (writable, in-memory only)
        // fsAddr kept for backward-compat routing — points to hddAddr
        private string fsAddr => hddAddr;

        // tmpfs: small in-memory writable filesystem mounted at /tmp.
        // Real OC always exposes a tmpfs as a separate filesystem component
        // distinct from the HDD; OpenOS's `install` command, mounting logic
        // (90_filesystem.lua) and shutdown handler all key on the
        // computer.tmpAddress() return value to *exclude* that filesystem
        // from being treated as a regular disk. Previously we returned the
        // HDD address from tmpAddress(), which made install reject the HDD
        // as a target ("No writable disks found, aborting"). Now tmpfs has
        // its own separate address and VFS, and HDD is a regular disk.
        private readonly Dictionary<string, byte[]> tmpVfs =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private string internetAddr; // null when internet card is disabled

        // Internet card state (toggled via gizmo)
        private volatile bool internetEnabled = false;

        // ── GPU off-screen buffer state ─────────────────────────────────────
        // MineOS uses gpu.allocateBuffer / setActiveBuffer / bitblt for double
        // buffering. Our implementation is a stub: every "buffer" reports the
        // screen size and bitblt is a no-op (writes already went to the main
        // screen). Tracking just the indices is enough to satisfy MineOS's
        // "is this buffer still alive?" checks and avoid the
        // "attempt to compare number with nil" error in drawImage.
        private int _gpuBufferNextIdx = 0;
        private int _gpuActiveBuffer  = 0; // 0 = main screen
        private readonly Dictionary<int, (int w, int h)> _gpuBufferSizes
            = new Dictionary<int, (int, int)>();

        // Per-computer BIOS code (eeprom.get / eeprom.set — ~4 KB Lua source).
        private string _biosCode;
        public string BiosCode { get => _biosCode; set => _biosCode = value ?? ""; }

        // EEPROM user data (eeprom.getData / eeprom.setData — max 256 B in OC).
        // Used by OpenOS to store the boot-filesystem UUID. This is SEPARATE
        // from the BIOS code; conflating the two (as a previous version did)
        // causes the BIOS to be overwritten with a UUID the first time OpenOS
        // runs `computer.setBootAddress`, which breaks subsequent reboots and
        // halfway-through installers such as MineOS.
        private string _eepromUserData = "";
        public string EepromUserData
        {
            get => _eepromUserData ?? "";
            set => _eepromUserData = value ?? "";
        }

        // State
        // (eepromData removed — BIOS code is now stored in _biosCode)
        private string bootAddress;   // which filesystem component to boot from
        private volatile bool running = true;
        public bool IsRunning => running;

        // Toggle internet card on/off (called from game thread via gizmo)
        public bool InternetEnabled => internetEnabled;
        public void SetInternetEnabled(bool enabled)
        {
            if (internetEnabled == enabled) return;
            internetEnabled = enabled;
            if (enabled)
            {
                internetAddr = DeterministicGuid(compAddr, "internet");
                PushSignal("component_added", internetAddr, "internet");
                Log($"[internet] card enabled, addr={internetAddr}");
            }
            else
            {
                if (internetAddr != null)
                    PushSignal("component_removed", internetAddr, "internet");
                Log("[internet] card disabled");
                internetAddr = null;
            }
        }

        // Timing
        //
        // `uptimeSeconds` must be consistent with how long `os.sleep` / `pullSignal`
        // actually block. Previously it was tied to `Find.TickManager.TicksGame`,
        // which advances 3× faster on 3× game speed and stops when the game is
        // paused — while our signalReady.Wait() always sleeps in *real* wall‑clock
        // milliseconds. That mismatch made e.g. IcePlayer run 2–3× too fast on
        // fast‑forward (uptime deadline is reached long before the wall‑clock
        // sleep returns). We now measure wall‑clock time since boot via a
        // Stopwatch, which matches wall‑clock sleeping exactly.
        private readonly System.Diagnostics.Stopwatch _uptime =
            System.Diagnostics.Stopwatch.StartNew();
        private double uptimeSeconds => _uptime.Elapsed.TotalSeconds;

        // File handles
        private int nextHandle = 1;
        private readonly Dictionary<int, FileHandle> openHandles =
            new Dictionary<int, FileHandle>();

        // Native CFunctions for byte-preserving I/O.
        //
        // NLua marshals strings as UTF-8 in *both* directions, which is fatal
        // for binary file I/O. e.g. IcePlayer's bad.ice header byte 0xA0 (=160)
        // gets re-encoded through UTF-8 → garbled → finally decoded as "?"
        // (=63), which is why setResolution(W,H) was being called with
        // setResolution(63, 50) instead of setResolution(160, 50). Likewise
        // MineOS install writes binary downloads through fs.write and they end
        // up corrupt for the same reason.
        //
        // KeraLua's PushBuffer / ToBuffer push/pop raw bytes via lua_pushlstring
        // and lua_tolstring without any encoding conversion. We register two
        // CFunctions that do byte-faithful read/write and route the proxy's
        // fs:read / fs:write calls through them, keeping every other field
        // unchanged. The delegates are kept in fields so the GC can't collect
        // them while the Lua state still has pointers.
        private KeraLua.LuaFunction _fsReadRawFn;
        private KeraLua.LuaFunction _fsWriteRawFn;
        private KeraLua.LuaFunction _inetReadRawFn;

        // Memory tracking
        private long ramUsed = 0, romUsed = 0;
        public long RamUsed => ramUsed;
        public long RomUsed => romUsed;

        // Effective RAM exposed to the Lua VM. Usually equals props.ramBytes,
        // but Comp_Computer passes a tier-multiplied override so the amount of
        // memory reported via computer.totalMemory()/freeMemory() matches the
        // gizmo label (e.g. 5 MB at T1, 10 MB at T2, 20 MB at T3).
        private long effectiveRamBytes;
        public long EffectiveRamBytes => effectiveRamBytes;

        private sealed class FileHandle
        {
            public string Path;
            public bool Writing;
            public bool Appending;
            public int Pos;
            // For write handles — the target VFS (hddVfs). Null = read from MergedVfs.
            public Dictionary<string, byte[]> Vfs;
            // For read handles — merged ROM+HDD view.
            public Dictionary<string, byte[]> MergedVfs;

            // Returns the appropriate VFS for reading
            public Dictionary<string, byte[]> ReadVfs => Writing ? Vfs : MergedVfs;
        }

        // ── Threading ────────────────────────────────────────────────────────
        private Thread luaThread;
        private LuaFunction compileChunkFn;

        // ── Beep queue (Lua thread enqueues, game thread plays) ──────────────
        private readonly Queue<(float freq, float duration)> _beepQueue =
            new Queue<(float, float)>();
        private AudioSource _beepSource;

        // ════════════════════════════════════════════════════════════════════
        // Constructor
        // ════════════════════════════════════════════════════════════════════

        public OCApi(ScreenBuffer screen, CompProperties_Computer props,
                     List<string> log, Thing building,
                     string hddFolderPath = null,
                     Dictionary<string, byte[]> legacyVfsMigration = null,
                     string savedBiosCode = null,
                     Comp_Computer compComp = null,
                     long ramBytesOverride = 0L,
                     string savedEepromData = null)
        {
            LoadNativeLua();

            this.lua = new Lua();
            this.screen = screen;
            this.props = props;
            this.log = log;
            this.building = building;
            this.compComp = compComp;
            this.hddFolderPath = hddFolderPath;
            this.effectiveRamBytes = ramBytesOverride > 0 ? ramBytesOverride : props.ramBytes;
            this._eepromUserData   = savedEepromData ?? "";

            lua.UseTraceback = true;

            string baseId = building?.ThingID ?? Guid.NewGuid().ToString("N");
            gpuAddr    = DeterministicGuid(baseId, "gpu");
            scrnAddr   = DeterministicGuid(baseId, "screen");
            romAddr    = DeterministicGuid(baseId, "rom");
            hddAddr    = DeterministicGuid(baseId, "hdd");
            tmpAddr    = DeterministicGuid(baseId, "tmp");
            eepromAddr = DeterministicGuid(baseId, "eeprom");
            compAddr   = DeterministicGuid(baseId, "computer");
            kbdAddr    = DeterministicGuid(baseId, "keyboard");

            // ── ROM floppy (read-only OS disk) ────────────────────────────────
            string defaultBios;
            foreach (var e in RomLoader.Load(romVfs, out defaultBios)) log.Add(e);
            foreach (var k in romVfs.Keys)  romKeys.Add(k);
            foreach (var v in romVfs.Values) romUsed += v.LongLength;

            // ── Per-computer BIOS ─────────────────────────────────────────────
            // Use saved BIOS if this computer was previously flashed; else default.
            //
            // Defensive: in pre-v4 builds, eeprom.set and eeprom.setData both
            // wrote to _savedBiosCode, so OpenOS's `setBootAddress(addr)`
            // routinely clobbered the BIOS with a 36-byte filesystem UUID.
            // Detect that case (looks-like-UUID and obviously not Lua source)
            // and fall back to the default BIOS, so old saves don't need a
            // manual "Reset BIOS to Default" press to recover.
            bool savedLooksCorrupt =
                !string.IsNullOrEmpty(savedBiosCode) &&
                savedBiosCode.Length <= 64 &&
                !savedBiosCode.Contains("\n") &&
                !savedBiosCode.Contains(";") &&
                !savedBiosCode.Contains("=") &&
                !savedBiosCode.Contains("(");
            if (savedLooksCorrupt)
            {
                Log($"[OCApi] saved BIOS looks corrupt ({savedBiosCode.Length}B, no Lua syntax) — falling back to default");
                savedBiosCode = null;
                // Tell Comp_Computer to drop the corrupt blob so it isn't
                // reapplied on next reboot.
                compComp?.OnBiosFlashed(null);
            }
            _biosCode = !string.IsNullOrEmpty(savedBiosCode)
                ? savedBiosCode
                : defaultBios ?? RomLoader.BuiltinBiosStub;

            // ── HDD (writable, persisted to folder) ──────────────────────────
            if (!string.IsNullOrEmpty(hddFolderPath) && Directory.Exists(hddFolderPath))
            {
                LoadFolderToVfs(hddFolderPath, hddVfs);
                Log($"[hdd] Loaded {hddVfs.Count} files from disk folder");
            }
            else if (legacyVfsMigration != null && legacyVfsMigration.Count > 0)
            {
                foreach (var kv in legacyVfsMigration)
                    if (!romKeys.Contains(kv.Key))
                        hddVfs[kv.Key] = kv.Value;
                Log($"[hdd] Migrated {hddVfs.Count} entries from legacy save");
            }
            InvalidateMergedVfs();

            // Boot from HDD if it has an OS, else fall back to ROM (first run / install)
            bool hddHasOs = hddVfs.ContainsKey("init.lua") || hddVfs.ContainsKey("bios.lua")
                         || hddVfs.ContainsKey("boot/01_process.lua");
            bootAddress = hddHasOs ? hddAddr : romAddr;

            foreach (var d in new[] { "bin","lib","etc","tmp","home","mnt","usr","boot","lib/core" })
                EnsureDir(d, hddVfs);
            if (!hddVfs.ContainsKey("etc/hostname") && !romVfs.ContainsKey("etc/hostname"))
                WriteText("etc/hostname", "rimcomp-" + hddAddr.Substring(0,8) + "\n", hddVfs);

            Log($"[OCApi] ROM={romVfs.Count} files ({romUsed/1024}KB)  HDD={hddVfs.Count} files  boot={bootAddress.Substring(0,8)}...");
            Log($"[OCApi] BIOS {_biosCode.Length}B ({(savedBiosCode != null ? "custom/flashed" : "default")})");

            RegisterAll();

            var res = lua.DoString(
                @"return function(src, name)
                    return load(src, name, 'bt', _ENV)
                  end");
            compileChunkFn = res?[0] as LuaFunction;

            // Quick sanity check
            try
            {
                var r = lua.DoString("return 42");
                Log($"[test] NLua OK: {r[0]}");
            }
            catch (Exception ex) { Log($"[test] FAIL: {ex.Message}"); }

            Log("[OCApi] Lua initialized, ready for BIOS");
        }

        // ════════════════════════════════════════════════════════════════════
        // Native Library Loading
        // ════════════════════════════════════════════════════════════════════

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        private static IntPtr nativeLuaHandle = IntPtr.Zero;

        private static void LoadNativeLua()
        {
            if (nativeLuaHandle != IntPtr.Zero) return;

            string modDir = null;
            foreach (var mod in LoadedModManager.RunningMods)
            {
                if (mod.PackageId != null &&
                    mod.PackageId.ToLowerInvariant().Contains("rimcomputers"))
                {
                    modDir = mod.RootDir;
                    break;
                }
            }

            var searchPaths = new[]
            {
                modDir != null
                    ? Path.Combine(modDir, "Native", "win64", "lua54.dll")
                    : null,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "Mods", "RimComputers", "Native", "win64", "lua54.dll"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "Native", "win64", "lua54.dll"),
            };

            foreach (var path in searchPaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                string full = Path.GetFullPath(path);
                if (!File.Exists(full)) continue;

                nativeLuaHandle = LoadLibrary(full);
                if (nativeLuaHandle != IntPtr.Zero)
                {
                    Verse.Log.Message(
                        "[RimComputers] Loaded native lua54.dll from: " + full);
                    return;
                }
            }

            Verse.Log.Warning(
                "[RimComputers] Could not find lua54.dll in Native/win64/ folder");
        }

        // ════════════════════════════════════════════════════════════════════
        // API Registration  (all called from game thread before Lua thread starts)
        // ════════════════════════════════════════════════════════════════════

        private void RegisterAll()
        {
            // ── checkArg ─────────────────────────────────────────────────────
            lua.DoString(@"
function checkArg(n, val, ...)
    local types = {...}
    local t = type(val)
    for _, expected in ipairs(types) do
        if t == expected then return end
    end
    local exp = table.concat(types, ' or ')
    error(string.format('bad argument #%d (%s expected, got %s)', n, exp, t), 2)
end
");

            // ── _OSVERSION ───────────────────────────────────────────────────
            lua["_OSVERSION"] = "OpenOS 1.8.9";

            // ── process stub (boot/01_process.lua will replace it) ──────────
            // Нужен полный стаб — boot/01_process.lua делает require("process")
            // и использует process.list, process.findProcess, process.info, process.addHandle
            lua.DoString(@"
do
    local _process_list = {}
    local _process_self = {
        path = '/init.lua',
        command = 'init',
        env = _ENV,
        data = { vars = {}, handles = {}, io = {}, coroutine_handler = coroutine, signal = error },
        instances = setmetatable({}, {__mode='v'})
    }

    process = {
        list = setmetatable({}, {
            __index = function(t, co) return _process_self end,
            __newindex = function(t, co, v)
                rawset(t, co, v)
            end,
            __pairs = function(t)
                return next, t, nil
            end
        }),
        info = function(co)
            return _process_self
        end,
        findProcess = function(co)
            return _process_self
        end,
        addHandle = function(handle)
            -- track opened handles for process
            table.insert(_process_self.data.handles, handle)
        end,
    }
end
");

            // ── computer table ───────────────────────────────────────────────
            lua.DoString(@"computer = {}");
            lua["computer.address"] = new Func<string>(() => compAddr);
            // tmpAddress ДОЛЖНА быть функцией — OpenOS вызывает computer.tmpAddress().
            // Real OC returns the *tmpfs* address (a separate filesystem from
            // the HDD). OpenOS install/mount logic uses this to *exclude* the
            // tmpfs from regular disk handling. Returning fsAddr (=hdd) made
            // install say "No writable disks found" because it filtered out
            // the only writable disk we exposed.
            lua["computer.tmpAddress"] = (Func<string>)(() => tmpAddr);
            lua["computer.totalMemory"] = (Func<long>)(() => effectiveRamBytes);
            lua["computer.freeMemory"] = (Func<long>)(() => effectiveRamBytes - ramUsed);
            lua["computer.uptime"] = (Func<double>)(() => (double)uptimeSeconds);
            lua["computer.energy"] = (Func<double>)(() => 100.0);
            lua["computer.maxEnergy"] = (Func<double>)(() => 100.0);
            lua["computer.isRunning"] = (Func<bool>)(() => running);
            // beep(frequency, duration) — генерирует синусоидальный звук через Unity AudioClip
            lua["computer.beep"] = (Action<object, object>)((freqObj, durObj) =>
            {
                float freq = 1000f;
                float dur = 0.1f;
                if (freqObj != null) try { freq = (float)Convert.ToDouble(freqObj); } catch { }
                if (durObj != null) try { dur = (float)Convert.ToDouble(durObj); } catch { }
                freq = Mathf.Clamp(freq, 20f, 8000f);
                dur = Mathf.Clamp(dur, 0.01f, 0.5f);
                lock (signalLock) { _beepQueue.Enqueue((freq, dur)); }
                Log($"[beep] {freq:F0}Hz {dur:F2}s");
            });

            lua["computer.shutdown"] = (Action<object>)(reboot =>
            {
                bool isReboot = reboot is bool b && b;
                Log($"[computer] shutdown(reboot={isReboot})");
                running = false;
                try { signalReady.Release(10); } catch { }
                // Notify game thread so it can flush VFS and schedule reboot/shutdown.
                // This must be done BEFORE Lua thread exits so the snapshot is still valid.
                compComp?.RequestShutdown(isReboot);
            });

            // getBootAddress / setBootAddress
            lua["computer.getBootAddress"] = (Func<string>)(() => bootAddress ?? hddAddr);
            lua["computer.setBootAddress"] = (Action<object>)(addr =>
            {
                bootAddress = addr?.ToString() ?? bootAddress;
                Log($"[computer] setBootAddress={bootAddress?.Substring(0, Math.Min(8, bootAddress?.Length ?? 0))}...");
            });

            // pushSignal — NLua cannot match (string, object[]) when Lua passes individual
            // args. Expose raw C# function taking a packed LuaTable, wrap in Lua for varargs.
            lua["__comp_pushSignal_raw__"] = (Action<string, LuaTable>)((name, argsTable) =>
            {
                var extra = new List<object>();
                if (argsTable != null)
                    for (int i = 1; ; i++)
                    {
                        var v = argsTable[i];
                        if (v == null) break;
                        extra.Add(v);
                    }
                PushSignal(name, extra.ToArray());
            });
            lua.DoString(@"
do
    local _raw = __comp_pushSignal_raw__
    computer.pushSignal = function(name, ...)
        local args = table.pack(...)
        local tbl = {}
        for i = 1, args.n do tbl[i] = args[i] end
        _raw(name, tbl)
    end
end
");

            // pullSignal
            RegisterPullSignal();

            // ── component table ──────────────────────────────────────────────
            RegisterComponentTable();

            // ── unicode ──────────────────────────────────────────────────────
            RegisterUnicode();

            // ── os stub ──────────────────────────────────────────────────────
            lua.DoString(@"os = os or {}");
            lua["os.clock"] = (Func<double>)(() => (double)uptimeSeconds);
            lua["os.time"] = (Func<double>)(() =>
                (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            lua["os.exit"] = (Action)(() => { running = false; });
            lua["os.sleep"] = (Action<double>)(secs =>
            {
                // Real OC os.sleep blocks via pullSignal with a timeout.
                // We wait up to 'secs' REAL seconds on the semaphore,
                // draining and discarding any signals that wake us early.
                //
                // Uses a wall‑clock deadline instead of accumulating the
                // requested chunk size — previously each early wakeup counted
                // the full 50 ms chunk towards `elapsed`, so sleeps finished
                // far too soon (this is why IcePlayer ran 2–3× too fast).
                if (secs <= 0) return;
                int totalMs = Math.Min((int)(secs * 1000), 300_000); // cap 5 min
                long deadline = Environment.TickCount + totalMs;
                const int chunk = 50;
                while (running)
                {
                    int remaining = unchecked((int)(deadline - Environment.TickCount));
                    if (remaining <= 0) break;
                    int wait = Math.Min(chunk, remaining);
                    signalReady.Wait(wait);
                    // Drain any signals that woke us so they don't pile up.
                    lock (signalLock)
                    {
                        while (signalQueue.Count > 0) signalQueue.Dequeue();
                    }
                }
            });
            lua["os.setenv"] = (Action<string, object>)((k, v) => { });
            lua["os.getenv"] = (Func<string, object>)(k => null);

            // ── bit32 ────────────────────────────────────────────────────────
            lua.DoString(@"
bit32 = {}
function bit32.band(...)
    local r = 0xFFFFFFFF
    for _,v in ipairs({...}) do r = r & math.tointeger(v) end
    return r
end
function bit32.bor(...)
    local r = 0
    for _,v in ipairs({...}) do r = r | math.tointeger(v) end
    return r
end
function bit32.bxor(...)
    local r = 0
    for _,v in ipairs({...}) do r = r ~ math.tointeger(v) end
    return r
end
function bit32.bnot(v)       return ~math.tointeger(v) & 0xFFFFFFFF end
function bit32.lshift(v, n)  return (math.tointeger(v) << math.tointeger(n)) & 0xFFFFFFFF end
function bit32.rshift(v, n)  return (math.tointeger(v) & 0xFFFFFFFF) >> math.tointeger(n) end
function bit32.arshift(v, n) return math.tointeger(v) >> math.tointeger(n) end
function bit32.btest(...)    return bit32.band(...) ~= 0 end
");

            // ── debug stub ───────────────────────────────────────────────────
            lua.DoString(@"
debug = debug or {}
if not debug.traceback then
    debug.traceback = function(msg) return tostring(msg) end
end
");

            // ── package stub ─────────────────────────────────────────────────
            lua.DoString(@"
package = package or {}
package.loaded   = package.loaded   or {}
package.preload  = package.preload  or {}
package.searchers = package.searchers or {}
-- OpenOS package.lua перезапишет этот путь, но для начальной загрузки нужен он:
package.path = '/lib/?.lua;/usr/lib/?.lua;/lib/core/?.lua;/lib/?/init.lua'
package.cpath = ''
");

            // ── io stub ──────────────────────────────────────────────────────
            // OpenOS boot/03_io.lua заменит io полностью через tty/buffer.
            // Наша заглушка нужна только для BIOS-фазы (до загрузки OpenOS).
            lua.DoString(@"io = io or {}");
            lua["io.write"] = (Action<string>)(s =>
            {
                if (!string.IsNullOrEmpty(s)) screen.Println(s);
            });
            lua["io.flush"] = (Action)(() => { });
            // Заглушки для io.input/io.output/io.error — OpenOS их переопределит
            lua.DoString(@"
do
    local _stderr_stream = { write = function(self, ...) end, flush = function(self) end, close = function() end }
    local _stdout_stream = { write = function(self, s) io.write(tostring(s or '')) end, flush = function(self) end, close = function() end }
    local _stdin_stream  = { read = function(self, ...) return nil end, close = function() end }

    io.stderr = _stderr_stream
    io.stdout = _stdout_stream
    io.stdin  = _stdin_stream

    -- io.input/io.output/io.error как в стандартном Lua,
    -- OpenOS boot/03_io.lua вызовет io.input(core_stdin) и т.д.
    local _input  = _stdin_stream
    local _output = _stdout_stream
    local _error  = _stderr_stream

    function io.input(file)
        if file ~= nil then _input = file end
        return _input
    end
    function io.output(file)
        if file ~= nil then _output = file end
        return _output
    end
    function io.error(file)
        if file ~= nil then _error = file end
        return _error
    end

    -- io.read — делегируем в _input
    function io.read(...)
        return _input:read(...)
    end

    -- io.lines stub
    function io.lines(filename, ...)
        return function() return nil end
    end
end
");

            // ── GPU, filesystem, screen, eeprom ──────────────────────────────
            RegisterGpu();
            RegisterFilesystem();
            RegisterScreen();
            RegisterEeprom();

            // ── Internet response read/close helpers ──────────────────────────
            // Raw byte-preserving inet read. Same UTF-8 marshalling problem
            // as fs.read had pre-v5f1: NLua's `Func<double,string>` round-trips
            // through `Encoding.UTF8.GetString` in both directions, so any
            // byte that isn't valid UTF-8 (e.g. binary image data inside a
            // .pic file MineOS downloads, or a tinyurl HTML 30x redirect
            // body containing 0x80-0xFF bytes) becomes 0xEF 0xBF 0xBD or
            // a literal '?' (0x3F) by the time Lua's `:byte()` sees it.
            // The downloaded MineOS installer's drawImage then compares
            // a corrupted byte against a real number and crashes with
            // `attempt to compare number with nil`.
            //
            // Use a KeraLua CFunction with PushBuffer to push raw bytes
            // straight to the Lua stack — no encoding round-trip.
            _inetReadRawFn = new KeraLua.LuaFunction(luaStatePtr =>
            {
                ThrottleInvokeCall();
                var L = KeraLua.Lua.FromIntPtr(luaStatePtr);
                int id = (int)L.ToInteger(1);
                if (!_internetResponses.TryGetValue(id, out var state))
                {
                    L.PushNil();
                    return 1;
                }
                if (state.pos >= state.data.Length)
                {
                    _internetResponses.Remove(id);
                    L.PushNil();
                    return 1; // EOF
                }
                int chunk = Math.Min(2048, state.data.Length - state.pos);
                byte[] part = new byte[chunk];
                Buffer.BlockCopy(state.data, state.pos, part, 0, chunk);
                _internetResponses[id] = (state.data, state.pos + chunk);
                L.PushBuffer(part);
                return 1;
            });
            lua.State.Register("__inet_read_raw__", _inetReadRawFn);

            lua["__inet_close__"] = (Action<double>)(hid =>
            {
                _internetResponses.Remove((int)hid);
            });
            // Wrap the internet handle table with proper read/close methods
            lua.DoString(@"
do
    local _inet_read  = __inet_read_raw__
    local _inet_close = __inet_close__
    -- Called by InvokeInternet to wrap a response into an OC-compatible handle
    -- internet.lua calls: request.read() and request.close() (no colon)
    function __make_inet_handle__(hid)
        local closed = false
        local h = {}
        -- read() called as h.read() by internet.lua wrapper
        h.read = function(...)
            if closed then return nil end
            local chunk = _inet_read(hid)
            if chunk == nil then
                closed = true
                return nil
            end
            return chunk
        end
        -- close() called as h.close() 
        h.close = function(...)
            if not closed then
                _inet_close(hid)
                closed = true
            end
            return true
        end
        return h
    end
end
");

            // ── loadfile (VFS-aware, deadlock-safe) ──────────────────────────
            // IMPORTANT: this is called from the Lua thread.
            // It must NOT call lua.DoString(). Instead it uses the pre-compiled
            // compileChunkFn LuaFunction which re-enters Lua safely via the C API.
            lua["loadfile"] = (Func<string, object[]>)(LuaLoadFileSafe);

            // dofile helper built in Lua (uses our loadfile)
            lua.DoString(@"
function dofile(filename)
    local fn, err = loadfile(filename)
    if not fn then error(tostring(err) .. ': ' .. tostring(filename), 0) end
    return fn()
end
");

            // ── require (VFS-aware, replaces built-in) ───────────────────────
            RegisterRequire();

            // ── package.loaded — заполняем ПОСЛЕ всех регистраций ────────────
            // Это гарантирует что require("component"), require("computer") и т.д.
            // вернут финальные объекты, а не промежуточные заглушки.
            // boot.lua делает _G.component = nil, но package.loaded.component
            // остаётся живым — так и работает настоящий OC.
            lua.DoString(@"
package.loaded['component'] = component
package.loaded['computer']  = computer
package.loaded['unicode']   = unicode
package.loaded['process']   = process
package.loaded['string']    = string
package.loaded['table']     = table
package.loaded['math']      = math
package.loaded['os']        = os
package.loaded['io']        = io
package.loaded['bit32']     = bit32
package.loaded['_G']        = _G
package.loaded['coroutine'] = coroutine
package.loaded['debug']     = debug
");

            Log("[reg] All APIs registered");
        }

        // ════════════════════════════════════════════════════════════════════
        // component table
        // ════════════════════════════════════════════════════════════════════

        private void RegisterComponentTable()
        {
            lua.DoString(@"component = {}");

            // component.list(filter, exact) → iterable table  {[addr]=type,...}
            // The table must be iterable with  for addr in component.list(filter) do
            // which means component.list must return a *stateful iterator function*.
            // In real OC the returned object is both a table AND callable.
            // We implement it as a simple stateful closure to stay deadlock-safe.
            lua["__comp_list_raw__"] =
                (Func<string, bool, LuaTable>)ComponentListRaw;

            // component.list(filter, exact) — точное воспроизведение поведения настоящего OC:
            // Возвращает таблицу {[addr]=type, ...} с метатаблицей __call.
            //
            // Паттерны из OpenOS которые должны работать одновременно:
            //
            //   (A) component.list("gpu")()
            //       → таблица.__call() → возвращает итератор-функцию → вызов () → первый адрес
            //
            //   (B) for a,t in component.list("gpu") do
            //       → for..in вызывает таблицу как f(s,var) через __call
            //       → __call должен вернуть тройку (iter, state, initval)
            //       НО: Lua for..in ожидает что первый возврат — это сама итераторная функция.
            //       Значит __call должен возвращать STATEFUL ITERATOR напрямую.
            //
            //   (C) component.list("filesystem")[addr]
            //       → прямая индексация таблицы по адресу
            //
            // Решение: __call возвращает stateful iterator (замыкание).
            // Для паттерна (A): component.list("gpu")() — первый () вызывает __call,
            //   возвращает iterator-функцию. Второй () вызывает iterator → первый адрес.
            // Для паттерна (B): for..in получает таблицу, вызывает __call(tbl) →
            //   получает iterator. Lua теперь ожидает что iterator это f(s,var),
            //   но наш iterator stateful и игнорирует аргументы.
            //   ПРОБЛЕМА: for..in делает local f,s,v = expr; потом f(s,v).
            //   Если expr возвращает таблицу, то f=таблица, s=nil, v=nil.
            //   Потом Lua вызывает f(s,v) = table(nil,nil) через __call → наш iterator.
            //   Итератор возвращает addr,type → всё работает!
            lua.DoString(@"
component.list = function(filter, exact)
    local tbl = __comp_list_raw__(filter or '', exact == true)
    local keys = {}
    for k in pairs(tbl) do keys[#keys+1] = k end
    table.sort(keys)
    local i = 0
    -- __call делает таблицу одновременно итерируемой (for..in)
    -- и вызываемой для получения следующего элемента
    return setmetatable(tbl, {
        __call = function(self, ...)
            i = i + 1
            local addr = keys[i]
            if addr then return addr, tbl[addr] end
            return nil
        end
    })
end
");

            // component.invoke(addr, method, ...)
            lua["__comp_invoke_raw__"] =
                (Func<string, string, LuaTable, LuaTable>)ComponentInvoke;

            lua.DoString(@"
component.invoke = function(addr, method, ...)
    -- Filesystem read/write must preserve raw bytes. See proxy below.
    if method == 'read' or method == 'write' then
        local t = __comp_type_raw__(addr)
        if t == 'filesystem' then
            if method == 'read'  then return __fs_read_raw__(...)  end
            if method == 'write' then return __fs_write_raw__(...) end
        end
    end
    local args = table.pack(...)
    local tbl = {}
    for i = 1, args.n do tbl[i] = args[i] end
    local packed = __comp_invoke_raw__(addr, method, tbl)
    if packed == nil then return nil end
    if packed.isnil  then return nil end
    if packed.ok then
        if packed.result == nil then return nil end
        return table.unpack(packed.result)
    else
        error(packed.err, 0)
    end
end
");

            lua["__comp_type_raw__"] = (Func<string, string>)ComponentType;
            lua["__comp_avail_raw__"] = (Func<string, bool>)ComponentAvailable;
            lua["__comp_address_raw__"] = (Func<string>)(() => compAddr);

            // Register the byte-faithful fs.read / fs.write CFunctions.
            RegisterRawFsIO();

            lua.DoString(@"
component.type = function(addr)   return __comp_type_raw__(addr) end
component.isAvailable = function(t) return __comp_avail_raw__(t) end
component.address = function() return __comp_address_raw__() end

-- proxy захватывает __comp_invoke_raw__ напрямую в замыкание,
-- чтобы не зависеть от глобального _G.component (boot.lua делает _G.component = nil)
component.proxy = function(addr)
    local t = __comp_type_raw__(addr)
    -- Если адрес неизвестен — вернуть nil, как в реальном OC
    if not t then return nil end
    -- Захватываем invoke-функцию прямо сейчас, не через component.invoke
    local _invoke_raw = __comp_invoke_raw__
    local _fs_read_raw  = __fs_read_raw__
    local _fs_write_raw = __fs_write_raw__
    local function invoke_method(method, ...)
        -- Filesystem read/write must preserve raw bytes — NLua converts
        -- strings via UTF-8 in both directions, which corrupts arbitrary
        -- binary data (e.g. an 0xA0 byte in IcePlayer's bad.ice header
        -- becomes a literal '?'). The CFunctions push/pop bytes directly
        -- via lua_pushlstring / lua_tolstring, so they round-trip.
        if t == 'filesystem' then
            if method == 'read'  then return _fs_read_raw(...) end
            if method == 'write' then return _fs_write_raw(...) end
        end
        local args = table.pack(...)
        local tbl = {}
        for i = 1, args.n do tbl[i] = args[i] end
        local packed = _invoke_raw(addr, method, tbl)
        if packed == nil then return nil end
        if packed.isnil then return nil end
        if packed.ok then
            if packed.result == nil then return nil end
            return table.unpack(packed.result)
        else
            error(packed.err, 0)
        end
    end
    local proxy = { address = addr, type = t }
    setmetatable(proxy, {
        __index = function(self, key)
            return function(...)
                return invoke_method(key, ...)
            end
        end
    })
    return proxy
end

component.getPrimary = function(t)
    local addr = component.list(t)()
    if addr then return component.proxy(addr) end
    return nil
end
");

            // ── print() / error() hooks ──────────────────────────────────────
            // Mirror everything the Lua code prints to the host debug log.
            // MineOS / OpenOS BIOSes commonly use xpcall(boot, errorHandler)
            // and the errorHandler does `print(traceback)` before drawing the
            // error to the screen. Our C# `try/catch` around biosFn.Call()
            // never sees these errors because they're handled inside Lua. By
            // funnelling Lua's print to our log we get the full traceback
            // without modifying the user's BIOS.
            lua["__host_log__"] = (Action<string>)(s =>
            {
                if (s == null) return;
                lock (logLock)
                {
                    foreach (var line in s.Split('\n'))
                        Log("[lua] " + line.TrimEnd('\r'));
                }
            });
            lua.DoString(@"
local _native_print = print
print = function(...)
    local n = select('#', ...)
    local parts = {}
    for i = 1, n do parts[i] = tostring((select(i, ...))) end
    local s = table.concat(parts, '\t')
    pcall(__host_log__, s)
    if _native_print then return _native_print(...) end
end
");

            // Wrap io.stderr:write / io.write so error chunks (including
            // MineOS BIOS's traceback dumps) reach the host log even if the
            // BIOS bypasses print().
            lua.DoString(@"
if io and io.write then
    local _native_io_write = io.write
    io.write = function(...)
        local n = select('#', ...)
        local parts = {}
        for i = 1, n do parts[i] = tostring((select(i, ...))) end
        pcall(__host_log__, '[io.write] ' .. table.concat(parts, ''))
        return _native_io_write(...)
    end
end
");
        }

        // Returns flat {[addr]=type} table for all matching components.
        // Safe to call from Lua thread because it only uses NewLuaTable().
        private LuaTable ComponentListRaw(string filter, bool exact)
        {
            lock (logLock) { Log($"[ComponentListHelper] filter='{filter}', exact={exact}"); }

            var all = new List<(string addr, string type)>
            {
                (gpuAddr,    "gpu"),
                (scrnAddr,   "screen"),
                (romAddr,    "filesystem"),   // ROM floppy (read-only)
                (hddAddr,    "filesystem"),   // HDD (writable, persisted)
                (tmpAddr,    "filesystem"),   // tmpfs (writable, in-memory)
                (eepromAddr, "eeprom"),
                (kbdAddr,    "keyboard"),
                (compAddr,   "computer"),
            };

            if (internetEnabled && internetAddr != null)
                all.Add((internetAddr, "internet"));

            var tbl = NewLuaTable();
            int count = 0;
            foreach (var (addr, type) in all)
            {
                bool match = string.IsNullOrEmpty(filter) ||
                             (exact ? type == filter : type.Contains(filter));
                if (!match) continue;
                tbl[addr] = type;
                count++;
                lock (logLock) { Log($"[ComponentListHelper] {addr.Substring(0,8)} = {type}"); }
            }
            lock (logLock) { Log($"[ComponentListHelper] {count} entries"); }
            return tbl;
        }

        private string ComponentType(string addr)
        {
            if (addr == gpuAddr)    return "gpu";
            if (addr == scrnAddr)   return "screen";
            if (addr == romAddr)    return "filesystem";
            if (addr == hddAddr)    return "filesystem";
            if (addr == tmpAddr)    return "filesystem";
            if (addr == eepromAddr) return "eeprom";
            if (addr == kbdAddr)    return "keyboard";
            if (addr == compAddr)   return "computer";
            if (internetEnabled && addr == internetAddr) return "internet";
            return null;
        }

        private bool ComponentAvailable(string type)
            => type == "gpu" || type == "screen" ||
               type == "filesystem" || type == "eeprom" ||
               type == "keyboard" || type == "computer" ||
               (type == "internet" && internetEnabled);

        // ── Методы, которые вызываются сотни раз в секунду — не логируем ────────
        private static readonly HashSet<string> _quietInvoke = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "set","fill","copy","setBackground","setForeground","setForegroundColor",
            "setBackgroundColor","getBackground","getForeground","getScreen",
            "getCursor","setCursor","cursorBlink",
            "read","write","seek",
            // Filesystem hot path during install / cp -r — these get called
            // hundreds of times per file (transfer.lua's stat() does ~5 fs
            // ops per entry, install_basics scans every mounted fs). Logging
            // each call drowns out everything useful and slows the Lua
            // thread (Log → compComp.Log → disk write through WriteThrough).
            "exists","isDirectory","isReadOnly","list","size","spaceUsed",
            "spaceTotal","lastModified","getLabel",
            // GPU diagnostics — 100s/frame in MineOS GUI redraws.
            "getResolution","maxResolution","getDepth","maxDepth",
            "getPaletteColor","setPaletteColor","getViewport",
            "getActiveBuffer","setActiveBuffer","getBufferSize",
            "buffers","freeMemory","totalMemory",
        };

        // Called from Lua wrapper (Lua thread). args = {[1]=v1,[2]=v2,...}
        private LuaTable ComponentInvoke(string addr, string method, LuaTable args)
        {
            // NOTE: we used to throw here when running==false to force
            // zombie Lua threads to exit on power-off / Force Restart.
            // That broke normal shutdown — when Lua calls
            // `computer.shutdown()` and then OpenOS's shutdown handlers
            // do final gpu.fill / fs.flush, those calls saw running==false
            // and threw, which OpenOS's xpcall caught and rendered as a
            // BIOS error on screen. The new Comp_Computer.TryPowerOn
            // gives each boot its own ScreenBuffer, so a leaked Lua
            // thread writes harmlessly to an orphaned buffer; the throw
            // is no longer needed.

            // Pay per-call CPU throttle BEFORE doing the work so heavy
            // programs (IcePlayer, MineOS install, gpu.fill loops) get
            // properly slowed down even if `debug.sethook` isn't firing.
            ThrottleInvokeCall();

            if (!_quietInvoke.Contains(method))
                lock (logLock) { Log($"[invoke] {addr.Substring(0,8)}::{method}"); }
            try
            {
                var argList = new List<object>();
                if (args != null)
                    for (int i = 1; ; i++) { var v = args[i]; if (v == null) break; argList.Add(v); }
                var a = argList.ToArray();

                object[] result;
                if      (addr == gpuAddr)   result = InvokeGpu(method, a);
                else if (addr == romAddr)   result = InvokeFsOnVfs(method, a, romVfs, readOnly: true);
                else if (addr == hddAddr)   result = InvokeFsOnVfs(method, a, hddVfs, readOnly: false);
                else if (addr == tmpAddr)   result = InvokeFsOnVfs(method, a, tmpVfs, readOnly: false);
                else if (addr == scrnAddr)  result = InvokeScreen(method, a);
                else if (addr == eepromAddr)result = InvokeEeprom(method, a);
                else if (addr == kbdAddr)   result = InvokeKeyboard(method, a);
                else if (addr == compAddr)  result = InvokeComputer(method, a);
                else if (internetEnabled && addr == internetAddr) result = InvokeInternet(method, a);
                else return MakeResultTable(false, null, "unknown component: " + addr);

                if (result == null)
                {
                    var nilTbl = NewLuaTable();
                    nilTbl["ok"] = true; nilTbl["isnil"] = true;
                    return nilTbl;
                }
                return MakeResultTable(true, result, null);
            }
            catch (Exception ex)
            {
                lock (logLock) { Log($"[invoke] ERR {addr.Substring(0,8)}::{method}: {ex.Message}"); }
                return MakeResultTable(false, null, ex.Message);
            }
        }

        private object[] InvokeKeyboard(string method, object[] args)
        {
            switch (method)
            {
                case "address":
                    return new object[] { kbdAddr };
                case "isControl":
                    if (args.Length >= 1)
                    {
                        double v = args[0] is double d ? d : Convert.ToDouble(args[0]);
                        return new object[] { v < 32.0 || v == 127.0 };
                    }
                    return new object[] { false };
                default:
                    return new object[] { null };
            }
        }

        private object[] InvokeComputer(string method, object[] args)
        {
            switch (method)
            {
                case "address": return new object[] { compAddr };
                case "tmpAddress": return new object[] { tmpAddr };
                case "freeMemory": return new object[] { (double)(effectiveRamBytes - ramUsed) };
                case "totalMemory": return new object[] { (double)effectiveRamBytes };
                case "uptime": return new object[] { (double)uptimeSeconds };
                case "energy": return new object[] { 100.0 };
                case "maxEnergy": return new object[] { 100.0 };
                case "isRunning": return new object[] { running };
                case "beep":
                    {
                        float freq = args.Length > 0 ? (float)Convert.ToDouble(args[0]) : 1000f;
                        float dur = args.Length > 1 ? (float)Convert.ToDouble(args[1]) : 0.1f;
                        lock (signalLock) { _beepQueue.Enqueue((freq, dur)); }
                        return null;
                    }
                case "shutdown":
                    {
                        bool reboot = args.Length > 0 && args[0] is bool b && b;
                        running = false;
                        try { signalReady.Release(10); } catch { }
                        compComp?.RequestShutdown(reboot);
                        return null;
                    }
                case "getProgramLocations": return new object[] { NewLuaTable() };
                default: return new object[] { null };
            }
        }

        // Internet card implementation — HTTP requests via System.Net.WebClient
        private object[] InvokeInternet(string method, object[] args)
        {
            string Str(int i) => i < args.Length ? args[i]?.ToString() ?? "" : "";
            switch (method)
            {
                case "isHttpEnabled": return new object[] { true };
                case "isTcpEnabled": return new object[] { false };
                case "request":
                    {
                        string url = Str(0);
                        string postData = args.Length > 1 && args[1] != null ? Str(1) : null;

                        try
                        {
#pragma warning disable SYSLIB0014
                            var client = new System.Net.WebClient();
#pragma warning restore SYSLIB0014
                            client.Headers["User-Agent"] = "OpenComputers/1.7.5 RimComputers/1.0";

                            // Extra headers from table arg[2]
                            if (args.Length > 2 && args[2] is LuaTable headers)
                            {
                                foreach (System.Collections.DictionaryEntry kv in headers)
                                    client.Headers[kv.Key.ToString()] = kv.Value?.ToString() ?? "";
                            }

                            byte[] responseBytes;
                            if (!string.IsNullOrEmpty(postData))
                            {
                                if (!client.Headers.AllKeys.Any(
                                    k => k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                                    client.Headers["Content-Type"] = "application/x-www-form-urlencoded";
                                responseBytes = client.UploadData(url,
                                    System.Text.Encoding.UTF8.GetBytes(postData));
                            }
                            else
                            {
                                responseBytes = client.DownloadData(url);
                            }

                            int hid = nextHandle++;
                            // Keep raw bytes — do NOT round-trip through
                            // UTF8.GetString or NLua will mangle binary data.
                            _internetResponses[hid] = (responseBytes, 0);

                            // Build handle via Lua helper (must call from Lua thread — this IS the Lua thread)
                            var makeHandle = lua["__make_inet_handle__"] as LuaFunction;
                            if (makeHandle != null)
                            {
                                var result = makeHandle.Call((double)hid);
                                Log($"[internet] HTTP OK url={url} bytes={responseBytes.Length}");
                                return result != null && result.Length > 0
                                    ? new object[] { result[0] }
                                    : new object[] { null, "handle creation failed" };
                            }
                            // Fallback: just return hid as number
                            return new object[] { (double)hid };
                        }
                        catch (Exception ex)
                        {
                            Log($"[internet] HTTP error: {ex.Message}");
                            return new object[] { null, ex.Message };
                        }
                    }
                default: return new object[] { null };
            }
        }

        // Storage for internet response streams.
        // Holds raw bytes so binary downloads (images, .pic files, anything
        // with bytes outside the UTF-8 ASCII range) survive the Lua handoff
        // intact. The CFunction __inet_read_raw__ reads slices via
        // PushBuffer; no UTF-8 round-trip happens.
        private readonly Dictionary<int, (byte[] data, int pos)> _internetResponses =
            new Dictionary<int, (byte[], int)>();

        // ════════════════════════════════════════════════════════════════════
        // unicode
        // ════════════════════════════════════════════════════════════════════

        private void RegisterUnicode()
        {
            // CRITICAL FIX: All unicode functions use LOCAL upvalues to reference
            // each other — NOT the global 'unicode' table.
            //
            // Root cause of the crash: OpenOS's boot process (lib/core/boot.lua,
            // boot/04_component.lua, etc.) replaces or scopes _ENV. When code runs
            // in the new _ENV, looking up 'unicode' as a global fails → nil.
            // If unicode.wlen calls unicode.len (global lookup), it crashes with
            // "attempt to index a nil value (global 'unicode')".
            //
            // Solution: define all implementations as local functions first, then
            // assign them to the unicode table. The functions never reference 'unicode'
            // inside their bodies — they use upvalues.
            //
            // unicode.char uses utf8.char (Lua 5.3 built-in, always present in NLua).
            // This correctly handles box-drawing (U+2500+), arrows (U+2190+), etc.
            lua.DoString(@"
do
    -- ── char ────────────────────────────────────────────────────────────────
    local function char_impl(...)
        local r = ''
        local n = select('#', ...)
        for i = 1, n do
            local cp = select(i, ...)
            if type(cp) == 'number' then
                cp = math.floor(cp + 0.5)
                if cp >= 0 then
                    if cp < 128 then
                        r = r .. string.char(cp)
                    elseif utf8 then
                        -- utf8.char is part of Lua 5.3 standard library
                        local ok, ch = pcall(utf8.char, cp)
                        if ok then r = r .. ch end
                    end
                end
            end
        end
        return r
    end

    -- ── len ─────────────────────────────────────────────────────────────────
    local function len_impl(s)
        if type(s) ~= 'string' then return 0 end
        if utf8 then
            local n = utf8.len(s)
            return n or 0
        end
        -- fallback: count non-continuation bytes
        local n = 0
        for i = 1, #s do
            local b = string.byte(s, i)
            if b < 0x80 or b >= 0xC0 then n = n + 1 end
        end
        return n
    end

    -- ── sub ─────────────────────────────────────────────────────────────────
    local function sub_impl(s, i, j)
        if type(s) ~= 'string' then return '' end
        j = (j ~= nil) and j or -1
        if not utf8 then return string.sub(s, i, j) end
        local slen = len_impl(s)  -- uses upvalue len_impl, NOT unicode.len
        if i < 0 then i = slen + i + 1 end
        if j < 0 then j = slen + j + 1 end
        if i < 1 then i = 1 end
        if j > slen then j = slen end
        if i > j then return '' end
        local bstart = utf8.offset(s, i)
        if not bstart then return '' end
        local bend
        if j >= slen then
            bend = #s
        else
            local next = utf8.offset(s, j + 1)
            bend = next and (next - 1) or #s
        end
        return string.sub(s, bstart, bend)
    end

    -- ── wtrunc ──────────────────────────────────────────────────────────────
    local function wtrunc_impl(s, width)
        if type(s) ~= 'string' then return '' end
        local w = math.floor(width or 1)
        if w <= 1 then return '' end
        return sub_impl(s, 1, w - 1)  -- upvalue sub_impl
    end

    -- ── Assign to global unicode table ──────────────────────────────────────
    -- Functions captured by upvalue — safe even if 'unicode' is nil later.
    unicode = {
        char      = char_impl,
        len       = len_impl,
        wlen      = len_impl,    -- same impl, captured directly
        wtrunc    = wtrunc_impl,
        sub       = sub_impl,
        lower     = function(s) return type(s)=='string' and string.lower(s) or '' end,
        upper     = function(s) return type(s)=='string' and string.upper(s) or '' end,
        charWidth = function(ch) return 1 end,
        isWide    = function(ch) return false end,
    }
end
");

        }

        // ════════════════════════════════════════════════════════════════════
        // GPU
        // ════════════════════════════════════════════════════════════════════

        private void RegisterGpu()
        {
            lua.DoString(@"gpu = {}");
            lua["gpu.address"] = (Func<string>)(() => gpuAddr);
            lua["gpu.type"] = "gpu";
        }

        private object[] InvokeGpu(string method, object[] args)
        {
            switch (method)
            {
                case "getResolution":
                    return new object[] { (double)screen.Width, (double)screen.Height };
                case "maxResolution":
                {
                    // Real OC: GPU tier caps the resolution.
                    //   T1 = 50×16, T2 = 80×25, T3 = 160×50.
                    // Previously we returned props.screenWidth (=160 always),
                    // which let MineOS request 160×50 even on a tier-1 GPU.
                    int mw = compComp?.Hardware?.GpuWidth  ?? props.screenWidth;
                    int mh = compComp?.Hardware?.GpuHeight ?? props.screenHeight;
                    return new object[] { (double)mw, (double)mh };
                }
                case "setResolution":
                {
                    if (args.Length >= 2)
                    {
                        int maxW = compComp?.Hardware?.GpuWidth  ?? props.screenWidth;
                        int maxH = compComp?.Hardware?.GpuHeight ?? props.screenHeight;
                        int nw = Math.Max(1, Math.Min(ToInt(args[0]), maxW));
                        int nh = Math.Max(1, Math.Min(ToInt(args[1]), maxH));
                        screen.Resize(nw, nh);
                        Log($"[gpu] setResolution {nw}x{nh} (max {maxW}x{maxH})");
                    }
                    return new object[] { true };
                }
                case "getScreen":
                    return new object[] { scrnAddr };
                case "bind":
                    Log($"[gpu] bind {(args.Length > 0 ? args[0] : "?")}");
                    return new object[] { true };
                case "set":
                    if (args.Length >= 3)
                    {
                        bool vert = args.Length >= 4 && args[3] is bool b && b;
                        int sx = ToInt(args[0]) - 1;
                        int sy = ToInt(args[1]) - 1;
                        string stext = args[2]?.ToString() ?? "";
                        screen.Set(sx, sy, stext, vert);
                        // Do NOT update screen.CursorX/Y here — OpenOS manages
                        // the cursor position itself via gpu.setCursor / cursorBlink.
                    }
                    return new object[] { true };
                case "get":
                    if (args.Length >= 2)
                    {
                        var (c, fg, bg) = screen.Get(ToInt(args[0]) - 1, ToInt(args[1]) - 1);
                        long fgn = ((long)fg.r << 16) | ((long)fg.g << 8) | fg.b;
                        long bgn = ((long)bg.r << 16) | ((long)bg.g << 8) | bg.b;
                        return new object[] { c, (double)fgn, (double)bgn, false, false };
                    }
                    return new object[] { " " };
                case "fill":
                    if (args.Length >= 5)
                    {
                        string fillStr = args[4]?.ToString() ?? " ";
                        if (string.IsNullOrEmpty(fillStr)) fillStr = " ";
                        // For single ASCII char use the fast char path; for Unicode use FillStr
                        if (fillStr.Length == 1 && fillStr[0] < 128)
                            screen.Fill(ToInt(args[0]) - 1, ToInt(args[1]) - 1,
                                        ToInt(args[2]), ToInt(args[3]), fillStr[0]);
                        else
                            screen.FillStr(ToInt(args[0]) - 1, ToInt(args[1]) - 1,
                                           ToInt(args[2]), ToInt(args[3]), fillStr);
                    }
                    return new object[] { true };
                case "copy":
                    if (args.Length >= 6)
                        screen.Copy(ToInt(args[0]) - 1, ToInt(args[1]) - 1,
                                    ToInt(args[2]), ToInt(args[3]),
                                    ToInt(args[4]), ToInt(args[5]));
                    return new object[] { true };
                case "setBackground":
                    if (args.Length >= 1)
                    {
                        var prevBG = screen.CurrentBG;
                        long prevBGn = ((long)prevBG.r << 16) | ((long)prevBG.g << 8) | prevBG.b;
                        long c = Convert.ToInt64(args[0]);
                        screen.CurrentBG = new Color32(
                            (byte)((c >> 16) & 0xFF),
                            (byte)((c >> 8) & 0xFF),
                            (byte)(c & 0xFF), 255);
                        return new object[] { (double)prevBGn, false };
                    }
                    return new object[] { 0.0, false };
                case "setForeground":
                    if (args.Length >= 1)
                    {
                        var prevFG = screen.CurrentFG;
                        long prevFGn = ((long)prevFG.r << 16) | ((long)prevFG.g << 8) | prevFG.b;
                        long c = Convert.ToInt64(args[0]);
                        screen.CurrentFG = new Color32(
                            (byte)((c >> 16) & 0xFF),
                            (byte)((c >> 8) & 0xFF),
                            (byte)(c & 0xFF), 255);
                        return new object[] { (double)prevFGn, false };
                    }
                    return new object[] { (double)0xFFFFFF, false };
                case "getBackground":
                    {
                        var bg2 = screen.CurrentBG;
                        long bgn2 = ((long)bg2.r << 16) | ((long)bg2.g << 8) | bg2.b;
                        return new object[] { (double)bgn2, false };
                    }
                case "getForeground":
                    {
                        var fg2 = screen.CurrentFG;
                        long fgn2 = ((long)fg2.r << 16) | ((long)fg2.g << 8) | fg2.b;
                        return new object[] { (double)fgn2, false };
                    }
                case "getPaletteColor":
                    if (args.Length >= 1)
                    {
                        int idx = ToInt(args[0]);
                        if (idx >= 0 && idx < ScreenBuffer.Palette.Length)
                        {
                            var p = ScreenBuffer.Palette[idx];
                            return new object[] {
                                (double)(((int)p.r << 16) | ((int)p.g << 8) | (int)p.b) };
                        }
                    }
                    return new object[] { 0.0 };
                case "setPaletteColor": return new object[] { 0.0 };
                case "getDepth": return new object[] { 8.0 };
                case "maxDepth": return new object[] { 8.0 };
                case "setDepth": return new object[] { 8.0 }; // return previous depth
                case "getViewport": return new object[] { (double)screen.Width, (double)screen.Height };
                case "setViewport": return new object[] { true };
                case "setCursor":
                    if (args.Length >= 2)
                    {
                        screen.CursorX = ToInt(args[0]) - 1;
                        screen.CursorY = ToInt(args[1]) - 1;
                    }
                    return new object[] { true };
                case "getCursor":
                    return new object[] { (double)(screen.CursorX + 1), (double)(screen.CursorY + 1) };
                case "cursorBlink":
                    if (args.Length >= 1 && args[0] is bool blink)
                        screen.CursorVisible = blink;
                    return new object[] { true };

                // ── Tier-3 GPU double-buffer API ────────────────────────────
                // MineOS (and most non-OpenOS systems) uses these to draw
                // off-screen and then bitblt the result. Without these MineOS
                // calls `gpu.allocateBuffer(w,h)`, gets nil, then later does
                // arithmetic on it and crashes with "attempt to compare
                // number with nil" — exactly the BSOD the user reported.
                //
                // We implement a minimal-but-correct shape: every buffer is
                // really the main screen, allocate just hands out a fake
                // index, bitblt is a no-op (writes already happened on the
                // real screen), getBufferSize reports screen size. This
                // disables MineOS's double-buffering smoothness but lets it
                // boot and run; we can swap in real backing buffers later.
                case "allocateBuffer":
                    // args: width, height (or none = use screen size)
                    {
                        int aw = args.Length > 0 ? ToInt(args[0]) : screen.Width;
                        int ah = args.Length > 1 ? ToInt(args[1]) : screen.Height;
                        int idx = ++_gpuBufferNextIdx;
                        _gpuBufferSizes[idx] = (aw, ah);
                        return new object[] { (double)idx };
                    }
                case "freeBuffer":
                    if (args.Length > 0)
                    {
                        int idx = ToInt(args[0]);
                        _gpuBufferSizes.Remove(idx);
                        if (_gpuActiveBuffer == idx) _gpuActiveBuffer = 0;
                    }
                    return new object[] { true };
                case "freeAllBuffers":
                    _gpuBufferSizes.Clear();
                    _gpuActiveBuffer = 0;
                    return new object[] { true };
                case "buffers":
                    {
                        var tab = NewLuaTable();
                        int i = 1;
                        foreach (var k in _gpuBufferSizes.Keys)
                            tab[(double)i++] = (double)k;
                        return new object[] { tab };
                    }
                case "getActiveBuffer":
                    return new object[] { (double)_gpuActiveBuffer };
                case "setActiveBuffer":
                    {
                        int prev = _gpuActiveBuffer;
                        if (args.Length > 0) _gpuActiveBuffer = ToInt(args[0]);
                        return new object[] { (double)prev };
                    }
                case "getBufferSize":
                    {
                        int idx = args.Length > 0 ? ToInt(args[0]) : _gpuActiveBuffer;
                        if (idx == 0)
                            return new object[] { (double)screen.Width, (double)screen.Height };
                        if (_gpuBufferSizes.TryGetValue(idx, out var sz))
                            return new object[] { (double)sz.w, (double)sz.h };
                        return new object[] { (double)screen.Width, (double)screen.Height };
                    }
                case "totalMemory":
                    // Tier-3 GPU has 16 KB of VRAM; report enough so MineOS
                    // is happy that allocateBuffer succeeded.
                    return new object[] { (double)(screen.Width * screen.Height * 4) };
                case "freeMemory":
                    return new object[] { (double)(screen.Width * screen.Height * 2) };
                case "bitblt":
                    // Stub: real bitblt would copy a region between buffers.
                    // Since all our writes go to the main screen anyway,
                    // there's nothing to copy. Return true so MineOS thinks
                    // its frame was committed.
                    return new object[] { true };

                default:
                    return new object[] { null, "unknown gpu method: " + method };
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Filesystem
        // ════════════════════════════════════════════════════════════════════

        private void RegisterFilesystem()
        {
            lua.DoString(@"filesystem = {}");
            lua["filesystem.address"] = (Func<string>)(() => fsAddr);
            lua["filesystem.type"] = "filesystem";
        }

        // Legacy shims kept for loadfile/require which still call these
        private bool FsExists(string path)      => FsExistsIn(path, BuildMergedVfs());
        private bool FsIsDirectory(string path) => IsDirIn(path, BuildMergedVfs());
        private long FsSize(string path)        => FsSizeIn(path, BuildMergedVfs());
        private LuaTable FsList(string path)    => FsListIn(path, BuildMergedVfs());
        private void FsClose(int handle)        => openHandles.Remove(handle);

        private object[] FsRead(int handle, double count)
        {
            if (!openHandles.TryGetValue(handle, out var h)) return new object[] { null, "bad handle" };
            var src = h.ReadVfs ?? BuildMergedVfs();
            if (!src.TryGetValue(h.Path, out var data)) return null; // EOF
            if (h.Pos >= data.Length) return null; // EOF
            int n;
            if (double.IsInfinity(count) || double.IsNaN(count) || count >= int.MaxValue)
                n = data.Length - h.Pos;
            else
                n = Math.Min(Math.Max(0, (int)count), data.Length - h.Pos);
            if (n == 0) return null;
            string chunk = Encoding.UTF8.GetString(data, h.Pos, n);
            h.Pos += n;
            return new object[] { chunk };
        }

        private bool FsWrite(int handle, string content,
            Dictionary<string, byte[]> targetVfs = null)
        {
            if (!openHandles.TryGetValue(handle, out var h)) return false;
            var vfsTarget = targetVfs ?? h.Vfs ?? hddVfs;
            byte[] bytes = Encoding.UTF8.GetBytes(content ?? "");
            if (!vfsTarget.TryGetValue(h.Path, out var cur)) cur = Array.Empty<byte>();
            byte[] newData;
            if (h.Appending || h.Pos >= cur.Length)
            {
                newData = new byte[cur.Length + bytes.Length];
                Buffer.BlockCopy(cur, 0, newData, 0, cur.Length);
                Buffer.BlockCopy(bytes, 0, newData, cur.Length, bytes.Length);
                h.Pos = newData.Length;
            }
            else
            {
                int needed = h.Pos + bytes.Length;
                newData = new byte[Math.Max(cur.Length, needed)];
                Buffer.BlockCopy(cur, 0, newData, 0, cur.Length);
                Buffer.BlockCopy(bytes, 0, newData, h.Pos, bytes.Length);
                h.Pos += bytes.Length;
            }
            vfsTarget[h.Path] = newData;
            InvalidateMergedVfs();
            return true;
        }

        private double FsSeek(int handle, string whence, int offset,
            Dictionary<string, byte[]> merged = null)
        {
            if (!openHandles.TryGetValue(handle, out var h)) return 0;
            var src = merged ?? h.ReadVfs ?? BuildMergedVfs();
            var data = src.TryGetValue(h.Path, out var d) ? d : Array.Empty<byte>();
            if (whence == "set") h.Pos = offset;
            else if (whence == "cur") h.Pos += offset;
            else if (whence == "end") h.Pos = data.Length + offset;
            h.Pos = Math.Max(0, Math.Min(h.Pos, data.Length));
            return h.Pos;
        }

        // Legacy shim — routes to the correct VFS based on address
        private object[] InvokeFs(string method, object[] args) =>
            InvokeFsOnVfs(method, args, hddVfs, readOnly: false);

        private object[] InvokeFsOnVfs(string method, object[] args,
            Dictionary<string, byte[]> targetVfs, bool readOnly)
        {
            if (!_quietInvoke.Contains(method))
                lock (logLock) { Log($"[fs] {method}"); }
            string Str(int i) => i < args.Length ? args[i]?.ToString() ?? "" : "";
            int    Int(int i) => i < args.Length ? ToInt(args[i]) : 0;
            double Dbl(int i) => i < args.Length && args[i] != null ? Convert.ToDouble(args[i]) : 0;

            // For read operations we search ROM first then HDD (merged view).
            // tmpfs is isolated — fs.proxy(tmpAddr) sees ONLY tmpVfs, otherwise
            // OpenOS would treat the tmpfs as the boot disk (it sees /init etc
            // there) and install would refuse to install over it.
            Dictionary<string, byte[]> merged;
            if (object.ReferenceEquals(targetVfs, tmpVfs))
                merged = tmpVfs;
            else
                merged = BuildMergedVfs();

            switch (method)
            {
                case "isReadOnly":
                {
                    string fsName =
                        object.ReferenceEquals(targetVfs, romVfs) ? "rom" :
                        object.ReferenceEquals(targetVfs, hddVfs) ? "hdd" :
                        object.ReferenceEquals(targetVfs, tmpVfs) ? "tmp" : "?";
                    Log("[fs/" + fsName + "] isReadOnly=" + readOnly);
                    return new object[] { readOnly };
                }
                case "spaceTotal":
                    return new object[] {
                        object.ReferenceEquals(targetVfs, tmpVfs) ? (long)64 * 1024
                            : readOnly ? (long)4096000
                            : props.romBytes
                    };
                case "spaceUsed":    return new object[] { targetVfs.Values.Sum(v => v.LongLength) };
                case "exists":       return new object[] { FsExistsIn(Str(0), merged) };
                case "isDirectory":  return new object[] { IsDirIn(Str(0), merged) };
                case "size":         return new object[] { FsSizeIn(Str(0), merged) };
                case "list":         return new object[] { FsListIn(Str(0), merged) };
                case "open":
                    // Writes always go to HDD, reads search merged
                    return FsOpenOn(Str(0), args.Length > 1 ? Str(1) : "r",
                                   targetVfs, merged, readOnly);
                case "close":        FsClose(Int(0)); return new object[] { true };
                case "read":         return FsRead(Int(0), Dbl(1));
                case "write":        return new object[] { FsWrite(Int(0), Str(1), targetVfs) };
                case "seek":         return new object[] { FsSeek(Int(0), Str(1), Int(2), merged) };
                case "makeDirectory":
                    if (readOnly)
                    {
                        // Allow ephemeral directory creation on ROM.
                        // OpenOS boot/90_filesystem.lua calls makeDirectory("/mnt/<addr>/")
                        // on the ROOT (ROM) filesystem to create mount points.
                        // Real OC ROM is read-only for files but allows mkdir in RAM;
                        // we replicate that by adding to romVfs (cleared each reboot
                        // since each boot creates a new OCApi instance).
                        EnsureDir(Str(0), romVfs);
                        InvalidateMergedVfs();
                        return new object[] { true };
                    }
                    EnsureDir(Str(0), targetVfs); InvalidateMergedVfs();
                    return new object[] { true };
                case "remove":
                    if (readOnly) return new object[] { null, "filesystem is read-only" };
                    lock (targetVfs) { targetVfs.Remove(Norm(Str(0))); }
                    InvalidateMergedVfs();
                    return new object[] { true };
                case "rename":
                {
                    if (readOnly) return new object[] { null, "filesystem is read-only" };
                    string nf = Norm(Str(0)), nt = Norm(Str(1));
                    if (!merged.TryGetValue(nf, out var rd)) return new object[] { null, "not found" };
                    lock (targetVfs) { targetVfs[nt] = rd; targetVfs.Remove(nf); }
                    InvalidateMergedVfs();
                    return new object[] { true };
                }
                case "lastModified":
                    return new object[] { DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                case "getLabel":
                    return new object[] {
                        object.ReferenceEquals(targetVfs, tmpVfs) ? "tmpfs"
                            : readOnly ? "rom" : "hdd"
                    };
                case "setLabel":
                    if (readOnly) return new object[] { null, "read-only" };
                    return new object[] { true };
                default:
                    return new object[] { null, "unknown fs method: " + method };
            }
        }

        // ── Per-VFS FS helpers ────────────────────────────────────────────────

        private bool FsExistsIn(string path, Dictionary<string, byte[]> v)
            => v.ContainsKey(Norm(path)) || IsDirIn(path, v);

        private bool IsDirIn(string path, Dictionary<string, byte[]> v)
        {
            string prefix = Norm(path);
            if (!prefix.EndsWith("/")) prefix += "/";
            return v.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        private long FsSizeIn(string path, Dictionary<string, byte[]> v)
            => v.TryGetValue(Norm(path), out var b) ? b.LongLength : 0L;

        private LuaTable FsListIn(string path, Dictionary<string, byte[]> v)
        {
            var result = NewLuaTable();
            string prefix = Norm(path);
            if (prefix.Length > 0 && !prefix.EndsWith("/")) prefix += "/";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int idx = 1;
            foreach (var k in v.Keys)
            {
                if (prefix.Length > 0 && !k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = k.Substring(prefix.Length);
                if (rel == "" || rel == ".dir") continue;
                if (rel.EndsWith("/.dir"))
                {
                    string dirName = rel.Substring(0, rel.Length - 4) + "/";
                    int s2 = dirName.IndexOf('/');
                    string e2 = s2 < 0 ? dirName : dirName.Substring(0, s2 + 1);
                    if (seen.Add(e2)) result[idx++] = e2;
                    continue;
                }
                int slash = rel.IndexOf('/');
                string entry = slash < 0 ? rel : rel.Substring(0, slash) + "/";
                if (entry == ".dir/") continue;
                if (seen.Add(entry)) result[idx++] = entry;
            }
            return result;
        }

        private object[] FsOpenOn(string path, string mode,
            Dictionary<string, byte[]> targetVfs,
            Dictionary<string, byte[]> merged,
            bool readOnly)
        {
            mode = mode ?? "r";
            string npath = Norm(path);
            bool reading  = mode.Contains("r");
            bool appending = mode.Contains("a");
            bool writing  = mode.Contains("w") || appending;

            if (readOnly && writing) return new object[] { null, "filesystem is read-only" };

            if (reading && !merged.ContainsKey(npath))
                return new object[] { null, "file not found: " + path };

            // For writes, always create/truncate in targetVfs (HDD)
            if (writing && !targetVfs.ContainsKey(npath))
            {
                lock (targetVfs)
                {
                    targetVfs[npath] = Array.Empty<byte>();
                    if (!appending) targetVfs[npath] = Array.Empty<byte>();
                }
                InvalidateMergedVfs();
            }

            var readSource = (writing || !reading) ? targetVfs : merged;
            int id = nextHandle++;
            openHandles[id] = new FileHandle
            {
                Path   = npath,
                Writing = writing,
                Appending = appending,
                Pos = appending && readSource.TryGetValue(npath, out var ab) ? ab.Length : 0,
                Vfs = writing ? targetVfs : null,  // null = use merged for reading
                MergedVfs = merged,
            };
            lock (logLock) { Log($"[fs] open '{path}' {mode} → h{id}"); }
            return new object[] { (double)id };
        }

        // ════════════════════════════════════════════════════════════════════
        // Screen
        // ════════════════════════════════════════════════════════════════════

        private void RegisterScreen()
        {
            lua.DoString(@"screen = {}");
            lua["screen.address"] = (Func<string>)(() => scrnAddr);
            lua["screen.type"] = "screen";
        }

        private object[] InvokeScreen(string method, object[] args)
        {
            switch (method)
            {
                case "getAspectRatio": return new object[] { 1.0, 1.0 };
                case "getKeyboards":
                    var k = NewLuaTable(); k[1] = kbdAddr;
                    return new object[] { k };
                case "isColor":   return new object[] { true };
                case "isPrecise": return new object[] { false };
                case "setPrecise":return new object[] { false };
                case "isOn":      return new object[] { true };
                case "turnOn":    return new object[] { true };
                case "turnOff":   return new object[] { true };
                case "getResolution":
                    return new object[] { (double)screen.Width, (double)screen.Height };
                case "setResolution":
                    if (args.Length >= 2)
                    {
                        int maxW = compComp?.Hardware?.GpuWidth  ?? props.screenWidth;
                        int maxH = compComp?.Hardware?.GpuHeight ?? props.screenHeight;
                        int nw = Math.Max(1, Math.Min(ToInt(args[0]), maxW));
                        int nh = Math.Max(1, Math.Min(ToInt(args[1]), maxH));
                        screen.Resize(nw, nh);
                    }
                    return new object[] { true };
                default: return new object[] { null };
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // EEPROM
        // ════════════════════════════════════════════════════════════════════

        private void RegisterEeprom()
        {
            lua.DoString(@"eeprom = {}");
            lua["eeprom.address"] = (Func<string>)(() => eepromAddr);
            lua["eeprom.type"] = "eeprom";
        }

        private object[] InvokeEeprom(string method, object[] args)
        {
            string Str(int i) => i < args.Length ? args[i]?.ToString() ?? "" : "";
            switch (method)
            {
                // get / set — BIOS Lua source code (~4 KB).
                case "get":
                    return new object[] { _biosCode ?? "" };
                case "set":
                {
                    string newCode = args.Length > 0 ? Str(0) : "";
                    _biosCode = newCode;
                    // Notify Comp_Computer so it persists the new code in the save game
                    compComp?.OnBiosFlashed(newCode);
                    Log($"[eeprom] BIOS reflashed ({newCode.Length} bytes)");
                    return new object[] { true };
                }

                // getData / setData — small user data slot (256 B in OC).
                // OpenOS stores the boot-filesystem UUID here via
                // computer.setBootAddress(). Must NOT touch the BIOS code.
                case "getData":
                    return new object[] { _eepromUserData ?? "" };
                case "setData":
                {
                    string newData = args.Length > 0 ? Str(0) : "";
                    if (newData.Length > 256) newData = newData.Substring(0, 256);
                    _eepromUserData = newData;
                    compComp?.OnEepromDataChanged(newData);
                    Log($"[eeprom] data written ({newData.Length} bytes)");
                    return new object[] { true };
                }

                // Boot address: used by OC BIOS to set which filesystem to boot next
                case "getBootAddress": return new object[] { bootAddress };
                case "setBootAddress":
                    if (args.Length > 0 && args[0] is string addr)
                        bootAddress = addr;
                    return new object[] { true };

                case "getLabel":    return new object[] { "BIOS" };
                case "setLabel":    return new object[] { true };
                case "getSize":     return new object[] { (double)4096 };  // BIOS code capacity
                case "getDataSize": return new object[] { (double)256 };   // user-data capacity
                case "getChecksum": return new object[] { ComputeChecksum(_biosCode) };
                case "makeReadonly":return new object[] { false }; // not locked
                default: return new object[] { null };
            }
        }

        private static string ComputeChecksum(string code)
        {
            if (string.IsNullOrEmpty(code)) return "00000000";
            uint h = 0;
            foreach (char c in code) { h ^= (uint)c; h = (h << 5) | (h >> 27); }
            return h.ToString("x8");
        }

        // ════════════════════════════════════════════════════════════════════
        // Raw byte-faithful fs.read / fs.write (KeraLua CFunctions)
        // ════════════════════════════════════════════════════════════════════

        // Registers __fs_read_raw__(handle, count) and __fs_write_raw__(handle,
        // data) as native CFunctions. They bypass NLua's string marshaling
        // (which is UTF-8 in both directions and corrupts non-ASCII bytes)
        // and use lua_pushlstring / lua_tolstring directly via KeraLua's
        // PushBuffer / ToBuffer.
        //
        // We accept the same arguments and return values as the legacy
        // string-based fs.read / fs.write paths so the OpenOS io library
        // (which calls component.fs.read(handle, count) and treats the
        // result as a Lua string) Just Works.
        private void RegisterRawFsIO()
        {
            _fsReadRawFn = new KeraLua.LuaFunction(luaStatePtr =>
            {
                // Pay the per-call CPU throttle just like ComponentInvoke
                // would, so MineOS install / IcePlayer don't bypass it by
                // taking the raw-byte fast path.
                ThrottleInvokeCall();
                var L = KeraLua.Lua.FromIntPtr(luaStatePtr);
                try
                {
                    int handle = (int)L.ToInteger(1);
                    long count;
                    var typ2 = L.Type(2);
                    if (typ2 == KeraLua.LuaType.Number)
                    {
                        double d = L.ToNumber(2);
                        if (double.IsInfinity(d) || double.IsNaN(d) || d >= int.MaxValue)
                            count = int.MaxValue;
                        else if (d <= 0) count = 0;
                        else count = (long)d;
                    }
                    else if (typ2 == KeraLua.LuaType.String)
                    {
                        // OpenOS occasionally passes math.huge or "*a" — be permissive.
                        var s = L.ToString(2, false);
                        if (string.IsNullOrEmpty(s) || s.StartsWith("*"))
                            count = int.MaxValue;
                        else if (!long.TryParse(s, out count)) count = int.MaxValue;
                    }
                    else count = int.MaxValue;

                    if (!openHandles.TryGetValue(handle, out var h))
                    {
                        L.PushNil();
                        L.PushString("bad handle");
                        return 2;
                    }
                    var src = h.ReadVfs ?? BuildMergedVfs();
                    if (!src.TryGetValue(h.Path, out var data) || h.Pos >= data.Length)
                    {
                        L.PushNil();          // EOF
                        return 1;
                    }
                    int remaining = data.Length - h.Pos;
                    int n;
                    if (count <= 0 || count >= int.MaxValue)
                        n = remaining;
                    else
                        n = (int)Math.Min(count, remaining);
                    if (n == 0)
                    {
                        L.PushNil();
                        return 1;
                    }
                    byte[] result = new byte[n];
                    Buffer.BlockCopy(data, h.Pos, result, 0, n);
                    h.Pos += n;
                    L.PushBuffer(result);     // raw bytes — no UTF-8 conversion
                    return 1;
                }
                catch (Exception ex)
                {
                    Log("[fs] read_raw error: " + ex.Message);
                    L.PushNil();
                    L.PushString(ex.Message);
                    return 2;
                }
            });

            _fsWriteRawFn = new KeraLua.LuaFunction(luaStatePtr =>
            {
                ThrottleInvokeCall();
                var L = KeraLua.Lua.FromIntPtr(luaStatePtr);
                try
                {
                    int handle = (int)L.ToInteger(1);
                    byte[] data = L.ToBuffer(2) ?? Array.Empty<byte>();

                    if (!openHandles.TryGetValue(handle, out var h))
                    {
                        L.PushBoolean(false);
                        L.PushString("bad handle");
                        return 2;
                    }
                    var vfsTarget = h.Vfs ?? hddVfs;
                    // Lock around the read-modify-write so the game thread
                    // can take a consistent snapshot for FlushHddToFolder
                    // (it locks the same dict). Without this, install's
                    // ~150 fs.writes race with the post-install reboot's
                    // FlushHdd → "Collection was modified" / native crash.
                    lock (vfsTarget)
                    {
                        if (!vfsTarget.TryGetValue(h.Path, out var cur))
                            cur = Array.Empty<byte>();
                        byte[] newData;
                        if (h.Appending || h.Pos >= cur.Length)
                        {
                            newData = new byte[cur.Length + data.Length];
                            Buffer.BlockCopy(cur, 0, newData, 0, cur.Length);
                            Buffer.BlockCopy(data, 0, newData, cur.Length, data.Length);
                            h.Pos = newData.Length;
                        }
                        else
                        {
                            int needed = h.Pos + data.Length;
                            newData = new byte[Math.Max(cur.Length, needed)];
                            Buffer.BlockCopy(cur, 0, newData, 0, cur.Length);
                            Buffer.BlockCopy(data, 0, newData, h.Pos, data.Length);
                            h.Pos += data.Length;
                        }
                        vfsTarget[h.Path] = newData;
                    }
                    InvalidateMergedVfs();
                    L.PushBoolean(true);
                    return 1;
                }
                catch (Exception ex)
                {
                    Log("[fs] write_raw error: " + ex.Message);
                    L.PushBoolean(false);
                    L.PushString(ex.Message);
                    return 2;
                }
            });

            lua.State.Register("__fs_read_raw__",  _fsReadRawFn);
            lua.State.Register("__fs_write_raw__", _fsWriteRawFn);
        }

        // ════════════════════════════════════════════════════════════════════
        // pullSignal
        // ════════════════════════════════════════════════════════════════════

        private void RegisterPullSignal()
        {
            lua["__comp_pullSignalRaw__"] = (Func<double, LuaTable>)(timeout =>
            {
                // Wall‑clock deadline. Previously we accumulated the *requested*
                // chunk size (50 ms) into `elapsed` on every wakeup — if the
                // semaphore was released early (e.g. by our old 20 Hz heartbeat
                // in Tick()), the loop still credited a full 50 ms, so the
                // effective sleep time was much shorter than requested. Use
                // real elapsed time instead so sleeps match the OC spec.
                bool infinite = double.IsInfinity(timeout) || timeout > 3600.0;
                int totalWaitMs = infinite ? int.MaxValue : Math.Max(1, (int)(timeout * 1000));
                long deadline = infinite
                    ? long.MaxValue
                    : Environment.TickCount + totalWaitMs;
                const int chunkMs = 50;

                while (running)
                {
                    int remaining = infinite
                        ? chunkMs
                        : unchecked((int)(deadline - Environment.TickCount));
                    if (!infinite && remaining <= 0) break;
                    int wait = Math.Min(chunkMs, remaining);

                    signalReady.Wait(wait);

                    lock (signalLock)
                    {
                        if (signalQueue.Count > 0)
                        {
                            var sig = signalQueue.Dequeue();
                            lock (logLock) { Log($"[signal] pulled {sig[0]}"); }
                            var tbl = NewLuaTable();
                            for (int i = 0; i < sig.Length; i++)
                                tbl[i + 1] = sig[i];
                            tbl["n"] = sig.Length;
                            return tbl;
                        }
                    }
                }
                return null;
            });

            lua.DoString(@"
do
    local _raw = __comp_pullSignalRaw__
    computer.pullSignal = function(timeout)
        local raw = _raw(timeout or math.huge)
        if raw == nil then return nil end
        return table.unpack(raw, 1, raw.n)
    end
end
");
        }

        // ════════════════════════════════════════════════════════════════════
        // require (VFS-aware)
        // ════════════════════════════════════════════════════════════════════

        private void RegisterRequire()
        {
            // Expose a C# function that reads a file from VFS as a string.
            // Called from Lua thread — must not use lua.DoString().
            lua["__vfs_read__"] = (Func<string, string>)(path =>
            {
                string n = Norm(path);
                var merged = BuildMergedVfs();
                return merged.TryGetValue(n, out var data)
                    ? Encoding.UTF8.GetString(data)
                    : null;
            });

            lua["__vfs_exists__"] = (Func<string, bool>)(path =>
                BuildMergedVfs().ContainsKey(Norm(path)));

            // Начальная реализация require — совместимая с OpenOS.
            // После того как boot.lua загрузит lib/package.lua, она будет перезаписана
            // настоящим package.require из OpenOS.
            // Важно: НЕ пытаемся вызвать нативный require — он в NLua может
            // конфликтовать. Реализуем полностью сами.
            lua.DoString(@"
do
    -- Сохраняем ссылки на нужные функции до любого обнуления глобалов
    local _vfs_read = __vfs_read__
    local _loadfile = loadfile  -- наш C# loadfile уже установлен выше

    local function _vfs_require(modname)
        -- 1. Уже загружен?
        if package.loaded[modname] ~= nil then
            return package.loaded[modname]
        end
        -- 2. preload?
        if package.preload and package.preload[modname] then
            local result = package.preload[modname](modname)
            package.loaded[modname] = result ~= nil and result or true
            return package.loaded[modname]
        end
        -- 3. Ищем по package.path
        local errs = {}
        local path = package.path or '/lib/?.lua;/usr/lib/?.lua;/lib/core/?.lua'
        for pattern in path:gmatch('[^;]+') do
            local filepath = pattern:gsub('%?', (modname:gsub('%.', '/')))
            local src = _vfs_read(filepath)
            if src then
                local chunk, err = load(src, '=' .. filepath, 'bt', _ENV)
                if chunk then
                    local ok, result = pcall(chunk, modname)
                    if not ok then
                        error(string.format(""error loading module '%s' from file '%s':\n\t%s"",
                            modname, filepath, result), 2)
                    end
                    package.loaded[modname] = result ~= nil and result or true
                    return package.loaded[modname]
                else
                    table.insert(errs, '  ' .. filepath .. ': ' .. tostring(err))
                end
            else
                table.insert(errs, ""  no file '"" .. filepath .. ""'"")
            end
        end
        error(""module '"" .. modname .. ""' not found:\n"" .. table.concat(errs, '\n'), 2)
    end

    require = _vfs_require
end
");
        }

        // ════════════════════════════════════════════════════════════════════
        // loadfile — deadlock-safe version
        // ════════════════════════════════════════════════════════════════════
        // This is called from the Lua thread.
        // We MUST NOT call lua.DoString() here because NLua holds its own
        // monitor and would deadlock.  Instead we call the pre-compiled
        // compileChunkFn (a LuaFunction) which goes directly through the Lua
        // C API without acquiring the NLua wrapper lock again.

        private object[] LuaLoadFileSafe(string filename)
        {
            string path = Norm(filename ?? "");
            var merged = BuildMergedVfs();

            if (!merged.ContainsKey(path))
            {
                string stripped = path.TrimStart('/');
                if (merged.ContainsKey(stripped)) path = stripped;
            }

            if (!merged.TryGetValue(path, out var data))
            {
                lock (logLock) { Log($"[loadfile] NOT FOUND: {filename}"); }
                return new object[] { null, "file not found: " + filename };
            }

            string src = Encoding.UTF8.GetString(data);
            lock (logLock) { Log($"[loadfile] loading {filename} ({src.Length} chars)"); }

            try
            {
                // compileChunkFn is safe to call from the Lua thread
                if (compileChunkFn == null)
                    return new object[] { null, "compiler not initialised" };

                var result = compileChunkFn.Call(src, "=" + filename);
                if (result == null || result.Length == 0 || result[0] == null)
                {
                    string err = result?.Length > 1 ? result[1]?.ToString() : "compile error";
                    lock (logLock) { Log($"[loadfile] compile fail {filename}: {err}"); }
                    return new object[] { null, err };
                }
                return result; // [0]=LuaFunction, possibly [1]=nil
            }
            catch (Exception ex)
            {
                lock (logLock) { Log($"[loadfile] exception {filename}: {ex.Message}"); }
                return new object[] { null, ex.Message };
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Signal system
        // ════════════════════════════════════════════════════════════════════

        public void PushSignal(string name, params object[] args)
        {
            var sig = new object[1 + args.Length];
            sig[0] = name;
            args.CopyTo(sig, 1);
            lock (signalLock) signalQueue.Enqueue(sig);
            try { signalReady.Release(); } catch { }
            lock (logLock) { Log($"[signal] pushed {name}"); }
        }

        // LuaPushSignal removed — pushSignal now uses a Lua varargs wrapper

        // ════════════════════════════════════════════════════════════════════
        // CPU throttle (debug.sethook + per-component-call throttle)
        // ════════════════════════════════════════════════════════════════════
        //
        // We use TWO throttle mechanisms layered together. Either alone is not
        // enough:
        //
        //   1. `debug.sethook(hook, '', N)` fires `hook` every N Lua bytecodes.
        //      Throttles pure-Lua tight loops. UNRELIABLE on some NLua/KeraLua
        //      builds (sometimes silently doesn't fire, depending on how the
        //      hook trampoline is registered).
        //
        //   2. Per-call sleep on every `component.invoke`. ALWAYS works because
        //      every component call passes through ComponentInvoke. Charges a
        //      small wall-clock cost per call, accumulated as "sleep debt" and
        //      paid in 15 ms chunks (Thread.Sleep can't sleep less than the
        //      Windows scheduler tick anyway). Naturally caps the effective
        //      rate of gpu.fill / gpu.set / fs.read / etc.
        //
        // Goal:    program throughput roughly matches an OpenComputers tier-N
        //          machine.  T1 ≈  500 / T2 ≈ 1000 / T3 ≈ 2000 component
        //          invokes per second; pure-Lua bytecode rate ≈ hz × 1000.

        private const int CpuHookInstructions = 2000;

        // Hook state (Lua thread).
        private long _lastHookTicks;
        private int  _hookMinMs = 5;

        // Diagnostic — incremented by the hook, logged by the game thread to
        // confirm whether `debug.sethook` is actually firing in this build.
        private int  _hookFireCount = 0;

        // Per-call invoke throttle. Sleep debt accumulates on every component
        // invoke and is paid out in chunks. Tuned per CPU tier in
        // InstallCpuThrottleHook().
        private double _invokeMsPerCall = 0.5;
        private double _invokeDebtMs    = 0.0;
        private const  int    InvokeSleepChunkMs = 15;
        private readonly object _invokeThrottleLock = new object();

        // Called at the head of every ComponentInvoke. Pays out sleep debt in
        // ~15 ms chunks (the practical Windows Sleep granularity). Skips
        // throttling on shutdown so Dispose can drain quickly.
        private void ThrottleInvokeCall()
        {
            if (!running) return;
            if (_invokeMsPerCall <= 0) return;

            int sleepMs = 0;
            lock (_invokeThrottleLock)
            {
                _invokeDebtMs += _invokeMsPerCall;
                if (_invokeDebtMs >= InvokeSleepChunkMs)
                {
                    sleepMs = (int)_invokeDebtMs;
                    if (sleepMs > 250) sleepMs = 250; // safety clamp
                    _invokeDebtMs = 0.0;
                }
            }
            if (sleepMs > 0)
            {
                try { Thread.Sleep(sleepMs); }
                catch (ThreadInterruptedException) { }
            }
        }

        private void InstallCpuThrottleHook()
        {
            double hz = compComp?.Hardware?.CpuHz ?? 20.0;
            if (hz < 1.0) hz = 20.0;

            // Pure-Lua throttle (count-hook). v5d: reduced target windows by
            // 5× because the prior values were uniformly perceived as too
            // slow during MineOS install/boot. Effective rates with the
            // ~15 ms Windows Sleep granularity:
            //   T1 (5 Hz)  → 80 ms window, ~ 25 000 inst/s
            //   T2 (10 Hz) → 40 ms window, ~ 50 000 inst/s
            //   T3 (20 Hz) → 20 ms window, ~100 000 inst/s
            // T3's 20 ms target is below Sleep granularity so on most ticks
            // the hook returns without sleeping at all → near-native speed.
            // T1/T2 still get noticeable throttle. Tier 3 = "feels native",
            // T1 = "noticeably slow", which matches the design intent.
            _hookMinMs     = Math.Max(1, (int)Math.Round(CpuHookInstructions / hz / 5.0));
            _lastHookTicks = Environment.TickCount;

            // Per-call throttle re-enabled (v5e) at a *very* gentle rate.
            // v5d disabled it entirely after reports of "VERY slow" with
            // 4/hz and 10/hz; that fixed the speed but broke video timing
            // (IcePlayer ran 2-3× too fast on T3) and made OpenOS's cursor
            // blink irregular — both symptoms of unbounded gpu.set/gpu.fill
            // bursts saturating the buffer between os.sleep ticks. With a
            // *small* per-call cost we keep timing predictable without the
            // multi-second compound delays of earlier values:
            //   T1 (5 Hz)  → 0.10 ms/call → ~10 000 invokes/s
            //   T2 (10 Hz) → 0.05 ms/call → ~20 000 invokes/s
            //   T3 (20 Hz) → 0.025 ms/call → ~40 000 invokes/s
            // Even MineOS-style GUI redraws of ~500 gpu.set/frame only
            // accumulate ~12 ms of debt — well under the 50 ms frame
            // budget, so it doesn't visibly throttle responsive UI work.
            _invokeMsPerCall = 0.5 / hz;
            _invokeDebtMs    = 0.0;

            lua["__oc_throttle_hook__"] = (Action)(() =>
            {
                // Diagnostics — periodically logged from the game thread.
                System.Threading.Interlocked.Increment(ref _hookFireCount);

                // On shutdown raise an error so Lua bails out of any tight
                // loop that never yields via pullSignal. Surfaces to
                // biosFn.Call() as a Lua error and the thread's try/catch
                // logs it and exits cleanly.
                if (!running) throw new Exception("[oc] shutdown requested");

                long now    = Environment.TickCount;
                int elapsed = unchecked((int)(now - _lastHookTicks));
                int sleepMs = _hookMinMs - elapsed;
                if (sleepMs > 0)
                {
                    try { Thread.Sleep(sleepMs); }
                    catch (ThreadInterruptedException) { }
                }
                _lastHookTicks = Environment.TickCount;
            });

            try
            {
                // Install the count hook via a Lua trampoline. Lua passes
                // (event, line) to the hook function; our trampoline drops
                // those args and calls into C# with no parameters, which
                // sidesteps NLua's strict-arity delegate dispatch (which
                // silently swallows hook fires on signature mismatch).
                lua.DoString(
                    "debug.sethook(function() __oc_throttle_hook__() end, '', "
                    + CpuHookInstructions + ")");
                Log(
                    $"[cpu] throttle installed: {hz:0.#} Hz  hook=every {CpuHookInstructions} inst sleep≤{_hookMinMs}ms" +
                    $"  invoke={_invokeMsPerCall:0.##}ms/call");
            }
            catch (Exception ex)
            {
                Log($"[cpu] failed to install debug.sethook: {ex.Message}");
                Log("[cpu] falling back to per-call invoke throttle only");
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Boot Process
        // ════════════════════════════════════════════════════════════════════

        public void StartBios()
        {
            Log("[boot] Starting BIOS...");

            // Clear the C# screen buffer so any C#-side splash is gone before BIOS draws.
            screen.Clear();

            string biosCode = _biosCode;
            if (string.IsNullOrEmpty(biosCode))
            {
                Log("[boot] ERROR: BIOS code is empty!");
                screen.Println("ERROR: No BIOS found!");
                return;
            }

            Log($"[boot] BIOS size: {biosCode.Length} bytes");
            Log("[boot] Compiling BIOS...");

            object[] compileResult;
            try
            {
                // Safe: we're on the game thread, not inside a Lua callback
                lua["__bios_src__"] = biosCode;
                compileResult = lua.DoString(
                    "local f,e = load(__bios_src__, '=bios', 'bt', _ENV); __bios_src__=nil; return f, e");
                lua["__bios_src__"] = null;
            }
            catch (Exception ex)
            {
                lock (logLock)
                {
                    Log("[boot] ====== BIOS compile exception ======");
                    Log($"[boot] {ex.Message}");
                    if (!string.IsNullOrEmpty(ex.StackTrace))
                        foreach (var ln in ex.StackTrace.Split('\n'))
                            Log("[boot] " + ln.TrimEnd('\r'));
                    Log("[boot] ====================================");
                }
                screen.CurrentBG = new Color32(0, 0, 170, 255);
                screen.CurrentFG = new Color32(255, 255, 255, 255);
                screen.Fill(0, 0, screen.Width, screen.Height, ' ');
                screen.Set(2, 2, "*** BIOS COMPILE EXCEPTION ***");
                int rrow = 4, mw = Math.Max(20, screen.Width - 4);
                foreach (var rawLine in ex.Message.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    int pos = 0;
                    while (pos < line.Length && rrow < screen.Height - 1)
                    {
                        int take = Math.Min(mw, line.Length - pos);
                        screen.Set(2, rrow++, line.Substring(pos, take));
                        pos += take;
                    }
                }
                screen.CurrentBG = new Color32(0, 0, 0, 255);
                return;
            }

            if (compileResult == null || compileResult.Length == 0 ||
                !(compileResult[0] is LuaFunction biosFn))
            {
                string err = compileResult?.Length > 1
                    ? compileResult[1]?.ToString() : "unknown";
                lock (logLock)
                {
                    Log("[boot] ====== BIOS compile failed (syntax error in BIOS) ======");
                    foreach (var ln in (err ?? "").Split('\n'))
                        Log("[boot] " + ln.TrimEnd('\r'));
                    Log($"[boot] BIOS source length: {biosCode?.Length ?? 0} bytes");
                    Log("[boot] First 200 chars: " +
                        (biosCode != null && biosCode.Length > 0
                         ? biosCode.Substring(0, Math.Min(200, biosCode.Length))
                              .Replace("\n", "\\n").Replace("\r", "")
                         : "(empty)"));
                    Log("[boot] =========================================================");
                }
                screen.CurrentBG = new Color32(0, 0, 170, 255);
                screen.CurrentFG = new Color32(255, 255, 255, 255);
                screen.Fill(0, 0, screen.Width, screen.Height, ' ');
                screen.Set(2, 2, "*** BIOS SYNTAX ERROR ***");
                int rrow = 4, mw = Math.Max(20, screen.Width - 4);
                foreach (var rawLine in (err ?? "").Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    int pos = 0;
                    while (pos < line.Length && rrow < screen.Height - 2)
                    {
                        int take = Math.Min(mw, line.Length - pos);
                        screen.Set(2, rrow++, line.Substring(pos, take));
                        pos += take;
                    }
                }
                if (rrow < screen.Height - 1)
                    screen.Set(2, screen.Height - 2,
                        "Open Debug Console for full source dump.");
                screen.CurrentBG = new Color32(0, 0, 0, 255);
                return;
            }

            Log("[boot] BIOS compiled OK. Pushing init signal...");
            PushSignal("init");

            // Re-inject unicode into _G just before the thread starts.
            // OpenOS boot scripts may create sub-environments; by doing this
            // last we ensure it's in the Lua state when the BIOS thread begins.
            try
            {
                lua.DoString("rawset(_G, 'unicode', unicode)");
            }
            catch { }

            // ── CPU throttle (debug.sethook) ─────────────────────────────────
            // Real OC caps Lua execution at 5/10/20 Hz "clock" ticks where
            // each tick allows a capped number of instructions before the
            // thread is suspended until the next tick. We approximate that by
            // installing an instruction-count hook that sleeps the Lua thread
            // for the remainder of the current tick window whenever a budget
            // (_cpuBudgetInst) has been spent.
            InstallCpuThrottleHook();

            luaThread = new Thread(() =>
            {
                try
                {
                    Log("[boot] Lua thread started");
                    biosFn.Call();
                    Log("[boot] BIOS returned (clean shutdown)");
                }
                catch (ThreadAbortException)
                {
                    Log("[boot] Lua thread aborted");
                }
                catch (Exception ex)
                {
                    // Surface the FULL Lua/BIOS error to:
                    //   1. The debug log (visible in "Open Debug Console"
                    //      gizmo / Dialog_ComputerDebug). Custom BIOSes such
                    //      as MineOS sometimes silently `pcall` errors and
                    //      never print them, so we make sure the host log
                    //      always has them, including the stack trace and
                    //      any nested InnerExceptions.
                    //   2. A blue-screen fatal on the in-game monitor with
                    //      the message word-wrapped over several lines.
                    string fullMsg = ex.Message ?? "(no message)";
                    string stack   = ex.StackTrace ?? "";
                    var inner = ex.InnerException;
                    int innerDepth = 0;
                    while (inner != null && innerDepth < 4)
                    {
                        fullMsg += "\n  caused by: " + (inner.Message ?? "(no message)");
                        if (!string.IsNullOrEmpty(inner.StackTrace))
                            stack += "\n--- inner trace ---\n" + inner.StackTrace;
                        inner = inner.InnerException;
                        innerDepth++;
                    }

                    lock (logLock)
                    {
                        Log("[boot] ====== BIOS / Lua fatal error ======");
                        foreach (var line in fullMsg.Split('\n'))
                            Log("[boot] " + line.TrimEnd('\r'));
                        if (!string.IsNullOrEmpty(stack))
                        {
                            Log("[boot] --- stack trace ---");
                            foreach (var line in stack.Split('\n'))
                                Log("[boot] " + line.TrimEnd('\r'));
                        }
                        Log("[boot] ====================================");
                    }

                    // Blue screen of death — render the error word-wrapped
                    // over the middle rows so the user can read it without
                    // having to open the host log.
                    try
                    {
                        screen.CurrentBG = new Color32(0, 0, 170, 255);
                        screen.CurrentFG = new Color32(255, 255, 255, 255);
                        screen.Fill(0, 0, screen.Width, screen.Height, ' ');

                        int row = Math.Max(0, screen.Height / 2 - 6);
                        int maxw = Math.Max(20, screen.Width - 4);
                        screen.Set(2, row++, "*** RIMCOMPUTERS BIOS FATAL ***");
                        row++;

                        // Show up to ~10 wrapped lines of the error.
                        int linesShown = 0;
                        foreach (var rawLine in fullMsg.Split('\n'))
                        {
                            string line = rawLine.TrimEnd('\r');
                            int pos = 0;
                            while (pos < line.Length && linesShown < 10 && row < screen.Height)
                            {
                                int take = Math.Min(maxw, line.Length - pos);
                                screen.Set(2, row++, line.Substring(pos, take));
                                pos += take;
                                linesShown++;
                            }
                            if (linesShown >= 10) break;
                        }

                        if (row < screen.Height - 1)
                        {
                            row = screen.Height - 2;
                            screen.Set(2, row, "See Open Debug Console for full trace.");
                        }
                        screen.CurrentBG = new Color32(0, 0, 0, 255);
                    }
                    catch { /* don't let display failure mask the original error */ }
                }
                finally
                {
                    running = false;
                    Log("[boot] Lua thread exited");
                }
            })
            {
                Name = "RimComputers-Lua",
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.BelowNormal
            };
            luaThread.Start();
        }

        // ════════════════════════════════════════════════════════════════════
        // Game loop (called from RimWorld tick thread)
        // ════════════════════════════════════════════════════════════════════

        // OC Tier-3 CPU: 20 executions/sec. RimWorld runs at 60 ticks/sec.
        // We push input signals every tick, but only wake the Lua thread
        // every 3 ticks (~20Hz) so it matches real OC timing.
        private int _luaTickAccum = 0;
        private const int LuaTickInterval = 3; // 60 TPS / 3 = 20Hz

        public void Tick()
        {
            if (!running) return;

            // uptimeSeconds is wall‑clock based now (see Stopwatch above). No
            // need to drive it from TicksGame anymore — doing so would make
            // computer time advance 3× faster on 3× game speed and stall when
            // the game is paused, neither of which matches how `os.sleep`
            // actually blocks.
            ramUsed = (openHandles.Count * 256L) + (signalQueue.Count * 128L) + 8192L;

            // Always drain input from the UI thread into the signal queue.
            // charCode: OC sends the full Unicode codepoint, not just ASCII.
            while (InputBuffer.TryDequeueKeyDown(out char kc, out int sc))
            {
                double charCode = kc >= 32 ? (double)kc : 0.0;
                PushSignal("key_down", kbdAddr, charCode, (double)sc, kbdAddr);
            }

            while (InputBuffer.TryDequeueKeyUp(out char ku, out int su))
            {
                double charCode = ku >= 32 ? (double)ku : 0.0;
                PushSignal("key_up", kbdAddr, charCode, (double)su, kbdAddr);
            }

            // Play beeps on game thread (Unity audio API is main-thread only)
            while (true)
            {
                (float freq, float dur) beep;
                lock (signalLock)
                {
                    if (_beepQueue.Count == 0) break;
                    beep = _beepQueue.Dequeue();
                }
                PlayBeep(beep.freq, beep.dur);
            }

            // NOTE: we used to release an extra semaphore slot here every ~50 ms
            // as a "heartbeat" for pullSignal. That caused os.sleep(d) to return
            // ~d/2 on average (wakeup could fire any time in the window, and the
            // loop counted the full requested wait towards `elapsed`). Combined
            // with the game‑speed‑based uptime this compounded into the 2–3×
            // speedup reported in IcePlayer. pullSignal / os.sleep now track
            // real wall‑clock elapsed time themselves, so no heartbeat is
            // needed; idle pullSignal just times out its 50 ms chunk and loops.
            _luaTickAccum++;
            if (_luaTickAccum >= LuaTickInterval) _luaTickAccum = 0;

            // ── debug.sethook diagnostics ────────────────────────────────────
            // Once every ~5 s log how many times the Lua count-hook fired.
            // If this stays at 0 the per-call invoke throttle is the only
            // thing limiting CPU — useful for confirming whether NLua's
            // debug.sethook works on the user's box.
            long nowMs = Environment.TickCount;
            if (_lastHookLogMs == 0) _lastHookLogMs = nowMs;
            if (nowMs - _lastHookLogMs >= 5000)
            {
                int fires = System.Threading.Interlocked.Exchange(ref _hookFireCount, 0);
                int dt    = unchecked((int)(nowMs - _lastHookLogMs));
                _lastHookLogMs = nowMs;
                lock (logLock) { Log($"[cpu] hook fires={fires} in {dt}ms (bytecodes={fires * CpuHookInstructions})"); }
            }
        }
        private long _lastHookLogMs = 0;

        private void PlayBeep(float frequency, float duration)
        {
            try
            {
                const int sampleRate = 22050;
                int samples = Mathf.Max(1, (int)(sampleRate * duration));
                var data = new float[samples];
                float fadeStart = samples * 0.85f;
                for (int i = 0; i < samples; i++)
                {
                    float t = (float)i / sampleRate;
                    float fade = i < fadeStart ? 1f
                                 : 1f - (i - fadeStart) / (samples - fadeStart);
                    data[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * 0.4f * fade;
                }
                var clip = AudioClip.Create("beep", samples, 1, sampleRate, false);
                clip.SetData(data, 0);

                if (_beepSource == null)
                {
                    var go = GameObject.Find("RimCompBeepSource");
                    if (go == null)
                    {
                        go = new GameObject("RimCompBeepSource");
                        UnityEngine.Object.DontDestroyOnLoad(go);
                    }
                    _beepSource = go.GetComponent<AudioSource>()
                               ?? go.AddComponent<AudioSource>();
                    _beepSource.spatialBlend = 0f;
                    _beepSource.volume = 0.6f;
                }
                _beepSource.PlayOneShot(clip);
            }
            catch (Exception ex) { Log($"[beep] error: {ex.Message}"); }
        }

        public void PushKeySignal(char ch, KeyCode keyCode)
        {
            double charCode = (ch >= 32 && ch <= 127) ? (double)ch : 0.0;
            PushSignal("key_down", kbdAddr, charCode, (double)keyCode, kbdAddr);
            PushSignal("key_up", kbdAddr, charCode, (double)keyCode, kbdAddr);
        }

        public void PushClipboardSignal(string text)
        {
            if (!string.IsNullOrEmpty(text))
                PushSignal("clipboard", kbdAddr, text, kbdAddr);
        }

        public void ExecuteLua(string code)
        {
            if (lua == null || !running) return;
            try { lua.DoString(code); }
            catch (Exception ex) { Log($"[lua] exec error: {ex.Message}"); }
        }

        // ════════════════════════════════════════════════════════════════════
        // Writable VFS snapshot
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns all VFS entries that are NOT original ROM files.
        /// These are files created or modified by OpenOS at runtime.
        /// <summary>Returns hddVfs snapshot (backward compat for legacy callers).</summary>
        public Dictionary<string, byte[]> GetWriteableVfsSnapshot() =>
            new Dictionary<string, byte[]>(hddVfs, StringComparer.OrdinalIgnoreCase);

        // ════════════════════════════════════════════════════════════════════
        // Dispose
        // ════════════════════════════════════════════════════════════════════

        public void Dispose()
        {
            running = false;

            // Wake the Lua thread if it's blocked in pullSignal so it can exit.
            // The CPU throttle hook also raises on running==false so any
            // Lua‑side tight loop will break out within CpuHookInstructions
            // bytecodes.
            try { signalReady.Release(100); } catch { }

            // Do NOT call lua.Dispose() / signalReady.Dispose() / luaThread.Join()
            // on the game thread:
            //   - Join() would freeze the game.
            //   - Disposing NLua's native state while the Lua thread is still
            //     executing triggers AccessViolation in the Lua VM. This is
            //     almost certainly what caused the occasional crash-on-shutdown
            //     reported by users.
            //
            // Hand cleanup off to the ThreadPool. Only dispose native
            // resources AFTER the Lua thread has fully exited. If the thread
            // is genuinely stuck (e.g. inside a PInvoke we can't interrupt),
            // LEAK rather than risk a crash — a Lua state leak on shutdown
            // is minor; Thread.Abort() was removed because it could return
            // while the native Lua state was only partially unwound.
            var threadToJoin = luaThread;
            var luaToDispose = lua;
            var semToDispose = signalReady;
            luaThread = null;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                bool exited = true;
                try
                {
                    if (threadToJoin != null && threadToJoin.IsAlive)
                        exited = threadToJoin.Join(5000);
                }
                catch { exited = false; }

                if (!exited)
                {
                    try
                    {
                        Verse.Log.Warning(
                            "[RimComputers] Lua thread did not exit in 5s; " +
                            "leaking Lua state to avoid a native crash");
                    }
                    catch { }
                    return; // do NOT dispose — Lua thread is still using it
                }

                try { luaToDispose?.Dispose(); } catch { }
                try { semToDispose?.Dispose(); } catch { }
            });
        }

        // ════════════════════════════════════════════════════════════════════
        // Helpers
        // ════════════════════════════════════════════════════════════════════

        private void EnsureDir(string p, Dictionary<string, byte[]> target = null)
        {
            string m = Norm(p) + "/.dir";
            var v = target ?? hddVfs;
            if (!v.ContainsKey(m)) { v[m] = Array.Empty<byte>(); InvalidateMergedVfs(); }
        }

        private void WriteText(string path, string text,
            Dictionary<string, byte[]> target = null)
        {
            var v = target ?? hddVfs;
            v[Norm(path)] = Encoding.UTF8.GetBytes(text);
            InvalidateMergedVfs();
        }

        // ── VFS ↔ real folder sync ─────────────────────────────────────────────

        private static void LoadFolderToVfs(string folder,
            Dictionary<string, byte[]> target)
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(folder.Length).TrimStart(Path.DirectorySeparatorChar,
                                                                       Path.AltDirectorySeparatorChar)
                                  .Replace('\\', '/');
                try { target[rel] = File.ReadAllBytes(file); }
                catch { /* skip locked files */ }
            }
        }

        /// <summary>Flush hddVfs to the real HDD folder on disk.</summary>
        public void FlushHddToFolder()
        {
            if (string.IsNullOrEmpty(hddFolderPath)) return;
            try
            {
                // Snapshot first. Without this, iterating `hddVfs` directly
                // races with Lua-thread writes (every fs.write goes through
                // InvokeFsOnVfs → hddVfs[path] = bytes). On reboot/shutdown
                // the Lua thread is still alive during FlushHddToFolder
                // (Dispose only triggers a ThreadPool worker join, not a
                // synchronous one) — so concurrent Dictionary mutation
                // throws "Collection was modified" or, worse, corrupts
                // internal state and the .NET runtime crashes the
                // process. The user's reboot-after-install crash is
                // almost certainly this race: install just wrote ~150
                // files, then reboot triggered FlushHdd while the Lua
                // thread was still finishing the last few writes.
                KeyValuePair<string, byte[]>[] snapshot;
                lock (hddVfs)
                {
                    snapshot = new KeyValuePair<string, byte[]>[hddVfs.Count];
                    int i = 0;
                    foreach (var kv in hddVfs) snapshot[i++] = kv;
                }
                Directory.CreateDirectory(hddFolderPath);
                int count = 0;
                foreach (var kv in snapshot)
                {
                    if (kv.Key.EndsWith("/.dir")) continue; // skip dir markers
                    string fullPath = Path.Combine(hddFolderPath,
                        kv.Key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                    File.WriteAllBytes(fullPath, kv.Value);
                    count++;
                }
                Log($"[hdd] Flushed {count} entries to {hddFolderPath}");
            }
            catch (Exception ex) { Log($"[hdd] Flush error: {ex.Message}"); }
        }

        private string Norm(string p)
            => (p ?? "").Replace('\\', '/').TrimStart('/');

        private static string NewGuid8()
            => Guid.NewGuid().ToString(); // full UUID: "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx" (36 chars)

        /// <summary>
        /// Creates a stable 36-char UUID from a seed string + role tag.
        /// Same input always produces the same output, so component addresses
        /// survive reboots and the eeprom boot address remains valid.
        /// </summary>
        private static string DeterministicGuid(string seed, string role)
        {
            // Use MD5 of seed+role as UUID bytes
            using var md5 = System.Security.Cryptography.MD5.Create();
            byte[] input = System.Text.Encoding.UTF8.GetBytes(seed + ":" + role);
            byte[] hash = md5.ComputeHash(input);
            // Format as RFC-4122 UUID string
            return $"{hash[0]:x2}{hash[1]:x2}{hash[2]:x2}{hash[3]:x2}-" +
                   $"{hash[4]:x2}{hash[5]:x2}-" +
                   $"{hash[6]:x2}{hash[7]:x2}-" +
                   $"{hash[8]:x2}{hash[9]:x2}-" +
                   $"{hash[10]:x2}{hash[11]:x2}{hash[12]:x2}{hash[13]:x2}{hash[14]:x2}{hash[15]:x2}";
        }

        private static int _tmpCounter;
        private LuaTable NewLuaTable()
        {
            string tmp = "__t" + (_tmpCounter++) + "__";
            lua.NewTable(tmp);
            var tbl = lua[tmp] as LuaTable;
            lua[tmp] = null;
            return tbl;
        }

        private LuaTable MakeResultTable(bool ok, object[] result, string error)
        {
            var tbl = NewLuaTable();
            tbl["ok"] = ok;
            if (ok)
            {
                var r = NewLuaTable();
                if (result != null)
                    for (int i = 0; i < result.Length; i++)
                        r[i + 1] = result[i];
                tbl["result"] = r;
            }
            else
            {
                tbl["err"] = error ?? "error";
            }
            return tbl;
        }

        private static int ToInt(object o)
        {
            if (o is double d) return (int)d;
            if (o is long l) return (int)l;
            if (o is int i) return i;
            return Convert.ToInt32(o);
        }

        private void Log(string msg)
        {
            // Forward to Comp_Computer.Log so the line both reaches the
            // visible Debug Console list (compComp.debugLog === this.log,
            // shared instance) AND hits debug.log on disk via
            // _debugLogWriter. We DELIBERATELY do NOT add to `log`
            // ourselves — Comp_Computer.Log already does that, and
            // adding twice was the root cause of v5h's duplicate-line
            // pattern ("[tick] msg" + "[tick] [oc] msg" both visible).
            // If compComp is null (game not yet spawned, edge case),
            // fall back to writing into the shared list directly.
            if (compComp != null)
            {
                try { compComp.Log("[oc] " + msg); return; } catch { }
            }
            lock (logLock)
            {
                string entry = "[" + (Find.TickManager?.TicksGame ?? 0) + "] " + msg;
                log.Add(entry);
                if (log.Count > 10000) log.RemoveAt(0);
            }
        }
    }
}