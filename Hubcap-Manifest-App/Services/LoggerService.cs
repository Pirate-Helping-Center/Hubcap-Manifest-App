using HubcapManifestApp.Helpers;
using HubcapManifestApp.Interfaces;
using System;
using System.IO;
using System.Linq;

namespace HubcapManifestApp.Services
{
    public class LoggerService : ILoggerService, IDisposable
    {
        private readonly object _lock = new object();
        private readonly string _logFilePath;
        private const long MAX_LOG_SIZE = 8 * 1024 * 1024; // 8MB
        private const long TRIM_TO_SIZE = 6 * 1024 * 1024; // Trim to 6MB when rotating
        private StreamWriter? _writer;
        private DateTime _lastFlush = DateTime.MinValue;

        public LoggerService(string logName = AppConstants.AppDataFolderName)
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppConstants.AppDataFolderName
            );
            Directory.CreateDirectory(appDataPath);

            // Use simple named log file (no timestamp)
            _logFilePath = Path.Combine(appDataPath, $"{logName}.log");

            Log("INFO", "Logger initialized");
            Log("INFO", $"Log file: {_logFilePath}");
        }

        public void Log(string level, string message)
        {
            lock (_lock)
            {
                try
                {
                    // Check if log file needs trimming
                    if (File.Exists(_logFilePath))
                    {
                        var fileInfo = new FileInfo(_logFilePath);
                        if (fileInfo.Length >= MAX_LOG_SIZE)
                        {
                            // Close writer before trimming
                            CloseWriter();
                            TrimLogFile();
                        }
                    }

                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    var logEntry = $"[{timestamp}] [{level}] {message}";

                    // Use a persistent StreamWriter to avoid open/close per line
                    EnsureWriter();
                    _writer!.WriteLine(logEntry);

                    // Flush periodically (every 500ms) to balance performance and durability
                    var now = DateTime.Now;
                    if ((now - _lastFlush).TotalMilliseconds >= 500)
                    {
                        _writer.Flush();
                        _lastFlush = now;
                    }

                    // Also write to debug output for convenience
                    System.Diagnostics.Debug.WriteLine(logEntry);
                }
                catch
                {
                    // Silently fail if logging fails
                }
            }
        }

        private void EnsureWriter()
        {
            if (_writer == null)
            {
                _writer = new StreamWriter(_logFilePath, append: true) { AutoFlush = false };
            }
        }

        private void CloseWriter()
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }

        private void TrimLogFile()
        {
            try
            {
                // Read all lines from the log file
                var allLines = File.ReadAllLines(_logFilePath);

                // Calculate how many lines to keep (approximate based on average line length)
                var currentSize = new FileInfo(_logFilePath).Length;
                var averageLineSize = currentSize / allLines.Length;
                var linesToKeep = (int)(TRIM_TO_SIZE / averageLineSize);

                // Keep only the newest lines
                var linesToWrite = allLines.Skip(Math.Max(0, allLines.Length - linesToKeep)).ToArray();

                // Write back the trimmed content
                File.WriteAllLines(_logFilePath, linesToWrite);
            }
            catch
            {
                // If trimming fails, try to at least clear the file
                try
                {
                    File.WriteAllText(_logFilePath, "");
                }
                catch
                {
                    // Silently fail
                }
            }
        }

        public void Info(string message) => Log("INFO", message);
        public void Debug(string message) => Log("DEBUG", message);
        public void Warning(string message) => Log("WARN", message);
        public void Error(string message) => Log("ERROR", message);

        public string GetLogsFolderPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppConstants.AppDataFolderName
            );
        }

        public void OpenLogsFolder()
        {
            var logsFolderPath = GetLogsFolderPath();
            if (Directory.Exists(logsFolderPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", logsFolderPath);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                CloseWriter();
            }
        }

        public void ClearOldLogs()
        {
            try
            {
                var logsFolderPath = GetLogsFolderPath();
                if (!Directory.Exists(logsFolderPath))
                {
                    return;
                }

                // Clean up old timestamp-based log files from previous versions.
                // Pre-rebrand Solus builds wrote timestamped logs named "solus_YYYYMMDD_HHMMSS.log";
                // current Hubcap builds write a single {logName}.log (see ctor), so these legacy
                // files can safely be purged on startup. Kept post-rebrand so users migrating from
                // Solus don't carry the cruft forward indefinitely.
                var oldLogFiles = Directory.GetFiles(logsFolderPath, "solus_*.log");
                foreach (var logFile in oldLogFiles)
                {
                    try
                    {
                        File.Delete(logFile);
                        Info($"Deleted old timestamp log file: {Path.GetFileName(logFile)}");
                    }
                    catch (Exception ex)
                    {
                        Error($"Failed to delete old log file {Path.GetFileName(logFile)}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Error($"Failed to clear old logs: {ex.Message}");
            }
        }
    }
}
