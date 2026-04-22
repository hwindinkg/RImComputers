using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Verse;

namespace RimComputers
{
    /// <summary>
    /// Loads ROM contents into the virtual filesystem at boot.
    ///
    /// New folder structure (preferred):
    ///   ROM/
    ///     BIOS/
    ///       bios.lua          ← default BIOS Lua code
    ///       bios_1.lua        ← custom BIOS alternative
    ///     OpenOS/             ← OpenOS filesystem root
    ///       .prop
    ///       init.lua
    ///       boot/, bin/, lib/, etc., home/, usr/
    ///     AnotherOS/          ← future OS disk images
    ///
    /// Legacy flat structure (backward compatible):
    ///   ROM/
    ///     bios.lua
    ///     init.lua, boot/, bin/, …
    ///
    /// BIOS loading priority:
    ///   1. ROM/BIOS/bios.lua   (new structure)
    ///   2. ROM/bios.lua        (legacy)
    ///   3. Built-in stub
    /// </summary>
    public static class RomLoader
    {
        // ════════════════════════════════════════════════════════════════════
        // Public entry point
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Load ROM files into <paramref name="romVfs"/> (read-only OS disk).
        /// Returns the BIOS Lua source code and log messages.
        /// </summary>
        public static List<string> Load(
            Dictionary<string, byte[]> romVfs,
            out string biosCode)
        {
            var log = new List<string>();
            biosCode = null;

            string romPath = ModRomPath();

            if (!Directory.Exists(romPath))
            {
                log.Add("[ROM] No ROM folder found — using built-in stub.");
                biosCode = BuiltinBiosStub;
                return log;
            }

            log.Add($"[ROM] Loading from folder: {romPath}");

            // ── Detect structure ─────────────────────────────────────────────
            string biosDir    = Path.Combine(romPath, "BIOS");
            string openOsDir  = Path.Combine(romPath, "OpenOS");
            bool newStructure = Directory.Exists(biosDir) || Directory.Exists(openOsDir);

            if (newStructure)
            {
                // ── New structure ──────────────────────────────────────────
                // BIOS: load from ROM/BIOS/
                if (Directory.Exists(biosDir))
                {
                    string biosFile = Path.Combine(biosDir, "bios.lua");
                    if (File.Exists(biosFile))
                    {
                        biosCode = File.ReadAllText(biosFile, Encoding.UTF8);
                        log.Add($"[ROM] BIOS: {biosFile} ({biosCode.Length} B)");
                    }
                    // Also load any other BIOS files as available alternatives
                    // (flash.lua can reference them)
                    foreach (var f in Directory.GetFiles(biosDir, "*.lua"))
                    {
                        string name = Path.GetFileName(f);
                        string key  = "BIOS/" + name;
                        romVfs[key] = File.ReadAllBytes(f);
                        log.Add($"[ROM] bios /{key} ({romVfs[key].Length} B)");
                    }
                }

                // OS disk: load from ROM/OpenOS/ (or any OS subfolder)
                if (Directory.Exists(openOsDir))
                {
                    log.Add($"[ROM] OS: loading OpenOS from {openOsDir}");
                    LoadFolderInto(openOsDir, "", romVfs, log);
                }
                else
                {
                    // No OS subfolder — load flat (non-BIOS files directly in ROM/)
                    LoadFolderInto(romPath, "", romVfs, log,
                        skipSubdir: "BIOS");
                }
            }
            else
            {
                // ── Legacy flat structure ──────────────────────────────────
                LoadFolderInto(romPath, "", romVfs, log);

                // Extract BIOS from VFS if present
                if (romVfs.TryGetValue("bios.lua", out var biosBytes))
                    biosCode = Encoding.UTF8.GetString(biosBytes);
            }

            // Fallback BIOS if nothing found
            if (biosCode == null)
            {
                biosCode = BuiltinBiosStub;
                log.Add("[ROM] No BIOS found — using built-in stub.");
            }

            bool hasOpenOS = romVfs.ContainsKey("init.lua") || romVfs.ContainsKey("boot/01_process.lua");
            if (hasOpenOS)
                log.Add("[ROM] OpenOS detected — skipping built-in boot stubs.");
            else
                InjectBuiltinStubs(romVfs, log);

            log.Add($"[ROM] Total files: {romVfs.Count}");
            return log;
        }

        // ════════════════════════════════════════════════════════════════════
        // Folder loader
        // ════════════════════════════════════════════════════════════════════

        private static void LoadFolderInto(
            string diskPath, string vfsBase,
            Dictionary<string, byte[]> vfs, List<string> log,
            string skipSubdir = null)
        {
            foreach (var file in Directory.GetFiles(diskPath))
            {
                string name  = Path.GetFileName(file);
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

            foreach (var dir in Directory.GetDirectories(diskPath))
            {
                string dname = Path.GetFileName(dir);
                if (skipSubdir != null &&
                    string.Equals(dname, skipSubdir, StringComparison.OrdinalIgnoreCase))
                    continue;

                string vchild = string.IsNullOrEmpty(vfsBase) ? dname : vfsBase + "/" + dname;
                LoadFolderInto(dir, vchild, vfs, log, skipSubdir: null);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Built-in stubs (used when no OpenOS is present)
        // ════════════════════════════════════════════════════════════════════

        private static void InjectBuiltinStubs(
            Dictionary<string, byte[]> vfs, List<string> log)
        {
            void TryInject(string path, string content)
            {
                if (!vfs.ContainsKey(path))
                {
                    vfs[path] = Encoding.UTF8.GetBytes(content);
                    log.Add($"[ROM] stub /{path}");
                }
            }

            TryInject("boot/01_base.lua", BuiltinBootStub);
            TryInject("etc/rc.cfg",       "-- RimComputers boot config\n");
        }

        // ════════════════════════════════════════════════════════════════════
        // Helpers
        // ════════════════════════════════════════════════════════════════════

        private static string ModRomPath()
        {
            foreach (var mod in LoadedModManager.RunningMods)
                if (mod.PackageId.ToLowerInvariant().Contains("rimcomputers"))
                    return Path.Combine(mod.RootDir, "ROM");

            return Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Mods", "RimComputers", "ROM");
        }

        // ════════════════════════════════════════════════════════════════════
        // Built-in BIOS stub
        // ════════════════════════════════════════════════════════════════════

        internal static readonly string BuiltinBiosStub = @"
-- RimComputers built-in BIOS stub
-- Runs when no ROM/BIOS/bios.lua is found.
-- Scans for a bootable init.lua or boot/*.lua on any filesystem.

local function findBootDisk()
  for addr in component.list('filesystem') do
    local fs = component.proxy(addr)
    if fs and (fs.exists('/init.lua') or fs.exists('init.lua')) then
      return addr, fs
    end
  end
  return nil, nil
end

local bootAddr, bootFs = findBootDisk()
if bootAddr then
  computer.setBootAddress(bootAddr)
  local data = bootFs.open('/init.lua', 'r') or bootFs.open('init.lua', 'r')
  if data then
    local code = ''
    repeat
      local chunk = bootFs.read(data, math.maxinteger)
      if chunk then code = code .. chunk end
    until not chunk
    bootFs.close(data)
    local fn, err = load(code, '=init')
    if fn then fn() else error('BIOS: init error: ' .. tostring(err)) end
  end
else
  error('BIOS: No bootable disk found')
end
";

        private static readonly string BuiltinBootStub = @"
-- Minimal fallback shell (no OpenOS)
local gpu = component.getPrimary and component.getPrimary('gpu')
if gpu then
  local w, h = table.unpack(gpu.getResolution())
  gpu.setBackground(0x000000)
  gpu.setForeground(0x00FF00)
  gpu.fill(1, 1, w, h, ' ')
  gpu.set(1, 1, 'RimComputers — No OS installed')
  gpu.set(1, 2, 'Place OpenOS in ROM/OpenOS/ or drop openos.zip in ROM/')
end
";
    }
}
