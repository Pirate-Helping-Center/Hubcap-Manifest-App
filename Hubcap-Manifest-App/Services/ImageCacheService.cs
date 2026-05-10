using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HubcapManifestApp.Services
{
    /// <summary>
    /// In-memory cache service for BitmapImage objects to improve Library page performance.
    /// Caches decoded images in memory to avoid repeated disk I/O and image decoding.
    /// </summary>
    public class ImageCacheService
    {
        private readonly Dictionary<string, BitmapImage> _imageCache = new();
        private readonly object _cacheLock = new object();
        private readonly LoggerService? _logger;
        private const int MAX_CACHE_SIZE = 200; // Maximum number of images to cache
        private const int DECODE_PIXEL_WIDTH = 280; // Decode images at display size for memory optimization

        public ImageCacheService(LoggerService? logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gets a BitmapImage from cache, or loads it asynchronously if not cached.
        /// </summary>
        /// <param name="appId">Steam App ID</param>
        /// <param name="imagePath">Full path to the image file on disk</param>
        /// <returns>BitmapImage object ready for binding, or null if failed</returns>
        public async Task<BitmapImage?> GetImageAsync(string appId, string? imagePath)
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                return null;
            }

            var cacheKey = $"steam_{appId}";

            // Check cache first (thread-safe)
            lock (_cacheLock)
            {
                if (_imageCache.TryGetValue(cacheKey, out var cachedImage))
                {
                    _logger?.Debug($"Image cache HIT for {appId}");
                    return cachedImage;
                }
            }

            // Not in cache - load asynchronously
            _logger?.Debug($"Image cache MISS for {appId}, loading from disk: {imagePath}");

            try
            {
                // Load and decode image on background thread
                var bitmap = await Task.Run(() => LoadBitmapImage(imagePath));

                if (bitmap != null)
                {
                    // Add to cache (thread-safe)
                    lock (_cacheLock)
                    {
                        // Check if cache is full
                        if (_imageCache.Count >= MAX_CACHE_SIZE)
                        {
                            _logger?.Info($"Image cache full ({MAX_CACHE_SIZE} items), clearing oldest entries");
                            // Simple strategy: clear 20% of cache to make room
                            var itemsToRemove = MAX_CACHE_SIZE / 5;
                            var keysToRemove = new List<string>();
                            int removed = 0;

                            foreach (var key in _imageCache.Keys)
                            {
                                keysToRemove.Add(key);
                                removed++;
                                if (removed >= itemsToRemove)
                                    break;
                            }

                            foreach (var key in keysToRemove)
                            {
                                _imageCache.Remove(key);
                            }
                        }

                        // Add to cache if not already there (another thread might have added it)
                        if (!_imageCache.ContainsKey(cacheKey))
                        {
                            _imageCache[cacheKey] = bitmap;
                            _logger?.Info($"✓ Cached image for {appId} (cache size: {_imageCache.Count})");
                        }
                    }
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to load image for {appId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Loads and decodes a BitmapImage from disk with optimization settings.
        /// </summary>
        private BitmapImage LoadBitmapImage(string filePath)
        {
            var bitmap = new BitmapImage();

            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // Load and cache immediately
            bitmap.DecodePixelWidth = DECODE_PIXEL_WIDTH;  // Decode at display size (saves memory)
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.EndInit();

            // Freeze the bitmap to make it thread-safe and cross-thread accessible
            bitmap.Freeze();

            return bitmap;
        }

        /// <summary>
        /// Gets a synchronous version of the cached image (for UI binding).
        /// Returns null if not in cache - use GetImageAsync to load.
        /// </summary>
        public BitmapImage? GetCachedImage(string appId)
        {
            var cacheKey = $"steam_{appId}";

            lock (_cacheLock)
            {
                if (_imageCache.TryGetValue(cacheKey, out var cachedImage))
                {
                    return cachedImage;
                }
            }

            return null;
        }

        /// <summary>
        /// Pre-loads multiple images into cache asynchronously.
        /// </summary>
        public async Task PreloadImagesAsync(Dictionary<string, string> appIdToPathMap)
        {
            _logger?.Info($"Pre-loading {appIdToPathMap.Count} images into cache...");

            var tasks = new List<Task>();

            foreach (var kvp in appIdToPathMap)
            {
                tasks.Add(GetImageAsync(kvp.Key, kvp.Value));
            }

            await Task.WhenAll(tasks);

            _logger?.Info($"✓ Pre-load complete. Cache size: {_imageCache.Count}");
        }

        /// <summary>
        /// Clears all cached images from memory.
        /// </summary>
        public void ClearCache()
        {
            lock (_cacheLock)
            {
                var count = _imageCache.Count;
                _imageCache.Clear();
                _logger?.Info($"Image cache cleared ({count} items removed)");
            }
        }

        /// <summary>
        /// Gets the current number of cached images.
        /// </summary>
        public int GetCacheSize()
        {
            lock (_cacheLock)
            {
                return _imageCache.Count;
            }
        }

        /// <summary>
        /// Estimates memory usage of cached images in MB.
        /// Rough estimation: Each decoded 280×160 image ≈ 50-100KB
        /// </summary>
        public double GetEstimatedMemoryUsageMB()
        {
            lock (_cacheLock)
            {
                // Rough estimate: 75KB average per image
                return (_imageCache.Count * 75.0) / 1024.0;
            }
        }
    }
}
