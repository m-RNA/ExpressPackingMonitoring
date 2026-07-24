using ExpressPackingMonitoring.Themes;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ThemeManagerTests
{
    [Theory]
    [InlineData("Auto", AppTheme.Auto)]
    [InlineData("Light", AppTheme.Light)]
    [InlineData("Dark", AppTheme.Dark)]
    [InlineData("invalid", AppTheme.Auto)]
    [InlineData(null, AppTheme.Auto)]
    public void ResolveConfiguredThemeUsesSavedThemeOrAutoFallback(string? configured, AppTheme expected)
    {
        Assert.Equal(expected, ThemeManager.ResolveConfiguredTheme(configured!));
    }
}
