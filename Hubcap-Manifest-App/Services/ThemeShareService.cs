using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using HubcapManifestApp.Models;

namespace HubcapManifestApp.Services
{
    /// <summary>
    /// Exports a CustomThemePreset to a shareable base64 string (gzipped JSON, <c>HUBCAP1:</c>
    /// prefixed) and reverses the flow to import one. Background image paths are intentionally
    /// stripped on export — we warn the user at the call site instead.
    ///
    /// Rebrand dual-support: the prefix used to be <c>SOLUS1:</c>. Imports still accept the
    /// legacy prefix so theme strings shared before the rebrand keep working indefinitely;
    /// new exports always use the Hubcap prefix. The body format (gzipped JSON of
    /// <see cref="CustomThemePreset"/>) is unchanged across the two prefixes — only the
    /// 7-byte tag at the front differs.
    /// </summary>
    public static class ThemeShareService
    {
        private const string Prefix = "HUBCAP1:";
        /// <summary>Pre-rebrand Solus-era prefix. Accepted on import, never written on export.</summary>
        private const string LegacyPrefix = "SOLUS1:";

        public static string Export(CustomThemePreset preset)
        {
            // Clone + strip image paths so we never leak local file paths into share strings.
            var clone = JsonConvert.DeserializeObject<CustomThemePreset>(
                JsonConvert.SerializeObject(preset))!;
            clone.PageBackgroundImagePath = null;
            clone.SidebarBackgroundImagePath = null;
            clone.Id = Guid.NewGuid().ToString("N"); // new id so imports never collide

            var json = JsonConvert.SerializeObject(clone, Formatting.None);
            var jsonBytes = Encoding.UTF8.GetBytes(json);

            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(jsonBytes, 0, jsonBytes.Length);
            }

            return Prefix + Convert.ToBase64String(output.ToArray());
        }

        public static bool TryImport(string input, out CustomThemePreset? preset, out string? error)
        {
            preset = null;
            error = null;

            if (string.IsNullOrWhiteSpace(input))
            {
                error = "Clipboard is empty.";
                return false;
            }

            input = input.Trim();
            // Accept both the current Hubcap prefix and the legacy Solus prefix so pre-rebrand
            // theme strings stay importable. Body format is identical.
            string? payload = null;
            if (input.StartsWith(Prefix, StringComparison.Ordinal))
                payload = input.Substring(Prefix.Length);
            else if (input.StartsWith(LegacyPrefix, StringComparison.Ordinal))
                payload = input.Substring(LegacyPrefix.Length);

            if (payload == null)
            {
                error = "That doesn't look like a Hubcap theme string.";
                return false;
            }

            try
            {
                var gzipped = Convert.FromBase64String(payload);

                using var input2 = new MemoryStream(gzipped);
                using var gzip = new GZipStream(input2, CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);

                var json = Encoding.UTF8.GetString(output.ToArray());
                var parsed = JsonConvert.DeserializeObject<CustomThemePreset>(json);
                if (parsed == null)
                {
                    error = "The share string was empty after decoding.";
                    return false;
                }

                // Always give imports a fresh id and a flag-ish name if missing.
                parsed.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(parsed.Name)) parsed.Name = "Imported Theme";

                preset = parsed;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Couldn't decode the share string: {ex.Message}";
                return false;
            }
        }
    }
}
