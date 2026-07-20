namespace ExpressPackingMonitoring.Config;

public static class AppModules
{
    public const string Overview = "overview";
    public const string PcRecording = "pc-recording";
    public const string MobileBackup = "mobile-backup";
    public const string OrderIntegration = "order-integration";
    public const string VideoLibrary = "video-library";
    public const string Settings = "settings";
}

public static class StartupModulePolicy
{
    public static string Resolve(string[]? args)
    {
        args ??= Array.Empty<string>();
        if (args.Any(a => string.Equals(a, "--monitor", StringComparison.OrdinalIgnoreCase)))
            return AppModules.PcRecording;
        if (args.Any(a => string.Equals(a, "--print-station", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(a, "--order-workstation", StringComparison.OrdinalIgnoreCase)))
            return AppModules.OrderIntegration;

        for (int i = 0; i < args.Length; i++)
        {
            string value = args[i] ?? "";
            if (value.StartsWith("--temporary-role=", StringComparison.OrdinalIgnoreCase))
                return FromLegacyRole(value[17..]);
            if (string.Equals(value, "--temporary-role", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return FromLegacyRole(args[i + 1]);
            if (value.StartsWith("--role=", StringComparison.OrdinalIgnoreCase))
                return FromLegacyRole(value[7..]);
            if (string.Equals(value, "--role", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return FromLegacyRole(args[i + 1]);
        }

        return AppModules.Overview;
    }

    private static string FromLegacyRole(string? role) =>
        string.Equals(role, "PrintStation", StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, "print", StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, "printer", StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, "order", StringComparison.OrdinalIgnoreCase)
            ? AppModules.OrderIntegration
            : AppModules.PcRecording;
}
