#nullable disable
// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.IsolatedStorage;
using System.Security.Cryptography;
using ProtoBuf;

namespace DepotDownloader
{
    [ProtoContract]
    public class AccountSettingsStore
    {
        // Member 1 was a Dictionary<string, byte[]> for SentryData.

        [ProtoMember(2, IsRequired = false)]
        public ConcurrentDictionary<string, int> ContentServerPenalty { get; private set; }

        // Member 3 was a Dictionary<string, string> for LoginKeys.

        [ProtoMember(4, IsRequired = false)]
        public Dictionary<string, string> LoginTokens { get; private set; }

        [ProtoMember(5, IsRequired = false)]
        public Dictionary<string, string> GuardData { get; private set; }

        string FileName;

        // Magic bytes to identify DPAPI-encrypted format vs legacy deflate
        static readonly byte[] DpapiMagic = { 0x53, 0x4D, 0x44, 0x50 }; // "SMDP"

        AccountSettingsStore()
        {
            ContentServerPenalty = new ConcurrentDictionary<string, int>();
            LoginTokens = new(StringComparer.OrdinalIgnoreCase);
            GuardData = new(StringComparer.OrdinalIgnoreCase);
        }

        static bool Loaded
        {
            get { return Instance != null; }
        }

        public static AccountSettingsStore Instance;
        static readonly IsolatedStorageFile IsolatedStorage = IsolatedStorageFile.GetUserStoreForAssembly();
        private static readonly object _saveLock = new();

        public static void LoadFromFile(string filename)
        {
            if (Loaded)
                return; // Already loaded, skip

            if (IsolatedStorage.FileExists(filename))
            {
                try
                {
                    using var fs = IsolatedStorage.OpenFile(filename, FileMode.Open, FileAccess.Read);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    var fileBytes = ms.ToArray();

                    Instance = DeserializeWithDpapi(fileBytes);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to load account settings: {0}", ex.Message);
                    Instance = new AccountSettingsStore();
                }
            }
            else
            {
                Instance = new AccountSettingsStore();
            }

            Instance.FileName = filename;
        }

        public static void Save()
        {
            if (!Loaded)
                throw new Exception("Saved config before loading");

            lock (_saveLock)
            {
            try
            {
                // Serialize with protobuf + deflate
                byte[] compressedData;
                using (var compressedStream = new MemoryStream())
                {
                    using (var ds = new DeflateStream(compressedStream, CompressionMode.Compress, leaveOpen: true))
                    {
                        Serializer.Serialize(ds, Instance);
                    }
                    compressedData = compressedStream.ToArray();
                }

                // Encrypt with DPAPI and prepend magic header
                byte[] encryptedData;
                try
                {
                    encryptedData = ProtectedData.Protect(compressedData, null, DataProtectionScope.CurrentUser);
                }
                catch
                {
                    // DPAPI unavailable — fall back to unencrypted deflate
                    using var fs = IsolatedStorage.OpenFile(Instance.FileName, FileMode.Create, FileAccess.Write);
                    fs.Write(compressedData, 0, compressedData.Length);
                    return;
                }

                using (var fs = IsolatedStorage.OpenFile(Instance.FileName, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(DpapiMagic, 0, DpapiMagic.Length);
                    fs.Write(encryptedData, 0, encryptedData.Length);
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine("Failed to save account settings: {0}", ex.Message);
            }
            } // lock
        }

        /// <summary>
        /// Deserializes account settings, handling both DPAPI-encrypted (new) and
        /// legacy deflate-only (old) formats transparently.
        /// </summary>
        private static AccountSettingsStore DeserializeWithDpapi(byte[] fileBytes)
        {
            // Check for DPAPI magic header
            if (fileBytes.Length > DpapiMagic.Length &&
                fileBytes[0] == DpapiMagic[0] && fileBytes[1] == DpapiMagic[1] &&
                fileBytes[2] == DpapiMagic[2] && fileBytes[3] == DpapiMagic[3])
            {
                // New DPAPI-encrypted format
                var encryptedData = new byte[fileBytes.Length - DpapiMagic.Length];
                Array.Copy(fileBytes, DpapiMagic.Length, encryptedData, 0, encryptedData.Length);

                byte[] compressedData;
                try
                {
                    compressedData = ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.CurrentUser);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    Console.WriteLine("Failed to decrypt account settings (wrong user or corrupt data). Starting fresh.");
                    return new AccountSettingsStore();
                }

                using var compressedStream = new MemoryStream(compressedData);
                using var ds = new DeflateStream(compressedStream, CompressionMode.Decompress);
                return Serializer.Deserialize<AccountSettingsStore>(ds);
            }
            else
            {
                // Legacy deflate-only format — deserialize directly
                using var ms = new MemoryStream(fileBytes);
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                return Serializer.Deserialize<AccountSettingsStore>(ds);
            }
        }
    }
}
