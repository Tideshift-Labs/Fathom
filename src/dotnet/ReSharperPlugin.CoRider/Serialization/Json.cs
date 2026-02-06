using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReSharperPlugin.CoRider.Serialization;

/// <summary>
/// JSON serialization utilities with consistent settings across the plugin.
/// </summary>
public static class Json
{
        private static readonly JsonSerializerOptions DefaultOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        private static readonly JsonSerializerOptions IndentedOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        /// <summary>
        /// Serialize an object to JSON string.
        /// </summary>
        public static string Serialize<T>(T obj, bool indented = false)
        {
            return JsonSerializer.Serialize(obj, indented ? IndentedOptions : DefaultOptions);
        }

        /// <summary>
        /// Deserialize a JSON string to an object.
        /// </summary>
        public static T Deserialize<T>(string json)
        {
            return JsonSerializer.Deserialize<T>(json, DefaultOptions);
        }

        /// <summary>
        /// Try to deserialize a JSON string, returning default(T) on failure.
        /// </summary>
        public static T TryDeserialize<T>(string json, T defaultValue = default)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json, DefaultOptions);
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Escape a string for safe inclusion in JSON.
        /// Useful when building JSON manually (e.g., for debug output).
        /// </summary>
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }
    }
