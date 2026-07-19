using System.IO;
using System.Net;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Services;

internal static class MobileConnectionService
{
    public static string BuildAccessUrl(string address, bool requireAccessKey, string? accessKey)
    {
        string url = WorkstationNetwork.ToUrl(address).TrimEnd('/');
        // App pairing always needs the key for mobile-backup-v1, even when the
        // browser-facing video page itself does not require authentication.
        if (string.IsNullOrWhiteSpace(accessKey))
            return url;

        return $"{url}/?key={Uri.EscapeDataString(accessKey.Trim())}";
    }

    public static bool TryBuildUsableAccessUrl(
        string address,
        bool requireAccessKey,
        string? accessKey,
        out string url)
    {
        url = "";
        string normalizedAddress = WorkstationNetwork.NormalizeAddress(address);
        if (string.IsNullOrWhiteSpace(normalizedAddress))
            return false;

        string candidate = BuildAccessUrl(normalizedAddress, requireAccessKey, accessKey);
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.IsLoopback
            || IsUnusableIpAddress(uri.Host))
        {
            return false;
        }

        url = candidate;
        return true;
    }

    public static BitmapSource CreateQrBitmap(string url, int size = 260)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("二维码网址不能为空", nameof(url));

        int normalizedSize = Math.Clamp(size, 160, 1024);
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Width = normalizedSize,
                Height = normalizedSize,
                Margin = 2,
                PureBarcode = true
            }
        };

        var pixelData = writer.Write(url);
        var source = BitmapSource.Create(
            pixelData.Width,
            pixelData.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixelData.Pixels,
            pixelData.Width * 4);
        source.Freeze();
        return source;
    }

    public static byte[] CreateQrPng(string url, int size = 260)
    {
        BitmapSource bitmap = CreateQrBitmap(url, size);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static string CreateQrDataUri(string url, int size = 260)
    {
        return $"data:image/png;base64,{Convert.ToBase64String(CreateQrPng(url, size))}";
    }

    private static bool IsUnusableIpAddress(string host)
    {
        if (!IPAddress.TryParse(host, out IPAddress? address))
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

        return IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6None);
    }
}
