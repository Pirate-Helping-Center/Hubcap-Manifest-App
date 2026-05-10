using Microsoft.Data.Sqlite;
using HubcapManifestApp.Helpers;
using HubcapManifestApp.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace HubcapManifestApp.Services
{
    public class LibraryDatabaseService : IDisposable
    {
        private readonly string _dbPath;
        private readonly LoggerService? _logger;

        public LibraryDatabaseService(LoggerService? logger = null)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dbFolder = Path.Combine(appData, AppConstants.AppDataFolderName);
            Directory.CreateDirectory(dbFolder);
            _dbPath = Path.Combine(appData, AppConstants.AppDataFolderName, "library.db");
            _logger = logger;

            _logger?.Info($"Database path: {_dbPath}");
            InitializeDatabase();
        }

        private SqliteConnection CreateConnection()
        {
            var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            return connection;
        }

        private void InitializeDatabase()
        {
            using var connection = CreateConnection();
            using var command = connection.CreateCommand();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS LibraryItems (
                    AppId TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Description TEXT,
                    Version TEXT,
                    ItemType INTEGER NOT NULL,
                    SizeBytes INTEGER DEFAULT 0,
                    InstallDate TEXT,
                    LastUpdated TEXT,
                    LocalPath TEXT,
                    CachedIconPath TEXT,
                    IconUrl TEXT,
                    LastScanned TEXT NOT NULL,
                    LastAccessed TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_library_items_type ON LibraryItems(ItemType);
                CREATE INDEX IF NOT EXISTS idx_library_items_name ON LibraryItems(Name);
                CREATE INDEX IF NOT EXISTS idx_library_items_last_scanned ON LibraryItems(LastScanned);
                CREATE INDEX IF NOT EXISTS idx_library_items_last_accessed ON LibraryItems(LastAccessed);
            ";
            command.ExecuteNonQuery();
        }

        public void UpsertLibraryItem(LibraryItem item)
        {
            try
            {
                using var connection = CreateConnection();
                using var command = connection.CreateCommand();

                command.CommandText = @"
                    INSERT OR REPLACE INTO LibraryItems
                    (AppId, Name, Description, Version, ItemType, SizeBytes, InstallDate, LastUpdated,
                     LocalPath, CachedIconPath, IconUrl, LastScanned, LastAccessed)
                    VALUES
                    (@AppId, @Name, @Description, @Version, @ItemType, @SizeBytes, @InstallDate, @LastUpdated,
                     @LocalPath, @CachedIconPath, @IconUrl, @LastScanned, @LastAccessed)
                ";

                command.Parameters.AddWithValue("@AppId", item.AppId);
                command.Parameters.AddWithValue("@Name", item.Name ?? "");
                command.Parameters.AddWithValue("@Description", item.Description ?? "");
                command.Parameters.AddWithValue("@Version", item.Version ?? "");
                command.Parameters.AddWithValue("@ItemType", (int)item.ItemType);
                command.Parameters.AddWithValue("@SizeBytes", item.SizeBytes);
                command.Parameters.AddWithValue("@InstallDate", item.InstallDate?.ToString("o") ?? "");
                command.Parameters.AddWithValue("@LastUpdated", item.LastUpdated?.ToString("o") ?? "");
                command.Parameters.AddWithValue("@LocalPath", item.LocalPath ?? "");
                command.Parameters.AddWithValue("@CachedIconPath", item.CachedIconPath ?? "");
                command.Parameters.AddWithValue("@IconUrl", item.IconUrl ?? "");
                command.Parameters.AddWithValue("@LastScanned", DateTime.Now.ToString("o"));
                command.Parameters.AddWithValue("@LastAccessed", "");

                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to upsert library item {item.AppId}: {ex.Message}");
            }
        }

        public void BulkUpsertLibraryItems(List<LibraryItem> items)
        {
            using var connection = CreateConnection();
            using var transaction = connection.BeginTransaction();

            try
            {
                foreach (var item in items)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;

                    command.CommandText = @"
                        INSERT OR REPLACE INTO LibraryItems
                        (AppId, Name, Description, Version, ItemType, SizeBytes, InstallDate, LastUpdated,
                         LocalPath, CachedIconPath, IconUrl, LastScanned, LastAccessed)
                        VALUES
                        (@AppId, @Name, @Description, @Version, @ItemType, @SizeBytes, @InstallDate, @LastUpdated,
                         @LocalPath, @CachedIconPath, @IconUrl, @LastScanned, @LastAccessed)
                    ";

                    command.Parameters.AddWithValue("@AppId", item.AppId);
                    command.Parameters.AddWithValue("@Name", item.Name ?? "");
                    command.Parameters.AddWithValue("@Description", item.Description ?? "");
                    command.Parameters.AddWithValue("@Version", item.Version ?? "");
                    command.Parameters.AddWithValue("@ItemType", (int)item.ItemType);
                    command.Parameters.AddWithValue("@SizeBytes", item.SizeBytes);
                    command.Parameters.AddWithValue("@InstallDate", item.InstallDate?.ToString("o") ?? "");
                    command.Parameters.AddWithValue("@LastUpdated", item.LastUpdated?.ToString("o") ?? "");
                    command.Parameters.AddWithValue("@LocalPath", item.LocalPath ?? "");
                    command.Parameters.AddWithValue("@CachedIconPath", item.CachedIconPath ?? "");
                    command.Parameters.AddWithValue("@IconUrl", item.IconUrl ?? "");
                    command.Parameters.AddWithValue("@LastScanned", DateTime.Now.ToString("o"));
                    command.Parameters.AddWithValue("@LastAccessed", "");

                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger?.Error($"Failed to bulk upsert library items: {ex.Message}");
                throw;
            }
        }

        public List<LibraryItem> GetAllLibraryItems()
        {
            var items = new List<LibraryItem>();

            try
            {
                using var connection = CreateConnection();
                using var command = connection.CreateCommand();

                command.CommandText = "SELECT * FROM LibraryItems ORDER BY Name";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    items.Add(MapToLibraryItem(reader));
                }
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to get library items: {ex.Message}");
            }

            return items;
        }

        public LibraryItem? GetLibraryItem(string appId)
        {
            try
            {
                using var connection = CreateConnection();
                using var command = connection.CreateCommand();

                command.CommandText = "SELECT * FROM LibraryItems WHERE AppId = @AppId";
                command.Parameters.AddWithValue("@AppId", appId);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    return MapToLibraryItem(reader);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to get library item {appId}: {ex.Message}");
            }

            return null;
        }

        public void UpdateIconPath(string appId, string iconPath)
        {
            try
            {
                using var connection = CreateConnection();
                using var command = connection.CreateCommand();

                command.CommandText = "UPDATE LibraryItems SET CachedIconPath = @IconPath WHERE AppId = @AppId";
                command.Parameters.AddWithValue("@IconPath", iconPath);
                command.Parameters.AddWithValue("@AppId", appId);

                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to update icon path for {appId}: {ex.Message}");
            }
        }

        public void DeleteLibraryItem(string appId)
        {
            try
            {
                using var connection = CreateConnection();
                using var command = connection.CreateCommand();

                command.CommandText = "DELETE FROM LibraryItems WHERE AppId = @AppId";
                command.Parameters.AddWithValue("@AppId", appId);

                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to delete library item {appId}: {ex.Message}");
            }
        }

        public bool HasRecentData(TimeSpan maxAge)
        {
            _logger?.Debug("[LibraryDatabaseService] HasRecentData() called");
            try
            {
                using var connection = CreateConnection();
                using var command = connection.CreateCommand();

                command.CommandText = "SELECT COUNT(*) FROM LibraryItems";
                var count = Convert.ToInt32(command.ExecuteScalar());
                _logger?.Debug($"[LibraryDatabaseService] Item count: {count}");

                if (count == 0)
                {
                    _logger?.Debug("[LibraryDatabaseService] No items in database, returning false");
                    return false;
                }

                command.CommandText = "SELECT MAX(LastScanned) FROM LibraryItems";
                var lastScanned = command.ExecuteScalar()?.ToString();
                _logger?.Debug($"[LibraryDatabaseService] LastScanned: {lastScanned}");

                if (string.IsNullOrEmpty(lastScanned))
                {
                    _logger?.Debug("[LibraryDatabaseService] LastScanned is null/empty, returning false");
                    return false;
                }

                var lastScannedDate = DateTime.Parse(lastScanned, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var age = DateTime.Now - lastScannedDate;
                var result = age < maxAge;
                _logger?.Info($"[LibraryDatabaseService] Cache age: {age.TotalMinutes:F1} minutes, maxAge: {maxAge.TotalMinutes:F1} minutes, hasRecentData: {result}");
                return result;
            }
            catch (Exception ex)
            {
                _logger?.Error($"[LibraryDatabaseService] HasRecentData failed: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private LibraryItem MapToLibraryItem(SqliteDataReader reader)
        {
            // Helper to safely parse DateTime with culture-invariant format
            DateTime? SafeParseDateTime(string columnName)
            {
                if (reader.IsDBNull(reader.GetOrdinal(columnName)))
                    return null;

                var dateStr = reader.GetString(reader.GetOrdinal(columnName));
                if (string.IsNullOrWhiteSpace(dateStr))
                    return null;

                if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
                    return result;

                // Fallback for old data that might not be in ISO format
                if (DateTime.TryParse(dateStr, out var fallbackResult))
                    return fallbackResult;

                return null;
            }

            return new LibraryItem
            {
                AppId = reader.GetString(reader.GetOrdinal("AppId")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? "" : reader.GetString(reader.GetOrdinal("Description")),
                Version = reader.IsDBNull(reader.GetOrdinal("Version")) ? "" : reader.GetString(reader.GetOrdinal("Version")),
                ItemType = (LibraryItemType)reader.GetInt32(reader.GetOrdinal("ItemType")),
                SizeBytes = reader.GetInt64(reader.GetOrdinal("SizeBytes")),
                InstallDate = SafeParseDateTime("InstallDate"),
                LastUpdated = SafeParseDateTime("LastUpdated"),
                LocalPath = reader.IsDBNull(reader.GetOrdinal("LocalPath")) ? "" : reader.GetString(reader.GetOrdinal("LocalPath")),
                CachedIconPath = reader.IsDBNull(reader.GetOrdinal("CachedIconPath")) ? null : reader.GetString(reader.GetOrdinal("CachedIconPath")),
                IconUrl = reader.IsDBNull(reader.GetOrdinal("IconUrl")) ? "" : reader.GetString(reader.GetOrdinal("IconUrl"))
            };
        }

        public void UpdateLastAccessed(string appId, DateTime lastAccessed)
        {
            try
            {
                using var connection = CreateConnection();
                using var command = connection.CreateCommand();

                command.CommandText = "UPDATE LibraryItems SET LastAccessed = @LastAccessed WHERE AppId = @AppId";
                command.Parameters.AddWithValue("@LastAccessed", lastAccessed.ToString("o"));
                command.Parameters.AddWithValue("@AppId", appId);

                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to update last accessed for {appId}: {ex.Message}");
            }
        }

        public List<RecentGameInfo> GetRecentGames(int limit = 5)
        {
            var recentGames = new List<RecentGameInfo>();

            try
            {
                using var connection = CreateConnection();
                using var command = connection.CreateCommand();

                command.CommandText = @"
                    SELECT AppId, Name, CachedIconPath, LastAccessed, LocalPath
                    FROM LibraryItems
                    WHERE LastAccessed IS NOT NULL AND LastAccessed != ''
                    ORDER BY LastAccessed DESC
                    LIMIT @Limit";
                command.Parameters.AddWithValue("@Limit", limit);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var lastAccessedStr = reader.GetString(reader.GetOrdinal("LastAccessed"));
                    if (!string.IsNullOrEmpty(lastAccessedStr))
                    {
                        DateTime lastAccessed;
                        // Try culture-invariant first (ISO 8601)
                        if (!DateTime.TryParse(lastAccessedStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastAccessed))
                        {
                            // Fallback to current culture for old data
                            if (!DateTime.TryParse(lastAccessedStr, out lastAccessed))
                                continue; // Skip invalid dates
                        }

                        recentGames.Add(new RecentGameInfo
                        {
                            AppId = reader.GetString(reader.GetOrdinal("AppId")),
                            Name = reader.GetString(reader.GetOrdinal("Name")),
                            IconPath = reader.IsDBNull(reader.GetOrdinal("CachedIconPath")) ? null : reader.GetString(reader.GetOrdinal("CachedIconPath")),
                            LastAccessed = lastAccessed,
                            LocalPath = reader.IsDBNull(reader.GetOrdinal("LocalPath")) ? "" : reader.GetString(reader.GetOrdinal("LocalPath"))
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to get recent games: {ex.Message}");
            }

            return recentGames;
        }

        public void Dispose()
        {
            // No persistent connection to dispose
        }
    }
}
