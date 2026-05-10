using System;
using System.Collections.Generic;
using System.IO;

namespace HubcapManifestApp.Services
{
    public class AppInfoEntry
    {
        public uint AppId { get; set; }
        public uint Size { get; set; }
        public uint InfoState { get; set; }
        public uint LastUpdated { get; set; }
        public ulong PicsToken { get; set; }
        public byte[] Sha1Hash { get; set; } = new byte[20];
        public uint ChangeNumber { get; set; }
        public byte[] BinarySha1 { get; set; } = new byte[20];
        public byte[] VdfData { get; set; } = Array.Empty<byte>();
    }

    public class AppInfoService
    {
        private const uint MAGIC_V41 = 0x29;

        private readonly SteamService _steamService;
        private readonly LoggerService? _logger;
        private readonly LuaParser _luaParser;

        private uint _magic;
        private uint _universe;
        private ulong _stringTableOffset;
        private byte[] _stringTableData = Array.Empty<byte>();
        private List<AppInfoEntry> _apps = new();
        private Dictionary<uint, AppInfoEntry> _appIndex = new();
        private HashSet<uint> _modifiedApps = new();

        public AppInfoService(SteamService steamService, LuaParser luaParser, LoggerService? logger = null)
        {
            _steamService = steamService;
            _luaParser = luaParser;
            _logger = logger;
        }

        public string? GetAppInfoPath()
        {
            var steamPath = _steamService.GetSteamPath();
            if (string.IsNullOrEmpty(steamPath))
                return null;

            return Path.Combine(steamPath, "appcache", "appinfo.vdf");
        }

        public bool ReadAppInfo(string? filePath = null)
        {
            filePath ??= GetAppInfoPath();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                _logger?.Error($"AppInfo file not found: {filePath}");
                return false;
            }

            try
            {
                _apps.Clear();
                _appIndex.Clear();
                _modifiedApps.Clear();

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(fs);

                _magic = reader.ReadUInt32();
                _universe = reader.ReadUInt32();

                if (_magic < MAGIC_V41)
                {
                    _logger?.Error($"Unsupported appinfo.vdf version: 0x{_magic:X8}");
                    return false;
                }

                _stringTableOffset = reader.ReadUInt64();

                while (true)
                {
                    long startPos = fs.Position;

                    if (fs.Position + 4 > fs.Length)
                        break;

                    uint appId = reader.ReadUInt32();

                    if (appId == 0)
                        break;

                    var entry = new AppInfoEntry
                    {
                        AppId = appId,
                        Size = reader.ReadUInt32(),
                        InfoState = reader.ReadUInt32(),
                        LastUpdated = reader.ReadUInt32(),
                        PicsToken = reader.ReadUInt64(),
                        Sha1Hash = reader.ReadBytes(20),
                        ChangeNumber = reader.ReadUInt32(),
                        BinarySha1 = reader.ReadBytes(20)
                    };

                    int vdfSize = (int)entry.Size - 60;
                    if (vdfSize > 0)
                    {
                        entry.VdfData = reader.ReadBytes(vdfSize);
                    }

                    _apps.Add(entry);
                    _appIndex[entry.AppId] = entry;
                }

                fs.Seek((long)_stringTableOffset, SeekOrigin.Begin);
                _stringTableData = reader.ReadBytes((int)(fs.Length - fs.Position));

                _logger?.Debug($"Read {_apps.Count} apps from appinfo.vdf");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to read appinfo.vdf: {ex.Message}");
                return false;
            }
        }

        public AppInfoEntry? GetApp(uint appId)
        {
            return _appIndex.TryGetValue(appId, out var entry) ? entry : null;
        }

        public bool AppExists(uint appId)
        {
            return GetApp(appId) != null;
        }

        public bool SetToken(uint appId, ulong newToken)
        {
            var app = GetApp(appId);
            if (app == null)
            {
                _logger?.Debug($"App {appId} not found in appinfo.vdf");
                return false;
            }

            ulong oldToken = app.PicsToken;
            app.PicsToken = newToken;
            _modifiedApps.Add(appId);

            _logger?.Debug($"Set token for app {appId}: {oldToken} -> {newToken}");
            return true;
        }

        public bool SetToken(uint appId, string newToken)
        {
            if (ulong.TryParse(newToken, out ulong token))
            {
                return SetToken(appId, token);
            }
            return false;
        }

        public bool CreateApp(uint appId, ulong token)
        {
            if (AppExists(appId))
            {
                _logger?.Debug($"App {appId} already exists, updating token instead");
                return SetToken(appId, token);
            }

            var placeholderSha1 = new byte[20];
            for (int i = 0; i < 20; i++)
                placeholderSha1[i] = 0x44;

            var entry = new AppInfoEntry
            {
                AppId = appId,
                Size = 60,
                InfoState = 0,
                LastUpdated = 0,
                PicsToken = token,
                Sha1Hash = placeholderSha1,
                ChangeNumber = 0,
                BinarySha1 = new byte[20],
                VdfData = Array.Empty<byte>()
            };

            int insertPos = 0;
            for (int i = 0; i < _apps.Count; i++)
            {
                if (_apps[i].AppId > appId)
                {
                    insertPos = i;
                    break;
                }
                insertPos = i + 1;
            }

            _apps.Insert(insertPos, entry);
            _appIndex[appId] = entry;
            _modifiedApps.Add(appId);

            _logger?.Debug($"Created new app entry: {appId} with token {token}");
            return true;
        }

        public bool CreateApp(uint appId, string token)
        {
            if (ulong.TryParse(token, out ulong tokenValue))
            {
                return CreateApp(appId, tokenValue);
            }
            return false;
        }

        public bool SetOrCreateToken(uint appId, ulong token)
        {
            if (AppExists(appId))
            {
                return SetToken(appId, token);
            }
            return CreateApp(appId, token);
        }

        public bool SetOrCreateToken(uint appId, string token)
        {
            if (ulong.TryParse(token, out ulong tokenValue))
            {
                return SetOrCreateToken(appId, tokenValue);
            }
            return false;
        }

        public int ApplyTokensFromLua(string luaContent, bool createIfMissing = true)
        {
            var tokens = _luaParser.ParseTokens(luaContent);
            int applied = 0;

            foreach (var (appId, token) in tokens)
            {
                if (uint.TryParse(appId, out uint id))
                {
                    bool success;
                    if (createIfMissing)
                    {
                        success = SetOrCreateToken(id, token);
                    }
                    else
                    {
                        success = SetToken(id, token);
                    }

                    if (success)
                    {
                        applied++;
                    }
                }
            }

            return applied;
        }

        public bool WriteAppInfo(string? outputPath = null)
        {
            outputPath ??= GetAppInfoPath();
            if (string.IsNullOrEmpty(outputPath))
            {
                _logger?.Error("No output path for appinfo.vdf");
                return false;
            }

            try
            {
                ulong newStringTableOffset = 16;

                foreach (var app in _apps)
                {
                    newStringTableOffset += 4 + 4 + 4 + 4 + 8 + 20 + 4 + 20 + (ulong)app.VdfData.Length;
                }

                newStringTableOffset += 4;

                using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                using var writer = new BinaryWriter(fs);

                writer.Write(_magic);
                writer.Write(_universe);
                writer.Write(newStringTableOffset);

                foreach (var app in _apps)
                {
                    writer.Write(app.AppId);
                    writer.Write(app.Size);
                    writer.Write(app.InfoState);
                    writer.Write(app.LastUpdated);
                    writer.Write(app.PicsToken);
                    writer.Write(app.Sha1Hash);
                    writer.Write(app.ChangeNumber);
                    writer.Write(app.BinarySha1);
                    writer.Write(app.VdfData);
                }

                writer.Write((uint)0);

                writer.Write(_stringTableData);

                _logger?.Debug($"Wrote appinfo.vdf with {_modifiedApps.Count} modified apps");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to write appinfo.vdf: {ex.Message}");
                return false;
            }
        }

        public int ModifiedCount => _modifiedApps.Count;
    }
}
