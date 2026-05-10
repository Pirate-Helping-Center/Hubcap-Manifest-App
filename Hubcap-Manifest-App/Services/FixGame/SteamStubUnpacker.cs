using HubcapManifestApp.Services.FixGame.Steamless.API.Model;
using HubcapManifestApp.Services.FixGame.Steamless.API.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace HubcapManifestApp.Services.FixGame
{
    /// <summary>
    /// Wraps the ported Steamless unpackers to detect and remove SteamStub DRM.
    /// </summary>
    public class SteamStubUnpacker
    {
        private readonly LoggerService _logger;
        private readonly List<SteamlessPlugin> _plugins = new();
        private readonly SteamlessOptions _options;
        private readonly LoggingService _loggingService = new();

        public event Action<string>? Log;

        public SteamStubUnpacker()
        {
            _logger = new LoggerService("SteamStubUnpacker");
            _options = new SteamlessOptions
            {
                KeepBindSection = true,
                ZeroDosStubData = true,
                DontRealignSections = true,
                RecalculateFileChecksum = false
            };

            _loggingService.AddLogMessage += (sender, e) =>
            {
                LogMsg($"    [Steamless] {e.Message}");
            };

            InitPlugins();
        }

        private void InitPlugins()
        {
            var pluginTypes = new SteamlessPlugin[]
            {
                new Steamless.V10x86.Main(),
                new Steamless.V20x86.Main(),
                new Steamless.V21x86.Main(),
                new Steamless.V30x86.Main(),
                new Steamless.V30x64.Main(),
                new Steamless.V31x86.Main(),
                new Steamless.V31x64.Main(),
            };

            foreach (var plugin in pluginTypes)
            {
                try
                {
                    if (plugin.Initialize(_loggingService))
                        _plugins.Add(plugin);
                }
                catch { }
            }

            LogMsg($"  Loaded {_plugins.Count} Steamless unpackers");
        }

        /// <summary>
        /// Attempts to unpack all SteamStub-protected executables in a directory.
        /// Backs up originals as .exe.bak before unpacking.
        /// Returns count of unpacked files.
        /// </summary>
        public async Task<int> UnpackDirectory(string directory)
        {
            int unpacked = 0;
            var exeFiles = Directory.GetFiles(directory, "*.exe", SearchOption.AllDirectories);

            foreach (var exe in exeFiles)
            {
                try
                {
                    if (await UnpackFile(exe))
                        unpacked++;
                }
                catch (Exception ex)
                {
                    LogMsg($"  Error unpacking {Path.GetFileName(exe)}: {ex.Message}");
                }
            }

            return unpacked;
        }

        /// <summary>
        /// Attempts to unpack a single executable.
        /// Returns true if SteamStub was found and removed.
        /// </summary>
        public async Task<bool> UnpackFile(string filePath)
        {
            // Find a plugin that can process this file
            foreach (var plugin in _plugins)
            {
                try
                {
                    if (plugin.CanProcessFile(filePath))
                    {
                        LogMsg($"  SteamStub detected in {Path.GetFileName(filePath)} ({plugin.Name})");

                        // Backup original
                        var backupPath = filePath + ".steamstub.bak";
                        if (!File.Exists(backupPath))
                        {
                            File.Copy(filePath, backupPath);
                            LogMsg($"  Backed up: {Path.GetFileName(filePath)} -> .steamstub.bak");
                        }

                        // Unpack
                        var result = await Task.Run(() => plugin.ProcessFile(filePath, _options));
                        if (result)
                        {
                            // Steamless writes to {filename}.unpacked.exe — move it over the original
                            var unpackedPath = filePath + ".unpacked.exe";
                            if (File.Exists(unpackedPath))
                            {
                                File.Copy(unpackedPath, filePath, true);
                                File.Delete(unpackedPath);
                                LogMsg($"  Unpacked: {Path.GetFileName(filePath)}");
                            }
                            return true;
                        }
                        else
                        {
                            LogMsg($"  Unpack failed for {Path.GetFileName(filePath)}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogMsg($"  Plugin {plugin.Name} error: {ex.Message}");
                }
            }

            return false;
        }

        private void LogMsg(string msg)
        {
            _logger.Info(msg);
            Log?.Invoke(msg);
        }
    }
}
