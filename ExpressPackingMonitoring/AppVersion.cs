using System;
using System.Reflection;

namespace ExpressPackingMonitoring
{
    public static class AppVersion
    {
        private const string FallbackCurrent = "v0.0.0";

        public static string Current => GetCurrentVersion();

        private static string GetCurrentVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string? informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (IsUsefulVersion(informationalVersion))
                return NormalizeDisplayVersion(informationalVersion!);

            return FallbackCurrent;
        }

        private static bool IsUsefulVersion(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            string version = value.Trim();
            if (version.StartsWith("1.0.0+", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private static string NormalizeDisplayVersion(string value)
        {
            string version = value.Trim();
            int metadataIndex = version.IndexOf('+');
            return metadataIndex > 0 ? version[..metadataIndex] : version;
        }
    }
}
