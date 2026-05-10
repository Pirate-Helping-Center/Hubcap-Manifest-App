using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HubcapManifestApp.Services
{
    public class LuaFileManager
    {
        private readonly string _stpluginPath;
        private readonly LoggerService? _logger;

        public LuaFileManager(string stpluginPath, LoggerService? logger = null)
        {
            _stpluginPath = stpluginPath;
            _logger = logger;
        }

        public (List<string> luaFiles, List<string> disabledFiles) FindLuaFiles()
        {
            _logger?.Debug($"[LuaFileManager] FindLuaFiles() called, stplug-in path: {_stpluginPath}");
            var luaFiles = new List<string>();
            var disabledFiles = new List<string>();

            try
            {
                if (!Directory.Exists(_stpluginPath))
                {
                    _logger?.Warning($"[LuaFileManager] stplug-in directory does not exist: {_stpluginPath}");
                    return (luaFiles, disabledFiles);
                }

                var allFiles = Directory.GetFiles(_stpluginPath);
                _logger?.Debug($"[LuaFileManager] Total files in directory: {allFiles.Length}");

                foreach (var file in allFiles)
                {
                    var fileName = Path.GetFileName(file);
                    var extension = Path.GetExtension(file);

                    if (extension == ".lua" && fileName.Count(c => c == '.') == 1)
                    {
                        if (!fileName.Equals("steamtools.lua", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger?.Debug($"[LuaFileManager] Found lua file: {fileName}");
                            luaFiles.Add(file);
                        }
                        else
                        {
                            _logger?.Debug($"[LuaFileManager] Skipped steamtools.lua");
                        }
                    }
                    else if (fileName.EndsWith(".lua.disabled", StringComparison.OrdinalIgnoreCase) && fileName.Count(c => c == '.') == 2)
                    {
                        if (!fileName.Equals("steamtools.lua.disabled", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger?.Debug($"[LuaFileManager] Found disabled file: {fileName}");
                            disabledFiles.Add(file);
                        }
                    }
                    else
                    {
                        _logger?.Debug($"[LuaFileManager] Skipped non-lua file: {fileName}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error($"[LuaFileManager] Error reading stplug-in directory: {ex.Message}\n{ex.StackTrace}");
                throw new Exception($"Error reading stplug-in directory: {ex.Message}", ex);
            }

            _logger?.Info($"[LuaFileManager] FindLuaFiles result: {luaFiles.Count} lua files, {disabledFiles.Count} disabled files");
            return (luaFiles, disabledFiles);
        }

        public string ExtractAppId(string filePath)
        {
            var filename = Path.GetFileName(filePath);
            return filename.Replace(".lua", "").Replace(".disabled", "");
        }

        public string PatchLuaFile(string filePath)
        {
            try
            {
                var content = File.ReadAllText(filePath);
                var lines = content.Split('\n').ToList();

                // Check if updates are already disabled
                if (lines.Any(line => line.Contains("-- LUATOOLS: UPDATES DISABLED!")))
                {
                    var modified = false;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        var trimmed = lines[i].Trim();
                        if (trimmed.StartsWith("--setManifestid"))
                        {
                            lines[i] = lines[i].Substring(2); // Remove --
                            modified = true;
                        }
                    }

                    if (modified)
                    {
                        File.WriteAllText(filePath, string.Join("\n", lines));
                        return "updates_disabled_modified";
                    }
                    return "updates_disabled";
                }

                // Check if file has addappid
                var hasAddAppId = lines.Any(line => line.ToLower().Contains("addappid"));
                if (!hasAddAppId)
                {
                    return "no_addappid";
                }

                // Patch setManifestid lines
                var wasModified = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (trimmed.StartsWith("setManifestid"))
                    {
                        lines[i] = "--" + lines[i];
                        wasModified = true;
                    }
                }

                if (wasModified)
                {
                    File.WriteAllText(filePath, string.Join("\n", lines));
                    return "patched";
                }

                return "no_changes";
            }
            catch (Exception ex)
            {
                throw new Exception($"Error patching {filePath}: {ex.Message}", ex);
            }
        }

        public (bool success, string message) DisableGame(string appId)
        {
            var luaFile = Path.Combine(_stpluginPath, $"{appId}.lua");
            if (!File.Exists(luaFile))
            {
                return (false, $"Lua file not found for {appId}");
            }

            var disabledFile = Path.Combine(_stpluginPath, $"{appId}.lua.disabled");
            if (File.Exists(disabledFile))
            {
                return (false, $"Disabled file already exists for {appId}");
            }

            try
            {
                File.Move(luaFile, disabledFile);
                return (true, $"Successfully disabled {appId}");
            }
            catch (Exception ex)
            {
                return (false, $"Error disabling game: {ex.Message}");
            }
        }

        public (bool success, string message) EnableGame(string appId)
        {
            var disabledFile = Path.Combine(_stpluginPath, $"{appId}.lua.disabled");
            if (!File.Exists(disabledFile))
            {
                return (false, $"Disabled file not found for {appId}");
            }

            var luaFile = Path.Combine(_stpluginPath, $"{appId}.lua");
            if (File.Exists(luaFile))
            {
                return (false, $"Lua file already exists for {appId}");
            }

            try
            {
                File.Move(disabledFile, luaFile);
                return (true, $"Successfully enabled {appId}");
            }
            catch (Exception ex)
            {
                return (false, $"Error enabling game: {ex.Message}");
            }
        }

        public (bool success, string message) DeleteLuaFile(string appId)
        {
            var luaFile = Path.Combine(_stpluginPath, $"{appId}.lua");
            var disabledFile = Path.Combine(_stpluginPath, $"{appId}.lua.disabled");

            string? fileToDelete = null;

            if (File.Exists(luaFile))
            {
                fileToDelete = luaFile;
            }
            else if (File.Exists(disabledFile))
            {
                fileToDelete = disabledFile;
            }
            else
            {
                return (false, $"Lua file not found for {appId}");
            }

            try
            {
                File.Delete(fileToDelete);
                return (true, $"Successfully deleted {appId}");
            }
            catch (Exception ex)
            {
                return (false, $"Error deleting game: {ex.Message}");
            }
        }

        public (bool success, string message) EnableAutoUpdatesForApp(string appId)
        {
            var luaFilePath = Path.Combine(_stpluginPath, $"{appId}.lua");
            if (!File.Exists(luaFilePath))
            {
                return (false, $"Could not find {appId}.lua file");
            }

            try
            {
                var content = File.ReadAllText(luaFilePath);
                var lines = content.Split('\n').ToList();
                bool modified = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    var trimmed = lines[i].Trim();
                    // If setManifestid is not commented, comment it out
                    if (trimmed.StartsWith("setManifestid") && !trimmed.StartsWith("--"))
                    {
                        lines[i] = "--" + lines[i];
                        modified = true;
                    }
                }

                if (modified)
                {
                    File.WriteAllText(luaFilePath, string.Join("\n", lines));
                    return (true, $"Successfully enabled auto-updates for {appId}");
                }

                return (true, $"No changes needed for {appId}");
            }
            catch (Exception ex)
            {
                return (false, $"Failed to enable auto-updates for {appId}: {ex.Message}");
            }
        }

        public (bool success, string message) DisableAutoUpdatesForApp(string appId)
        {
            var luaFilePath = Path.Combine(_stpluginPath, $"{appId}.lua");
            if (!File.Exists(luaFilePath))
            {
                return (false, $"Could not find {appId}.lua file");
            }

            try
            {
                var content = File.ReadAllText(luaFilePath);
                var lines = content.Split('\n').ToList();
                bool modified = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    var trimmed = lines[i].Trim();
                    // If setManifestid is commented out, uncomment it
                    if (trimmed.StartsWith("--setManifestid"))
                    {
                        lines[i] = lines[i].Replace("--setManifestid", "setManifestid");
                        modified = true;
                    }
                }

                if (modified)
                {
                    File.WriteAllText(luaFilePath, string.Join("\n", lines));
                    return (true, $"Successfully disabled auto-updates for {appId}");
                }

                return (true, $"No changes needed for {appId}");
            }
            catch (Exception ex)
            {
                return (false, $"Failed to disable auto-updates for {appId}: {ex.Message}");
            }
        }

        public bool IsAutoUpdatesEnabled(string appId)
        {
            var luaFilePath = Path.Combine(_stpluginPath, $"{appId}.lua");
            if (!File.Exists(luaFilePath))
            {
                return false;
            }

            try
            {
                var content = File.ReadAllText(luaFilePath);
                var lines = content.Split('\n');

                // Check ALL setManifestid lines to determine state
                bool hasUncommented = false;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();

                    // Check for commented setManifestid
                    if (trimmed.StartsWith("--setManifestid"))
                    {
                    }
                    // Check for uncommented setManifestid
                    else if (trimmed.StartsWith("setManifestid"))
                    {
                        hasUncommented = true;
                    }
                }

                // Determine state based on what we found:
                // If ANY uncommented setManifestid exists = updates DISABLED (manifest locked)
                // If ALL setManifestid are commented = updates ENABLED (no manifest lock)
                // If no setManifestid found at all = updates ENABLED (default behavior)
                if (hasUncommented)
                {
                    return false; // Updates disabled (manifest is locked)
                }

                return true; // Updates enabled (all commented or none found)
            }
            catch
            {
                return false;
            }
        }

    }
}
