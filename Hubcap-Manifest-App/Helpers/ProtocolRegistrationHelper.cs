using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;

namespace HubcapManifestApp.Helpers
{
    public static class ProtocolRegistrationHelper
    {
        private const string ProtocolName = "hubcapapp";
        private const string RegistryPath = @"Software\Classes\" + ProtocolName;

        /// <summary>
        /// Legacy URI scheme used by pre-rebrand Solus builds. Registered to
        /// <c>HKCU\Software\Classes\solusapp</c>. We no longer register this scheme,
        /// and we proactively remove its registry entry on startup so stale
        /// <c>solusapp://</c> links don't dead-link to an uninstalled Solus exe.
        /// </summary>
        private const string LegacyProtocolName = "solusapp";
        private const string LegacyRegistryPath = @"Software\Classes\" + LegacyProtocolName;

        public static bool IsProtocolRegistered()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool RegisterProtocol()
        {
            try
            {
                // Evict any legacy Solus-era scheme registration. The scheme was renamed
                // (solusapp:// -> hubcapapp://) as part of the Hubcap rebrand, and keeping
                // the old key around would dead-link to an uninstalled Solus exe.
                UnregisterLegacyProtocol();

                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                    return false;

                // Check if already registered with correct path
                var commandPath = $@"{RegistryPath}\shell\open\command";
                using (var commandKey = Registry.CurrentUser.OpenSubKey(commandPath))
                {
                    if (commandKey != null)
                    {
                        var currentCommand = commandKey.GetValue("")?.ToString() ?? "";
                        var expectedCommand = $"\"{exePath}\" \"%1\"";

                        // If path matches, no need to re-register
                        if (currentCommand.Equals(expectedCommand, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }

                        // Path is different, delete old registration
                        UnregisterProtocol();
                    }
                }

                // Register with new path
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                key.SetValue("", $"URL:{ProtocolName} Protocol");
                key.SetValue("URL Protocol", "");

                using var defaultIcon = key.CreateSubKey("DefaultIcon");
                defaultIcon.SetValue("", $"\"{exePath}\",0");

                using var command = key.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{exePath}\" \"%1\"");

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void UnregisterProtocol()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, false);
            }
            catch
            {
                // Ignore errors during unregistration
            }
        }

        /// <summary>
        /// Removes the legacy <c>solusapp://</c> scheme registration left over from
        /// pre-rebrand installs. Safe to call when the key does not exist. Failures
        /// are swallowed because this is a best-effort cleanup, not a correctness
        /// requirement.
        /// </summary>
        private static void UnregisterLegacyProtocol()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(LegacyRegistryPath, false);
            }
            catch
            {
                // Ignore: legacy key may not exist, or may be locked by another process.
            }
        }

        public static string? ParseProtocolUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return null;

            // Handle both formats: hubcapapp://download/install/400 and "hubcapapp://download/install/400"
            var cleanUrl = url.Trim('"', ' ');

            if (!cleanUrl.StartsWith($"{ProtocolName}://", StringComparison.OrdinalIgnoreCase))
                return null;

            // Remove the protocol prefix
            return cleanUrl.Substring($"{ProtocolName}://".Length);
        }
    }
}
