using ExpressPackingMonitoring.Config;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

internal sealed class MobileOrderReceiverRegistry
{
    internal const int OrderReceiverPort = 5280;
    private const int MaxAddresses = 7;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private readonly string _path;
    private readonly Func<DateTime> _utcNow;
    private readonly object _sync = new();
    private List<Entry> _entries;

    internal MobileOrderReceiverRegistry(string? path = null, Func<DateTime>? utcNow = null)
    {
        _path = path ?? GetDefaultPath();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _entries = Load(_path);
    }

    internal void Register(IPAddress? remoteAddress)
    {
        string? address = NormalizePrivateIpv4(remoteAddress);
        if (address == null) return;

        lock (_sync)
        {
            DateTime now = _utcNow();
            _entries.RemoveAll(item =>
                string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase)
                || now - item.LastSeenUtc > Retention);
            _entries.Insert(0, new Entry(address, now));
            if (_entries.Count > MaxAddresses)
                _entries.RemoveRange(MaxAddresses, _entries.Count - MaxAddresses);
            try { Save(); } catch { }
        }
    }

    internal IReadOnlyList<string> GetAuthorities()
    {
        lock (_sync)
        {
            DateTime now = _utcNow();
            return _entries
                .Where(item => now - item.LastSeenUtc <= Retention)
                .OrderByDescending(item => item.LastSeenUtc)
                .Select(item => $"{item.Address}:{OrderReceiverPort}")
                .ToArray();
        }
    }

    internal static IReadOnlyList<string> GetDefaultAuthorities() =>
        new MobileOrderReceiverRegistry().GetAuthorities();

    internal static string GetDefaultPath() =>
        Path.Combine(AppPaths.CacheDir, "mobile-backup", "order-receivers.json");

    private static string? NormalizePrivateIpv4(IPAddress? address)
    {
        if (address == null) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return null;

        byte[] bytes = address.GetAddressBytes();
        bool isPrivate = bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
        return isPrivate ? address.ToString() : null;
    }

    private static List<Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<Entry>();
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new List<Entry>();
        }
        catch
        {
            return new List<Entry>();
        }
    }

    private void Save()
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_entries));
        File.Move(temporaryPath, _path, true);
    }

    private sealed record Entry(string Address, DateTime LastSeenUtc);
}
