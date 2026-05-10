using System;
using System.Collections.Generic;

namespace HubcapManifestApp.Services
{
    /// <summary>
    /// Service to track recently accessed games for quick access from tray
    /// </summary>
    public class RecentGamesService
    {
        private readonly LibraryDatabaseService _dbService;
        private readonly LoggerService _logger;

        public RecentGamesService(LibraryDatabaseService dbService, LoggerService logger)
        {
            _dbService = dbService;
            _logger = logger;
        }

        /// <summary>
        /// Mark a game as recently accessed
        /// </summary>
        public void MarkAsRecentlyAccessed(string appId)
        {
            try
            {
                _dbService.UpdateLastAccessed(appId, DateTime.Now);
                _logger.Info($"Marked {appId} as recently accessed");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to mark {appId} as recent: {ex.Message}");
            }
        }

        /// <summary>
        /// Get list of recently accessed games (max 5)
        /// </summary>
        public List<RecentGameInfo> GetRecentGames(int limit = 5)
        {
            try
            {
                return _dbService.GetRecentGames(limit);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get recent games: {ex.Message}");
                return new List<RecentGameInfo>();
            }
        }
    }

    public class RecentGameInfo
    {
        public string AppId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? IconPath { get; set; }
        public DateTime LastAccessed { get; set; }
        public string LocalPath { get; set; } = "";
    }
}
