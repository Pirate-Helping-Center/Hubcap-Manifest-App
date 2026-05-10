using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HubcapManifestApp.Helpers
{
    /// <summary>
    /// Parser for Valve Data Format (VDF) files used by Steam
    /// Handles both .vdf and .acf files (appmanifest files)
    /// </summary>
    public class VdfParser
    {
        public static Dictionary<string, object> Parse(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"VDF file not found: {filePath}");

            var content = File.ReadAllText(filePath, Encoding.UTF8);
            return ParseContent(content);
        }

        public static Dictionary<string, object> ParseContent(string content)
        {
            var reader = new StringReader(content);
            return ParseObject(reader);
        }

        private static Dictionary<string, object> ParseObject(StringReader reader)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            string? line;
            string? pendingKey = null;

            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();

                // Skip empty lines and comments
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//"))
                    continue;

                // End of object
                if (line == "}")
                    break;

                // Opening brace for pending key
                if (line == "{")
                {
                    if (pendingKey != null)
                    {
                        result[pendingKey] = ParseObject(reader);
                        pendingKey = null;
                    }
                    continue;
                }

                // Parse key-value pair
                var parts = SplitKeyValue(line);
                if (parts.Length == 2)
                {
                    var key = parts[0];
                    var value = parts[1];
                    result[key] = value;
                }
                else if (parts.Length == 1)
                {
                    // Key only - expect opening brace on next line
                    pendingKey = parts[0];
                }
            }

            return result;
        }

        private static string[] SplitKeyValue(string line)
        {
            var parts = new List<string>();
            var inQuotes = false;
            var current = new StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                var ch = line[i];

                if (ch == '"')
                {
                    if (inQuotes)
                    {
                        // End of quoted string
                        parts.Add(current.ToString());
                        current.Clear();
                        inQuotes = false;
                    }
                    else
                    {
                        inQuotes = true;
                    }
                }
                else if (inQuotes)
                {
                    if (ch == '\\' && i + 1 < line.Length && line[i + 1] == '\\')
                    {
                        current.Append('\\');
                        i++;
                    }
                    else
                    {
                        current.Append(ch);
                    }
                }
                else if (char.IsWhiteSpace(ch))
                {
                    // Skip whitespace outside quotes
                    continue;
                }
                else if (ch == '{' || ch == '}')
                {
                    // Handle braces
                    if (current.Length > 0)
                    {
                        parts.Add(current.ToString());
                        current.Clear();
                    }
                }
            }

            if (current.Length > 0)
            {
                parts.Add(current.ToString());
            }

            return parts.ToArray();
        }

        public static string GetValue(Dictionary<string, object> data, string key, string defaultValue = "")
        {
            if (data.TryGetValue(key, out var value))
            {
                return value?.ToString() ?? defaultValue;
            }
            return defaultValue;
        }

        public static Dictionary<string, object>? GetObject(Dictionary<string, object> data, string key)
        {
            if (data.TryGetValue(key, out var value) && value is Dictionary<string, object> dict)
            {
                return dict;
            }
            return null;
        }

        public static long GetLong(Dictionary<string, object> data, string key, long defaultValue = 0)
        {
            if (data.TryGetValue(key, out var value))
            {
                if (long.TryParse(value.ToString(), out var result))
                    return result;
            }
            return defaultValue;
        }

        public static int GetInt(Dictionary<string, object> data, string key, int defaultValue = 0)
        {
            if (data.TryGetValue(key, out var value))
            {
                if (int.TryParse(value.ToString(), out var result))
                    return result;
            }
            return defaultValue;
        }
    }
}
