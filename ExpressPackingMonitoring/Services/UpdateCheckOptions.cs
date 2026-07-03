using System;
using System.IO;

namespace ExpressPackingMonitoring.Services
{
    public static class UpdateCheckOptions
    {
        public const string UrlKey = "UPDATE_CHECK_URL";
        private const string DefaultCheckUrl = "https://api.github.com/repos/m-RNA/ExpressPackingMonitoring/releases/latest";

        public static string GetUpdateCheckUrl()
        {
            string? value = Environment.GetEnvironmentVariable(UrlKey);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();

            value = ReadEnvFileValue();
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();

            return DefaultCheckUrl;
        }

        private static string? ReadEnvFileValue()
        {
            foreach (string path in GetEnvFileCandidates())
            {
                if (!File.Exists(path)) continue;

                foreach (string rawLine in File.ReadLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;

                    string key = line[..separator].Trim();
                    if (!string.Equals(key, UrlKey, StringComparison.OrdinalIgnoreCase)) continue;

                    return line[(separator + 1)..].Trim().Trim('"', '\'');
                }
            }

            return null;
        }

        private static string[] GetEnvFileCandidates()
        {
            string baseDir = AppContext.BaseDirectory;
            string currentDir = Environment.CurrentDirectory;
            return new[]
            {
                Path.Combine(baseDir, ".env"),
                Path.Combine(currentDir, ".env")
            };
        }
    }
}
