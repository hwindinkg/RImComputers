using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Verse;

namespace RimComputers
{
    /// <summary>
    /// Loads ROM contents into the virtual filesystem at boot time.
    ///
    /// Priority order (highest to lowest):
    ///   1. RimComputers/ROM/            — folder shipped with the mod
    ///   2. RimComputers/ROM/openos.zip  — optional pre-bundled OpenOS zip
    ///   3. Built-in BIOS stub           — always available as fallback
    ///
    /// The ROM folder mirrors the OC filesystem layout:
    ///   ROM/boot/           → /boot/
    ///   ROM/bin/            → /bin/
    ///   ROM/lib/            → /lib/
    ///   ROM/OpenOS.lua      → /OpenOS.lua
    ///   etc.
    /// </summary>
    public static class RomLoader
    {
        private static string ModRomPath => Path.Combine(
            ModRootPath(), "ROM");

        private static string ModZipPath => Path.Combine(
            ModRootPath(), "ROM", "openos.zip");

        // ════════════════════════════════════════════════════════════════════
        // Public entry point
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Populate <paramref name="vfs"/> with ROM contents.
        /// Returns a log of what was loaded.
        /// </summary>
        public static List<string> Load(Dictionary<string, byte[]> vfs)
        {
            var log = new List<string>();

            // 1. Try ZIP first (user dropped openos.zip into ROM/)
            if (File.Exists(ModZipPath))
            {
                log.Add($"[ROM] Loading from ZIP: {ModZipPath}");
                LoadZip(ModZipPath, vfs, log);
            }
            // 2. Try loose folder
            else if (Directory.Exists(ModRomPath))
            {
                log.Add($"[ROM] Loading from folder: {ModRomPath}");
                LoadFolder(ModRomPath, "", vfs, log);
            }
            else
            {
                log.Add("[ROM] No ROM found — using built-in BIOS stub.");
            }

            // 3. Always inject the built-in BIOS (it's the first thing that runs)
            InjectBuiltinBios(vfs, log);

            log.Add($"[ROM] Total files: {vfs.Count}");
            return log;
        }

        // ════════════════════════════════════════════════════════════════════
        // Zip loader
        // ════════════════════════════════════════════════════════════════════

        private static void LoadZip(string zipPath,
            Dictionary<string, byte[]> vfs, List<string> log)
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith("/")) continue; // directory entry
                    string vpath = NormVPath(entry.FullName);
                    using var ms = new MemoryStream();
                    using var s = entry.Open();
                    s.CopyTo(ms);
                    vfs[vpath] = ms.ToArray();
                    log.Add($"[ROM] zip  /{vpath} ({ms.Length} B)");
                }
            }
            catch (Exception ex)
            {
                log.Add($"[ROM] ZIP error: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Folder loader (recursive)
        // ════════════════════════════════════════════════════════════════════

        private static void LoadFolder(string diskPath, string vfsBase,
            Dictionary<string, byte[]> vfs, List<string> log)
        {
            // Files in this directory
            foreach (var file in Directory.GetFiles(diskPath))
            {
                string name = Path.GetFileName(file);
                string vpath = string.IsNullOrEmpty(vfsBase) ? name : vfsBase + "/" + name;
                try
                {
                    vfs[vpath] = File.ReadAllBytes(file);
                    log.Add($"[ROM] file /{vpath} ({vfs[vpath].Length} B)");
                }
                catch (Exception ex)
                {
                    log.Add($"[ROM] skip /{vpath}: {ex.Message}");
                }
            }

            // Recurse into sub-directories
            foreach (var dir in Directory.GetDirectories(diskPath))
            {
                string dname = Path.GetFileName(dir);
                string vchild = string.IsNullOrEmpty(vfsBase) ? dname : vfsBase + "/" + dname;
                LoadFolder(dir, vchild, vfs, log);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Built-in BIOS  (minimal OC-compatible boot sequence)
        // ════════════════════════════════════════════════════════════════════

        private static void InjectBuiltinBios(Dictionary<string, byte[]> vfs, List<string> log)
        {
            // Only inject if not already supplied by ROM folder/zip
            void TryInject(string path, string content)
            {
                if (!vfs.ContainsKey(path))
                {
                    vfs[path] = System.Text.Encoding.UTF8.GetBytes(content);
                    log.Add($"[ROM] bios /{path} (built-in)");
                }
            }

            // If OpenOS is present (has bios.lua), skip our custom boot scripts —
            // they would conflict with OpenOS boot sequence which runs boot/*.lua in order
            bool hasOpenOS = vfs.ContainsKey("bios.lua") || vfs.ContainsKey("init.lua");
            if (hasOpenOS)
            {
                log.Add("[ROM] OpenOS detected — skipping built-in boot/01_base.lua and boot/02_shell.lua");
                return;
            }

            // ── /boot/01_base.lua  ──────────────────────────────────────────
            // Equivalent to OC's machine.lua bootstrap
            TryInject("boot/01_base.lua", @"
-- RimComputers BIOS bootstrap (OC-compatible)
-- Mirrors the role of OpenComputers' machine.lua

local gpu = component.getPrimary('gpu')
local w, h = table.unpack(gpu.getResolution())

local function cls()   gpu.fill(1, 1, w, h, ' ') end
local function at(x,y,s,fg,bg)
  if fg then gpu.setForeground(fg) end
  if bg then gpu.setBackground(bg) end
  gpu.set(x, y, s)
end

-- BIOS screen
gpu.setBackground(0x000000)
gpu.setForeground(0x00FF00)
cls()
at(1, 1, 'RimComputers BIOS v1.0', 0x00FF00)
at(1, 2, string.rep('-', w),        0x007700)
at(1, 3, 'CPU:  MoonSharp Lua ' .. _VERSION)
at(1, 4, string.format('ROM:  %d KB', math.floor(filesystem.spaceTotal() / 1024)))
at(1, 5, string.format('RAM:  %d KB', math.floor(computer.totalMemory()  / 1024)))
at(1, 6, 'GPU:  ' .. w .. 'x' .. h .. ' text mode')
at(1, 7, string.rep('-', w),        0x007700)
gpu.setForeground(0xFFFFFF)

-- Scan for bootable /boot/*.lua files
local bootFiles = {}
for name in pairs(filesystem.list('boot') or {}) do
  if name:match('%.lua$') and name ~= '01_base.lua' then
    bootFiles[#bootFiles+1] = 'boot/' .. name
  end
end
table.sort(bootFiles)

if #bootFiles == 0 then
  at(1, 9, 'No bootable OS found.', 0xFF4444)
  at(1,10, 'Drop .lua files into boot/ or', 0xAAAAAA)
  at(1,11, 'place openos.zip in ROM/',        0xAAAAAA)
  at(1,13, '> Dropping into REPL mode', 0xFFFF00)
  -- REPL will be provided by the debug window
else
  at(1, 9, 'Found ' .. #bootFiles .. ' boot stage(s):', 0x88FF88)
  for i, f in ipairs(bootFiles) do
    at(1, 9+i, '  ' .. f, 0xCCCCCC)
  end
  at(1, 9 + #bootFiles + 2, 'Booting...', 0xFFFFFF)

  -- Execute each stage in order
  for _, path in ipairs(bootFiles) do
    local data = filesystem.open(path, 'r')
    if data then
      local code = data:read('*a')
      data:close()
      local fn, err = load(code, '@' .. path)
      if fn then
        local ok, runerr = pcall(fn)
        if not ok then
          at(1, h-2, 'Boot error in ' .. path .. ':', 0xFF4444)
          at(1, h-1, tostring(runerr), 0xFF8888)
          break
        end
      else
        at(1, h-1, 'Parse error: ' .. tostring(err), 0xFF4444)
        break
      end
    end
  end
end
");

            // ── /boot/02_shell.lua  ─────────────────────────────────────────
            // Simple interactive shell — used when no OpenOS is present
            TryInject("boot/02_shell.lua", @"
-- RimComputers minimal shell
-- Runs when no full OpenOS is installed

local gpu = component.getPrimary('gpu')
local w, h = table.unpack(gpu.getResolution())

local function cls() gpu.fill(1,1,w,h,' ') end
local function print(s, col)
  -- scroll up one line, write at bottom
  gpu.copy(1, 2, w, h-1, 0, -1)
  gpu.fill(1, h, w, 1, ' ')
  gpu.setForeground(col or 0xFFFFFF)
  gpu.set(1, h, tostring(s))
end

cls()
gpu.setForeground(0x00FFFF)
gpu.set(1,1,'RimComputers Shell v1.0  (type Lua expressions)')
gpu.set(1,2,string.rep('=', w))
gpu.setForeground(0xFFFFFF)

-- The actual interactive loop is driven by key signals from the UI.
-- We register a global _SHELL_INPUT that Dialog_ComputerScreen calls.
local inputLine = ''
local lineY = 3

local function redrawInput()
  gpu.fill(1, lineY, w, 1, ' ')
  gpu.setForeground(0xFFFF00)
  gpu.set(1, lineY, '> ' .. inputLine)
  gpu.setForeground(0xFFFFFF)
end

_G._SHELL_INPUT = function(text)
  if text == nil then return end
  inputLine = inputLine .. text
  redrawInput()
end

_G._SHELL_ENTER = function()
  local cmd = inputLine
  inputLine = ''
  -- Echo command
  gpu.copy(1, 2, w, h-2, 0, -1)
  gpu.fill(1, h-1, w, 2, ' ')
  gpu.setForeground(0xFFFF00)
  gpu.set(1, h-2, '> ' .. cmd)
  -- Execute
  local fn, err = load('return ' .. cmd)
  if not fn then fn, err = load(cmd) end
  if fn then
    local ok, result = pcall(fn)
    gpu.setForeground(ok and 0x88FF88 or 0xFF6666)
    gpu.set(1, h-1, ok and tostring(result) or 'Error: ' .. tostring(result))
  else
    gpu.setForeground(0xFF6666)
    gpu.set(1, h-1, 'Syntax: ' .. tostring(err))
  end
  gpu.setForeground(0xFFFFFF)
  lineY = h
  redrawInput()
end

redrawInput()
");

            // ── /lib/term.lua  ──────────────────────────────────────────────
            // OC-compatible term API stub
            TryInject("lib/term.lua", @"
-- OC-compatible term library
local term = {}
local gpu = component.getPrimary('gpu')
local w, h = table.unpack(gpu.getResolution())
local cx, cy = 1, 1
local blink = true

function term.getCursor()   return cx, cy end
function term.setCursor(x,y) cx, cy = x, y end
function term.getViewport() return w, h end

function term.write(s, wrap)
  s = tostring(s)
  for i = 1, #s do
    local c = s:sub(i,i)
    if c == '\n' then
      cx = 1; cy = cy + 1
    elseif c == '\r' then
      cx = 1
    else
      gpu.set(cx, cy, c)
      cx = cx + 1
      if cx > w then cx = 1; cy = cy + 1 end
    end
    if cy > h then
      gpu.copy(1, 2, w, h-1, 0, -1)
      gpu.fill(1, h, w, 1, ' ')
      cy = h
    end
  end
end

function term.read(history)
  -- Blocking read - not truly implementable without coroutines
  -- Returns empty string as stub; full impl needs signal loop
  return ''
end

function term.clear()
  gpu.fill(1,1,w,h,' ')
  cx, cy = 1, 1
end

function term.clearLine()
  gpu.fill(1, cy, w, 1, ' ')
  cx = 1
end

function term.isAvailable() return true end

return term
");

            // ── /lib/event.lua  ─────────────────────────────────────────────
            TryInject("lib/event.lua", @"
-- OC-compatible event library (simplified)
local event = {}
local listeners = {}

function event.listen(name, callback)
  listeners[name] = listeners[name] or {}
  table.insert(listeners[name], callback)
  return true
end

function event.ignore(name, callback)
  if listeners[name] then
    for i, cb in ipairs(listeners[name]) do
      if cb == callback then
        table.remove(listeners[name], i)
        return true
      end
    end
  end
  return false
end

function event.pull(timeout, filter)
  local sig = {computer.pullSignal(timeout or math.huge)}
  if sig[1] == nil then return nil end
  if filter and sig[1] ~= filter then return table.unpack(sig) end
  -- Fire listeners
  if listeners[sig[1]] then
    for _, cb in ipairs(listeners[sig[1]]) do
      pcall(cb, table.unpack(sig))
    end
  end
  return table.unpack(sig)
end

function event.push(name, ...)
  computer.pushSignal(name, ...)
end

function event.timer(interval, callback, times)
  -- Stub: immediate call once
  callback()
  return 0
end

return event
");

            // ── /lib/serialization.lua  ─────────────────────────────────────
            TryInject("lib/serialization.lua", @"
-- OC serialization library
local serialization = {}

local function serialize(val, indent, seen)
  seen = seen or {}
  local t = type(val)
  if t == 'nil'     then return 'nil'
  elseif t == 'boolean' then return tostring(val)
  elseif t == 'number'  then return tostring(val)
  elseif t == 'string'  then return string.format('%q', val)
  elseif t == 'table' then
    if seen[val] then return '{}' end
    seen[val] = true
    local parts = {}
    for k, v in pairs(val) do
      local key = type(k)=='string'
        and ('['..string.format('%q',k)..']')
        or  ('['..tostring(k)..']')
      parts[#parts+1] = key..'='..serialize(v, indent, seen)
    end
    return '{'..table.concat(parts,',')..'}'
  else
    return tostring(val)
  end
end

function serialization.serialize(val)   return serialize(val) end
function serialization.unserialize(str)
  local fn, err = load('return '..str)
  if fn then
    local ok, v = pcall(fn)
    return ok and v or nil
  end
  return nil
end

return serialization
");

            // ── /etc/rc.cfg  ────────────────────────────────────────────────
            if (!vfs.ContainsKey("etc/rc.cfg"))
                vfs["etc/rc.cfg"] = System.Text.Encoding.UTF8.GetBytes(
                    "-- RimComputers boot config\n");

            // ── /etc/hostname  ──────────────────────────────────────────────
            if (!vfs.ContainsKey("etc/hostname"))
                vfs["etc/hostname"] = System.Text.Encoding.UTF8.GetBytes("rimcomp\n");
        }

        // ════════════════════════════════════════════════════════════════════
        // Helpers
        // ════════════════════════════════════════════════════════════════════

        private static string NormVPath(string p) =>
            p.Replace('\\', '/').TrimStart('/');

        private static string ModRootPath()
        {
            // Walk loaded mods to find ours
            foreach (var mod in LoadedModManager.RunningMods)
                if (mod.PackageId.ToLowerInvariant().Contains("rimcomputers"))
                    return mod.RootDir;

            // Fallback: relative to executable
            return Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Mods", "RimComputers");
        }
    }
}