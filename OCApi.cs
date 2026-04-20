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

        // Virtual filesystem: normalised path → bytes
        private readonly Dictionary<string, byte[]> vfs =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        // Signal queue (game thread writes, Lua thread reads)
        private readonly Queue<object[]> signalQueue = new Queue<object[]>();
        private readonly SemaphoreSlim signalReady = new SemaphoreSlim(0, int.MaxValue);
        private readonly object signalLock = new object();
        private readonly object logLock = new object();

        // Component addresses
        private readonly string gpuAddr, scrnAddr, fsAddr, eepromAddr, compAddr;

        // State
        private string eepromData;    // what eeprom.getData() returns
        private string bootAddress;   // which filesystem component to boot from
        private volatile bool running = true;
        public bool IsRunning => running;

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
                     Dictionary<string, byte[]> persistentVfs = null,
                     Comp_Computer compComp = null)
        {
            LoadNativeLua();

            this.lua = new Lua();
            this.screen = screen;
            this.props = props;
            this.log = log;
            this.building = building;
            this.compComp = compComp;

            lua.UseTraceback = true;

            // Generate component addresses
            gpuAddr = NewGuid8();
            scrnAddr = NewGuid8();
            fsAddr = NewGuid8();
            eepromAddr = NewGuid8();
            compAddr = building?.ThingID ?? NewGuid8();

            // Boot address = our single filesystem component
            bootAddress = fsAddr;
            eepromData = fsAddr;   // eeprom stores the boot filesystem address

            // Load ROM into VFS, then snapshot which keys are ROM-originated
            foreach (var e in RomLoader.Load(vfs)) log.Add(e);
            foreach (var k in vfs.Keys) romKeys.Add(k);
            foreach (var v in vfs.Values) romUsed += v.Length;

            // Overlay writable files from previous session ON TOP of ROM.
            // OpenOS config edits (/etc/hostname, user files, etc.) survive reboot.
            if (persistentVfs != null)
            {
                foreach (var kv in persistentVfs)
                    vfs[kv.Key] = kv.Value;
                Log($"[OCApi] Restored {persistentVfs.Count} writable VFS entries from save");
            }

            // Ensure standard directories exist in VFS
            foreach (var d in new[] { "bin", "lib", "etc", "tmp", "home", "mnt", "usr", "usr/lib", "boot", "lib/core" })
                EnsureDir(d);

            if (!vfs.ContainsKey("etc/hostname"))
                WriteText("etc/hostname", "rimcomp-" + compAddr.Substring(0, 8) + "\n");

            Log($"[OCApi] VFS loaded: {vfs.Count} entries, ROM={romUsed / 1024}KB");

            // Register all APIs
            RegisterAll();

            // Compile the chunk-compiler helper ONCE on the game thread
            // (safe here — we are not inside a Lua callback)
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
            lua["computer.address"] = compAddr;
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
            lua["computer.getBootAddress"] = (Func<string>)(() => bootAddress ?? fsAddr);
            lua["computer.setBootAddress"] = (Action<object>)(addr =>
            {
                bootAddress = addr?.ToString() ?? fsAddr;
                eepromData = bootAddress;
                Log($"[computer] setBootAddress={bootAddress}");
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
                // non-blocking: just let pullSignal drain by timeout
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

            lua.DoString(@"
component.type = function(addr)   return __comp_type_raw__(addr) end
component.isAvailable = function(t) return __comp_avail_raw__(t) end

-- proxy захватывает __comp_invoke_raw__ напрямую в замыкание,
-- чтобы не зависеть от глобального _G.component (boot.lua делает _G.component = nil)
component.proxy = function(addr)
    local t = __comp_type_raw__(addr)
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

            // keyboard uses the same address as computer (compAddr)
            var all = new[]
            {
                (gpuAddr,    "gpu"),
                (scrnAddr,   "screen"),
                (fsAddr,     "filesystem"),
                (eepromAddr, "eeprom"),
                (compAddr,   "keyboard"),
            };

            var tbl = NewLuaTable();
            int count = 0;
            foreach (var (addr, type) in all)
            {
                bool match = string.IsNullOrEmpty(filter) ||
                             (exact ? type == filter : type.Contains(filter));
                if (!match) continue;

                tbl[addr] = type;
                count++;
                lock (logLock) { Log($"[ComponentListHelper] added: {addr} = {type}"); }
            }
            lock (logLock) { Log($"[ComponentListHelper] returning table with {count} entries"); }
            return tbl;
        }

        private string ComponentType(string addr)
        {
            if (addr == gpuAddr) return "gpu";
            if (addr == scrnAddr) return "screen";
            if (addr == fsAddr) return "filesystem";
            if (addr == eepromAddr) return "eeprom";
            if (addr == compAddr) return "keyboard";
            return null;
        }

        private bool ComponentAvailable(string type)
            => type == "gpu" || type == "screen" ||
               type == "filesystem" || type == "eeprom" ||
               type == "keyboard";

        // Called from Lua wrapper (Lua thread). args = {[1]=v1,[2]=v2,...}
        private LuaTable ComponentInvoke(string addr, string method, LuaTable args)
        {
            lock (logLock) { Log($"[invoke] {addr}::{method}"); }
            try
            {
                var argList = new List<object>();
                if (args != null)
                {
                    for (int i = 1; ; i++)
                    {
                        var v = args[i];
                        if (v == null) break;
                        argList.Add(v);
                    }
                }
                var a = argList.ToArray();

                object[] result;
                if (addr == gpuAddr) result = InvokeGpu(method, a);
                else if (addr == fsAddr) result = InvokeFs(method, a);
                else if (addr == scrnAddr) result = InvokeScreen(method, a);
                else if (addr == eepromAddr) result = InvokeEeprom(method, a);
                else if (addr == compAddr) result = InvokeKeyboard(method, a);
                else return MakeResultTable(false, null, "unknown component: " + addr);

                if (result == null)
                {
                    var nilTbl = NewLuaTable();
                    nilTbl["ok"] = true;
                    nilTbl["isnil"] = true;
                    return nilTbl;
                }
                return MakeResultTable(true, result, null);
            }
            catch (Exception ex)
            {
                lock (logLock) { Log($"[invoke] ERROR {addr}::{method}: {ex.Message}"); }
                return MakeResultTable(false, null, ex.Message);
            }
        }

        private object[] InvokeKeyboard(string method, object[] args)
        {
            switch (method)
            {
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

        // ════════════════════════════════════════════════════════════════════
        // unicode
        // ════════════════════════════════════════════════════════════════════

        private void RegisterUnicode()
        {
            // All in Lua — avoids NLua double→int marshalling failures.
            // unicode.char is safe-guarded to [0,255] so string.char never throws.
            lua.DoString(@"
unicode = {}
function unicode.len(s)   return type(s)=='string' and #s or 0 end
function unicode.wlen(s)  return type(s)=='string' and #s or 0 end
function unicode.charWidth(s) return 1 end
function unicode.isWide(s)    return false end
function unicode.char(...)
    local r = ''
    for _, v in ipairs({...}) do
        local n = math.floor(v)
        if n >= 0 and n <= 255 then r = r .. string.char(n) end
    end
    return r
end
function unicode.sub(s, i, j)
    if type(s) ~= 'string' then return '' end
    return string.sub(s, math.floor(i or 1), j ~= nil and math.floor(j) or #s)
end
function unicode.lower(s) return type(s)=='string' and string.lower(s) or '' end
function unicode.upper(s) return type(s)=='string' and string.upper(s) or '' end
");
        }

        // ════════════════════════════════════════════════════════════════════
        // GPU
        // ════════════════════════════════════════════════════════════════════

        private void RegisterGpu()
        {
            lua.DoString(@"gpu = {}");
            lua["gpu.address"] = gpuAddr;
            lua["gpu.type"] = "gpu";
        }

        private object[] InvokeGpu(string method, object[] args)
        {
            switch (method)
            {
                case "getResolution":
                case "maxResolution":
                    return new object[] { (double)props.screenWidth, (double)props.screenHeight };
                case "setResolution":
                    return new object[] { true };
                case "getScreen":
                    return new object[] { scrnAddr };
                case "bind":
                    Log($"[gpu] bind {(args.Length > 0 ? args[0] : "?")}");
                    return new object[] { true };
                case "set":
                    if (args.Length >= 3)
                    {
                        bool vert = args.Length >= 4 && args[3] is bool b && b;
                        screen.Set(ToInt(args[0]) - 1, ToInt(args[1]) - 1,
                                   args[2]?.ToString() ?? "", vert);
                    }
                    return new object[] { true };
                case "get":
                    if (args.Length >= 2)
                    {
                        var (c, fg, bg) = screen.Get(ToInt(args[0]) - 1, ToInt(args[1]) - 1);
                        long fgn = (fg.r << 16) | (fg.g << 8) | fg.b;
                        long bgn = (bg.r << 16) | (bg.g << 8) | bg.b;
                        return new object[] { c.ToString(), (double)fgn, (double)bgn, false, false };
                    }
                    return new object[] { " " };
                case "fill":
                    if (args.Length >= 5)
                        screen.Fill(ToInt(args[0]) - 1, ToInt(args[1]) - 1,
                                    ToInt(args[2]), ToInt(args[3]),
                                    args[4]?.ToString()?.Length > 0 ? args[4].ToString()[0] : ' ');
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
                        long c = Convert.ToInt64(args[0]);
                        screen.CurrentBG = new Color32(
                            (byte)((c >> 16) & 0xFF),
                            (byte)((c >> 8) & 0xFF),
                            (byte)(c & 0xFF), 255);
                    }
                    return new object[] { 0.0, false };
                case "setForeground":
                    if (args.Length >= 1)
                    {
                        long c = Convert.ToInt64(args[0]);
                        screen.CurrentFG = new Color32(
                            (byte)((c >> 16) & 0xFF),
                            (byte)((c >> 8) & 0xFF),
                            (byte)(c & 0xFF), 255);
                    }
                    return new object[] { (double)0xFFFFFF, false };
                case "getBackground":
                    return new object[] { 0.0, false };
                case "getForeground":
                    return new object[] { (double)0xFFFFFF, false };
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
                case "setDepth": return new object[] { args.Length > 0 ? args[0] : (object)8.0 };
                case "getViewport": return new object[] { (double)props.screenWidth, (double)props.screenHeight };
                case "setViewport": return new object[] { true };
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
            lua["filesystem.address"] = fsAddr;
            lua["filesystem.type"] = "filesystem";
        }

        private bool FsExists(string path) => vfs.ContainsKey(Norm(path)) || IsDir(path);
        private bool FsIsDirectory(string path) => IsDir(Norm(path));
        private long FsSize(string path) => vfs.TryGetValue(Norm(path), out var b) ? b.LongLength : 0L;

        private bool IsDir(string path)
        {
            string prefix = Norm(path);
            if (prefix.Length > 0 && !prefix.EndsWith("/")) prefix += "/";
            return vfs.Keys.Any(k =>
                k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        // Returns a table {[1]="name",[2]="name/",...} matching OC filesystem.list
        private LuaTable FsList(string path)
        {
            var result = NewLuaTable();
            string prefix = Norm(path);
            if (prefix.Length > 0 && !prefix.EndsWith("/")) prefix += "/";

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int idx = 1;
            foreach (var k in vfs.Keys)
            {
                if (prefix.Length > 0 &&
                    !k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                string rel = k.Substring(prefix.Length);
                if (rel == "" || rel == ".dir") continue;

                // skip internal .dir markers
                if (rel.EndsWith("/.dir"))
                {
                    string dirName = rel.Substring(0, rel.Length - 4) + "/";
                    int slash2 = dirName.IndexOf('/');
                    string entry2 = slash2 < 0 ? dirName : dirName.Substring(0, slash2 + 1);
                    if (seen.Add(entry2)) result[idx++] = entry2;
                    continue;
                }

                int slash = rel.IndexOf('/');
                string entry = slash < 0 ? rel : rel.Substring(0, slash) + "/";
                if (entry == ".dir/") continue;
                if (seen.Add(entry)) result[idx++] = entry;
            }
            return result;
        }

        private object[] FsOpen(string path, string mode)
        {
            mode = mode ?? "r";
            string npath = Norm(path);
            bool reading = mode.Contains("r");
            bool appending = mode.Contains("a");
            bool writing = mode.Contains("w") || appending;

            if (reading && !vfs.ContainsKey(npath))
                return new object[] { null, "file not found: " + path };

            if (writing && !vfs.ContainsKey(npath))
                vfs[npath] = Array.Empty<byte>();

            int id = nextHandle++;
            openHandles[id] = new FileHandle
            {
                Path = npath,
                Writing = writing,
                Appending = appending,
                Pos = appending && vfs.TryGetValue(npath, out var ab) ? ab.Length : 0
            };
            Log($"[fs] open '{path}' mode={mode} → handle {id}");
            return new object[] { (double)id };
        }

        private void FsClose(int handle) => openHandles.Remove(handle);

        private object[] FsRead(int handle, double count)
        {
            if (!openHandles.TryGetValue(handle, out var h))
                return new object[] { null, "bad file handle" };
            if (!vfs.TryGetValue(h.Path, out var data)) return null; // EOF
            if (h.Pos >= data.Length) return null; // EOF

            // math.maxinteger or math.huge означает "читать всё"
            int n;
            if (double.IsInfinity(count) || double.IsNaN(count) || count >= int.MaxValue)
                n = data.Length - h.Pos;
            else
                n = Math.Min(Math.Max(0, (int)count), data.Length - h.Pos);

            if (n == 0) return null; // EOF

            string chunk = Encoding.UTF8.GetString(data, h.Pos, n);
            h.Pos += n;
            return new object[] { chunk };
        }

        private bool FsWrite(int handle, string content)
        {
            if (!openHandles.TryGetValue(handle, out var h)) return false;
            byte[] bytes = Encoding.UTF8.GetBytes(content ?? "");
            if (!vfs.TryGetValue(h.Path, out var cur)) cur = Array.Empty<byte>();

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
            vfs[h.Path] = newData;
            return true;
        }

        private double FsSeek(int handle, string whence, int offset)
        {
            if (!openHandles.TryGetValue(handle, out var h)) return 0;
            var data = vfs.TryGetValue(h.Path, out var d) ? d : Array.Empty<byte>();
            if (whence == "set") h.Pos = offset;
            else if (whence == "cur") h.Pos += offset;
            else if (whence == "end") h.Pos = data.Length + offset;
            h.Pos = Math.Max(0, Math.Min(h.Pos, data.Length));
            return h.Pos;
        }

        private object[] InvokeFs(string method, object[] args)
        {
            lock (logLock) { Log($"[fs] {method}"); }
            string Str(int i) => i < args.Length ? args[i]?.ToString() ?? "" : "";
            int Int(int i) => i < args.Length ? ToInt(args[i]) : 0;
            double Dbl(int i) => i < args.Length && args[i] != null
                                 ? Convert.ToDouble(args[i]) : 0;
            switch (method)
            {
                case "isReadOnly": return new object[] { false };
                case "spaceTotal": return new object[] { props.romBytes };
                case "spaceUsed": return new object[] { vfs.Values.Sum(v => v.LongLength) };
                case "exists": return new object[] { FsExists(Str(0)) };
                case "isDirectory": return new object[] { FsIsDirectory(Str(0)) };
                case "size": return new object[] { FsSize(Str(0)) };
                case "list": return new object[] { FsList(Str(0)) };
                case "open": return FsOpen(Str(0), args.Length > 1 ? Str(1) : "r");
                case "close": FsClose(Int(0)); return new object[] { true };
                case "read": return FsRead(Int(0), Dbl(1));
                case "write": return new object[] { FsWrite(Int(0), Str(1)) };
                case "seek": return new object[] { FsSeek(Int(0), Str(1), Int(2)) };
                case "makeDirectory": EnsureDir(Str(0)); return new object[] { true };
                case "remove": vfs.Remove(Norm(Str(0))); return new object[] { true };
                case "rename":
                    {
                        string nf = Norm(Str(0)), nt = Norm(Str(1));
                        if (!vfs.TryGetValue(nf, out var rd))
                            return new object[] { null, "not found" };
                        vfs[nt] = rd; vfs.Remove(nf);
                        return new object[] { true };
                    }
                case "lastModified": return new object[] { 0L };
                case "getLabel": return new object[] { "rimcomp" };
                case "setLabel": return new object[] { true };
                default:
                    return new object[] { null, "unknown fs method: " + method };
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Screen
        // ════════════════════════════════════════════════════════════════════

        private void RegisterScreen()
        {
            lua.DoString(@"screen = {}");
            lua["screen.address"] = scrnAddr;
            lua["screen.type"] = "screen";
        }

        private object[] InvokeScreen(string method, object[] args)
        {
            switch (method)
            {
                case "getAspectRatio": return new object[] { 1.0, 1.0 };
                case "getKeyboards":
                    var k = NewLuaTable(); k[1] = compAddr;
                    return new object[] { k };
                case "isColor": return new object[] { true };
                case "isPrecise": return new object[] { false };
                case "setPrecise": return new object[] { false };
                default: return new object[] { null };
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // EEPROM
        // ════════════════════════════════════════════════════════════════════

        private void RegisterEeprom()
        {
            lua.DoString(@"eeprom = {}");
            lua["eeprom.address"] = eepromAddr;
            lua["eeprom.type"] = "eeprom";
        }

        private object[] InvokeEeprom(string method, object[] args)
        {
            string Str(int i) => i < args.Length ? args[i]?.ToString() ?? "" : "";
            switch (method)
            {
                case "get": return new object[] { eepromData ?? "" };
                case "set": eepromData = Str(0); return new object[] { true };
                case "getData": return new object[] { eepromData ?? fsAddr };
                case "setData":
                    eepromData = args.Length > 0 ? Str(0) : null;
                    bootAddress = eepromData ?? fsAddr;
                    return new object[] { true };
                case "getLabel": return new object[] { "BIOS" };
                case "getSize": return new object[] { 4096 };
                case "getChecksum": return new object[] { "opencomputers" };
                default: return new object[] { null };
            }
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
                return vfs.TryGetValue(n, out var data)
                    ? Encoding.UTF8.GetString(data)
                    : null;
            });

            // Expose a C# function that checks if VFS path exists.
            lua["__vfs_exists__"] = (Func<string, bool>)(path => vfs.ContainsKey(Norm(path)));

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

            // Handle paths starting with /
            if (!vfs.ContainsKey(path))
            {
                // Try stripping leading slash
                string stripped = path.TrimStart('/');
                if (vfs.ContainsKey(stripped)) path = stripped;
            }

            if (!vfs.TryGetValue(path, out var data))
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

            if (!vfs.ContainsKey("bios.lua"))
            {
                Log("[boot] ERROR: bios.lua not found in VFS!");
                screen.Println("ERROR: No BIOS found!");
                return;
            }

            string biosCode = Encoding.UTF8.GetString(vfs["bios.lua"]);
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
                    screen.Fill(0, 0, props.screenWidth, props.screenHeight, ' ');
                    string msg = ex.Message.Length > props.screenWidth - 8
                        ? ex.Message.Substring(0, props.screenWidth - 8)
                        : ex.Message;
                    screen.Set(0, props.screenHeight / 2, "FATAL: " + msg);
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

        public void Tick()
        {
            if (!running) return;

            long gameTicks = Find.TickManager?.TicksGame ?? 0;
            uptimeSeconds = gameTicks * (1f / 60f);
            ramUsed = (openHandles.Count * 256L) + (signalQueue.Count * 128L) + 8192L;

            while (InputBuffer.TryDequeueKeyDown(out char kc, out int sc))
            {
                double charCode = (kc >= 32 && kc <= 127) ? (double)kc : 0.0;
                PushSignal("key_down", compAddr, charCode, (double)sc, compAddr);
            }

            while (InputBuffer.TryDequeueKeyUp(out char ku, out int su))
            {
                double charCode = (ku >= 32 && ku <= 127) ? (double)ku : 0.0;
                PushSignal("key_up", compAddr, charCode, (double)su, compAddr);
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
            PushSignal("key_down", compAddr, charCode, (double)keyCode, compAddr);
            PushSignal("key_up", compAddr, charCode, (double)keyCode, compAddr);
        }

        public void PushClipboardSignal(string text)
        {
            if (!string.IsNullOrEmpty(text))
                PushSignal("clipboard", scrnAddr, text, compAddr);
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
        /// Comp_Computer serialises this dictionary to the save game.
        /// </summary>
        public Dictionary<string, byte[]> GetWriteableVfsSnapshot()
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in vfs)
                if (!romKeys.Contains(kv.Key))
                    result[kv.Key] = kv.Value;
            return result;
        }

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

        private void EnsureDir(string p)
        {
            string m = Norm(p) + "/.dir";
            if (!vfs.ContainsKey(m)) vfs[m] = Array.Empty<byte>();
        }

        private void WriteText(string path, string text)
            => vfs[Norm(path)] = Encoding.UTF8.GetBytes(text);

        private string Norm(string p)
            => (p ?? "").Replace('\\', '/').TrimStart('/');

        private static string NewGuid8()
            => Guid.NewGuid().ToString("N").Substring(0, 8);

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
                if (log.Count > 2000) log.RemoveAt(0);
            }
        }
    }
}