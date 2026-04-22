using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NLua;
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
        private readonly string hddAddr;   // HDD (writable)
        // fsAddr kept for backward-compat routing — points to hddAddr
        private string fsAddr => hddAddr;
        private string internetAddr; // null when internet card is disabled

        // Internet card state (toggled via gizmo)
        private volatile bool internetEnabled = false;

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
        private float uptimeSeconds = 0f;

        // File handles
        private int nextHandle = 1;
        private readonly Dictionary<int, FileHandle> openHandles =
            new Dictionary<int, FileHandle>();

        // Memory tracking
        private long ramUsed = 0, romUsed = 0;
        public long RamUsed => ramUsed;
        public long RomUsed => romUsed;

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
                     Comp_Computer compComp = null)
        {
            LoadNativeLua();

            this.lua = new Lua();
            this.screen = screen;
            this.props = props;
            this.log = log;
            this.building = building;
            this.compComp = compComp;
            this.hddFolderPath = hddFolderPath;

            lua.UseTraceback = true;

            // ── Per-computer BIOS code ────────────────────────────────────────────
        // Loaded from ROM/BIOS/bios.lua on first boot; can be reflashed by flash.lua.
        private string _biosCode;
        public  string BiosCode { get => _biosCode; set => _biosCode = value ?? ""; }

        // Eeprom user data (arbitrary storage for Lua programs)
        private string _eepromUserData = "";

        public OCApi(ScreenBuffer screen, CompProperties_Computer props,
                     List<string> log, Thing building,
                     string hddFolderPath = null,
                     Dictionary<string, byte[]> legacyVfsMigration = null,
                     string savedBiosCode = null,
                     Comp_Computer compComp = null)
        {
            LoadNativeLua();

            this.lua = new Lua();
            this.screen = screen;
            this.props = props;
            this.log = log;
            this.building = building;
            this.compComp = compComp;
            this.hddFolderPath = hddFolderPath;

            lua.UseTraceback = true;

            string baseId = building?.ThingID ?? Guid.NewGuid().ToString("N");
            gpuAddr    = DeterministicGuid(baseId, "gpu");
            scrnAddr   = DeterministicGuid(baseId, "screen");
            romAddr    = DeterministicGuid(baseId, "rom");
            hddAddr    = DeterministicGuid(baseId, "hdd");
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
            // tmpAddress ДОЛЖНА быть функцией — OpenOS вызывает computer.tmpAddress()
            lua["computer.tmpAddress"] = (Func<string>)(() => fsAddr);
            lua["computer.totalMemory"] = (Func<long>)(() => props.ramBytes);
            lua["computer.freeMemory"] = (Func<long>)(() => props.ramBytes - ramUsed);
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
                // We replicate that: wait up to 'secs' seconds on the semaphore
                // (draining and discarding any signals that arrive).
                if (secs <= 0) return;
                int totalMs = Math.Min((int)(secs * 1000), 300_000); // cap 5 min
                int elapsed  = 0;
                const int chunk = 50;
                while (running && elapsed < totalMs)
                {
                    int wait = Math.Min(chunk, totalMs - elapsed);
                    signalReady.Wait(wait);
                    // Drain any signals that woke us so they don't pile up.
                    lock (signalLock)
                    {
                        while (signalQueue.Count > 0) signalQueue.Dequeue();
                    }
                    elapsed += wait;
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
            lua["__inet_read__"] = (Func<double, string>)(hid =>
            {
                int id = (int)hid;
                if (!_internetResponses.TryGetValue(id, out var state)) return null;
                if (state.pos >= state.data.Length)
                {
                    _internetResponses.Remove(id);
                    return null; // EOF
                }
                int chunk = Math.Min(2048, state.data.Length - state.pos);
                string part = state.data.Substring(state.pos, chunk);
                _internetResponses[id] = (state.data, state.pos + chunk);
                return part;
            });
            lua["__inet_close__"] = (Action<double>)(hid =>
            {
                _internetResponses.Remove((int)hid);
            });
            lua["__inet_response__"] = (Func<double, string>)(hid =>
            {
                // Return full response at once (simpler for wget/pastebin usage)
                int id = (int)hid;
                if (!_internetResponses.TryGetValue(id, out var state)) return null;
                string all = state.data;
                _internetResponses.Remove(id);
                return all;
            });
            // Wrap the internet handle table with proper read/close methods
            lua.DoString(@"
do
    local _inet_read  = __inet_read__
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
    local function invoke_method(method, ...)
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
                (hddAddr,    "filesystem"),   // HDD (writable)
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
        };

        // Called from Lua wrapper (Lua thread). args = {[1]=v1,[2]=v2,...}
        private LuaTable ComponentInvoke(string addr, string method, LuaTable args)
        {
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
                case "tmpAddress": return new object[] { fsAddr };
                case "freeMemory": return new object[] { (double)(props.ramBytes - ramUsed) };
                case "totalMemory": return new object[] { (double)props.ramBytes };
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

                            string responseStr = System.Text.Encoding.UTF8.GetString(responseBytes);
                            int hid = nextHandle++;
                            _internetResponses[hid] = (responseStr, 0);

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

        // Storage for internet response streams
        private readonly Dictionary<int, (string data, int pos)> _internetResponses =
            new Dictionary<int, (string, int)>();

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
                    return new object[] { (double)props.screenWidth, (double)props.screenHeight };
                case "setResolution":
                {
                    if (args.Length >= 2)
                    {
                        int nw = Math.Max(1, Math.Min(ToInt(args[0]), props.screenWidth));
                        int nh = Math.Max(1, Math.Min(ToInt(args[1]), props.screenHeight));
                        screen.Resize(nw, nh);
                        Log($"[gpu] setResolution {nw}x{nh}");
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

            // For read operations we search ROM first then HDD (merged view)
            var merged = BuildMergedVfs();

            switch (method)
            {
                case "isReadOnly":   return new object[] { readOnly };
                case "spaceTotal":   return new object[] { readOnly ? (long)4096000 : props.romBytes };
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
                    targetVfs.Remove(Norm(Str(0))); InvalidateMergedVfs();
                    return new object[] { true };
                case "rename":
                {
                    if (readOnly) return new object[] { null, "filesystem is read-only" };
                    string nf = Norm(Str(0)), nt = Norm(Str(1));
                    if (!merged.TryGetValue(nf, out var rd)) return new object[] { null, "not found" };
                    targetVfs[nt] = rd; targetVfs.Remove(nf); InvalidateMergedVfs();
                    return new object[] { true };
                }
                case "lastModified":
                    return new object[] { DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                case "getLabel": return new object[] { readOnly ? "rom" : "hdd" };
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
                targetVfs[npath] = Array.Empty<byte>();
                if (!appending) targetVfs[npath] = Array.Empty<byte>();
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
                        int nw = Math.Max(1, Math.Min(ToInt(args[0]), props.screenWidth));
                        int nh = Math.Max(1, Math.Min(ToInt(args[1]), props.screenHeight));
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
                // getData / get: returns the current BIOS Lua code (what flash.lua reads)
                case "getData":
                case "get":
                    return new object[] { _biosCode ?? "" };

                // setData / set: flashes new BIOS code (persisted via Comp_Computer save)
                case "setData":
                case "set":
                {
                    string newCode = args.Length > 0 ? Str(0) : "";
                    _biosCode = newCode;
                    // Notify Comp_Computer so it persists the new code in the save game
                    compComp?.OnBiosFlashed(newCode);
                    Log($"[eeprom] BIOS reflashed ({newCode.Length} bytes)");
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
                case "getSize":     return new object[] { (double)65536 }; // 64KB eeprom
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
        // pullSignal
        // ════════════════════════════════════════════════════════════════════

        private void RegisterPullSignal()
        {
            lua["__comp_pullSignalRaw__"] = (Func<double, LuaTable>)(timeout =>
            {
                bool infinite = double.IsInfinity(timeout) || timeout > 3600.0;
                int totalWaitMs = infinite ? int.MaxValue : Math.Max(1, (int)(timeout * 1000));
                int elapsed = 0;
                const int chunkMs = 50;

                while (running)
                {
                    int wait = Math.Min(chunkMs, totalWaitMs - elapsed);
                    if (wait <= 0) break;

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

                    if (!infinite)
                    {
                        elapsed += wait;
                        if (elapsed >= totalWaitMs) break;
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
                Log($"[boot] Compile exception: {ex.Message}");
                screen.Println("BIOS compile error: " + ex.Message);
                return;
            }

            if (compileResult == null || compileResult.Length == 0 ||
                !(compileResult[0] is LuaFunction biosFn))
            {
                string err = compileResult?.Length > 1
                    ? compileResult[1]?.ToString() : "unknown";
                Log($"[boot] BIOS compile failed: {err}");
                screen.Println("BIOS compile failed: " + err);
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
                    lock (logLock) { Log($"[boot] BIOS fatal: {ex.Message}"); }

                    // Blue screen of death
                    screen.CurrentBG = new Color32(0, 0, 170, 255);
                    screen.CurrentFG = new Color32(255, 255, 255, 255);
                    screen.Fill(0, 0, screen.Width, screen.Height, ' ');
                    string msg = ex.Message.Length > screen.Width - 8
                        ? ex.Message.Substring(0, screen.Width - 8)
                        : ex.Message;
                    screen.Set(0, screen.Height / 2, "FATAL: " + msg);
                    screen.CurrentBG = new Color32(0, 0, 0, 255);
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

            long gameTicks = Find.TickManager?.TicksGame ?? 0;
            uptimeSeconds = gameTicks * (1f / 60f);
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

            // Throttle Lua wakeups to ~20Hz so the computer doesn't run
            // at unbounded speed. The Lua thread sleeps in pullSignal until
            // we release the semaphore here.
            _luaTickAccum++;
            if (_luaTickAccum >= LuaTickInterval)
            {
                _luaTickAccum = 0;
                // Release one extra semaphore slot to wake pullSignal for the
                // next "clock" tick. This simulates the OC 20Hz interrupt.
                // Only release if nothing is already queued (avoid pileup).
                lock (signalLock)
                {
                    if (signalQueue.Count == 0)
                    {
                        try { signalReady.Release(); } catch { }
                    }
                }
            }
        }

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
            try { signalReady.Release(100); } catch { }

            // Do NOT call lua.Dispose() or luaThread.Join() on the game thread:
            //   - Join() would freeze the game.
            //   - lua.Dispose() while Lua thread is alive → AccessViolationException.
            // Hand off cleanup to ThreadPool. Lua thread exits on its own via
            // ShutdownException (if available) or running==false check.
            var threadToJoin = luaThread;
            var luaToDispose = lua;
            var semToDispose = signalReady;
            luaThread = null;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (threadToJoin != null && threadToJoin.IsAlive)
                    {
                        bool exited = threadToJoin.Join(3000);
                        if (!exited)
                        {
                            Verse.Log.Warning("[RimComputers] Lua thread did not exit in 3s — forcing abort");
                            try { threadToJoin.Abort(); } catch { }
                            threadToJoin.Join(2000);
                        }
                    }
                }
                catch { }
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
                Directory.CreateDirectory(hddFolderPath);
                // Write all files
                foreach (var kv in hddVfs)
                {
                    if (kv.Key.EndsWith("/.dir")) continue; // skip dir markers
                    string fullPath = Path.Combine(hddFolderPath,
                        kv.Key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                    File.WriteAllBytes(fullPath, kv.Value);
                }
                Log($"[hdd] Flushed {hddVfs.Count} entries to {hddFolderPath}");
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
            lock (logLock)
            {
                string entry = "[" + (Find.TickManager?.TicksGame ?? 0) + "] " + msg;
                log.Add(entry);
                // Keep last 10 000 entries — enough for long sessions, not a memory leak.
                if (log.Count > 10000) log.RemoveAt(0);
            }
        }
    }
}