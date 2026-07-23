using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class WebRequestLimitTests
{
    [Theory]
    [InlineData("secret-key", "secret-key", true)]
    [InlineData("secret-key", "SECRET-KEY", false)]
    [InlineData("", "secret-key", false)]
    public void AccessKeysEqual_UsesExactComparison(string left, string right, bool expected)
    {
        Assert.Equal(expected, WebServer.AccessKeysEqual(left, right));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData(null, false)]
    public void ShouldServeClipInline_OnlyAcceptsExplicitFlag(string? value, bool expected)
    {
        Assert.Equal(expected, WebServer.ShouldServeClipInline(value));
    }

    [Fact]
    public void ValidateOrderInfoItems_AcceptsBoundarySizedBatch()
    {
        var items = Enumerable.Range(0, WebServer.MaxOrderInfoItems)
            .Select(index => new OrderInfo
            {
                TrackingNumber = $"TRACK-{index}",
                BuyerMessage = new string('买', 2000),
                SellerMemo = new string('卖', 2000),
                ProductInfo = new string('商', 4000)
            })
            .ToList();

        WebServer.ValidateOrderInfoItems(items);
    }

    [Fact]
    public void ValidateOrderInfoItems_RejectsTooManyOrders()
    {
        var items = Enumerable.Range(0, WebServer.MaxOrderInfoItems + 1)
            .Select(index => new OrderInfo { TrackingNumber = index.ToString() })
            .ToList();

        Assert.Throws<InvalidDataException>(() => WebServer.ValidateOrderInfoItems(items));
    }

    [Fact]
    public void ValidateOrderInfoItems_RejectsOversizedField()
    {
        var items = new List<OrderInfo>
        {
            new() { TrackingNumber = "TRACK-1", BuyerMessage = new string('x', 2001) }
        };

        var error = Assert.Throws<InvalidDataException>(() => WebServer.ValidateOrderInfoItems(items));

        Assert.Contains("买家留言过长", error.Message);
    }

    [Fact]
    public void ClipEditor_UsesSingleScreenSourcePlaybackWorkflow()
    {
        string html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "index.html"));

        Assert.Contains("id=\"clipSourcePlayer\"", html);
        Assert.Contains("id=\"clipPlayhead\"", html);
        Assert.Contains("id=\"clipPlaySelectionBtn\"", html);
        Assert.Contains("生成并下载", html);
        Assert.Contains("resolvePlaybackUrl(v.id)", html);
        Assert.DoesNotContain("id=\"clipResult\"", html);
        Assert.DoesNotContain("/clip/preview", html);
        Assert.DoesNotContain("/clip/frame", html);
        Assert.DoesNotContain("/clip/prewarm", html);
    }

    [Fact]
    public void VideoList_UsesLazyThumbnailsAndSourceBadges()
    {
        string html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "index.html"));

        Assert.Contains("thumb.loading='lazy'", html);
        Assert.Contains("v.thumbnailUrl", html);
        Assert.Contains("status.className='status-badge'", html);
        Assert.Contains("?'其他设备':'PC'", html);
        Assert.Contains("missingBadge.className='missing-badge'", html);
        Assert.Contains("missingBadge.textContent='文件丢失'", html);
        Assert.Contains(".status-badge,.status-badge.external{background:var(--progress-bg);color:var(--muted)}", html);
        Assert.Contains(".mobile-connect-toggle,.install-card{display:none}", html);
    }

    [Fact]
    public void MobileOverview_UsesCompactCardsAndDisablesCompatibilityOnFirstVisit()
    {
        string html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "index.html"));

        Assert.Contains("@media (max-width:560px){.overview{grid-template-columns:repeat(2,minmax(0,1fr))", html);
        Assert.Contains(".overview .summary-card:nth-child(3){grid-column:1/-1}", html);
        Assert.Contains(".overview #oldestNote,.overview #retentionNote,.overview #storageNote{display:none}", html);
        Assert.Contains("localStorage.getItem(compatStorageKey)===null", html);
        Assert.Contains("window.matchMedia('(max-width:900px)').matches", html);
        Assert.Contains("localStorage.setItem(compatStorageKey,'0')", html);
    }
}
