using System;
using System.IO;

namespace HubcapManifestApp.Helpers
{
    /// <summary>
    /// Handles one-time migration of the per-user AppData folder from the pre-rebrand
    /// Solus layout (<c>%APPDATA%\SolusManifestApp</c>) to the new Hubcap layout
    /// (<c>%APPDATA%\HubcapManifestApp</c>).
    /// </summary>
    /// <remarks>
    /// Runs at startup before any service reads from AppData. Copies the entire folder
    /// (settings.json, caches, themes, logs, databases, etc.) while preserving the legacy
    /// Solus folder intact as a rollback/backup. Writes a marker file in the new folder
    /// so the migration only runs once.
    ///
    /// Safe to call on every startup — becomes a no-op once the Hubcap folder exists.
    /// Never throws: a migration failure must not block app startup; in the worst case
    /// the user re-enters their settings on the fresh Hubcap folder.
    /// </remarks>
    public static class SettingsMigrationHelper
    {
        /// <summary>Legacy pre-rebrand AppData folder name (Solus).</summary>
        private const string LegacyFolderName = "SolusManifestApp";

        /// <summary>Marker file dropped in the new folder after a successful copy.</summary>
        private const string MigrationMarkerFileName = ".migrated_from_solus";

        /// <summary>
        /// Result of a migration attempt, for logging by the caller.
        /// </summary>
        public enum MigrationResult
        {
            /// <summary>No legacy folder on disk; nothing to migrate.</summary>
            NotApplicable,
            /// <summary>New folder already initialized; skipped to avoid overwriting.</summary>
            AlreadyMigrated,
            /// <summary>Files were copied from the legacy folder to the new folder.</summary>
            Migrated,
            /// <summary>Migration attempt failed; caller should fall back to a fresh folder.</summary>
            Failed
        }

        /// <summary>
        /// Performs the one-time migration if applicable. Idempotent.
        /// </summary>
        public static MigrationResult MigrateIfNeeded()
        {
            try
            {
                var appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(appDataRoot))
                {
                    return MigrationResult.NotApplicable;
                }

                var legacyPath = Path.Combine(appDataRoot, LegacyFolderName);
                var newPath = Path.Combine(appDataRoot, AppConstants.AppDataFolderName);

                // No legacy folder → fresh install or already cleaned up. Nothing to do.
                if (!Directory.Exists(legacyPath))
                {
                    return MigrationResult.NotApplicable;
                }

                // New folder already exists → either a prior migration completed, or the user
                // ran the Hubcap build without legacy data present. Either way, don't overwrite.
                if (Directory.Exists(newPath))
                {
                    return MigrationResult.AlreadyMigrated;
                }

                Directory.CreateDirectory(newPath);
                CopyDirectoryRecursive(legacyPath, newPath);

                // Drop a marker so the origin of this folder is auditable after the fact.
                try
                {
                    var markerPath = Path.Combine(newPath, MigrationMarkerFileName);
                    File.WriteAllText(
                        markerPath,
                        $"Migrated from {legacyPath} at {DateTime.UtcNow:O}{Environment.NewLine}" +
                        $"Legacy folder preserved as backup — safe to delete manually once Hubcap install is verified.{Environment.NewLine}");
                }
                catch
                {
                    // Marker file is informational only; do not fail the whole migration over it.
                }

                return MigrationResult.Migrated;
            }
            catch
            {
                // Never let a migration failure block startup.
                return MigrationResult.Failed;
            }
        }

        /// <summary>
        /// Recursive directory copy. Tolerates individual file failures (locked files, ACL
        /// denials, transient I/O errors) by skipping rather than aborting — partial data
        /// in the new folder is always better than no data.
        /// </summary>
        private static void CopyDirectoryRecursive(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var filePath in Directory.EnumerateFiles(source))
            {
                var destFile = Path.Combine(destination, Path.GetFileName(filePath));
                try
                {
                    File.Copy(filePath, destFile, overwrite: false);
                }
                catch
                {
                    // Skip files that fail to copy (e.g., locked by another process);
                    // continue with the rest of the migration.
                }
            }

            foreach (var dirPath in Directory.EnumerateDirectories(source))
            {
                var destDir = Path.Combine(destination, Path.GetFileName(dirPath));
                try
                {
                    CopyDirectoryRecursive(dirPath, destDir);
                }
                catch
                {
                    // Skip subtrees that fail to copy; preserve as much data as possible.
                }
            }
        }
    }
}
