using System.Net;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class MobileOrderReceiverRegistryTests
{
    [Fact]
    public void RegisterPersistsPrivateMobileAddressesAndRejectsPublicAddresses()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "receivers.json");
        try
        {
            var registry = new MobileOrderReceiverRegistry(path);
            registry.Register(IPAddress.Parse("192.168.31.205"));
            registry.Register(IPAddress.Parse("8.8.8.8"));
            registry.Register(IPAddress.Loopback);

            Assert.Equal(new[] { "192.168.31.205:5280" }, registry.GetAuthorities());
            Assert.Equal(new[] { "192.168.31.205:5280" }, new MobileOrderReceiverRegistry(path).GetAuthorities());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RegisterKeepsMostRecentSevenAddresses()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "receivers.json");
        try
        {
            var registry = new MobileOrderReceiverRegistry(path);
            for (int index = 1; index <= 8; index++)
                registry.Register(IPAddress.Parse($"192.168.31.{index}"));

            IReadOnlyList<string> addresses = registry.GetAuthorities();
            Assert.Equal(7, addresses.Count);
            Assert.Equal("192.168.31.8:5280", addresses[0]);
            Assert.DoesNotContain("192.168.31.1:5280", addresses);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
