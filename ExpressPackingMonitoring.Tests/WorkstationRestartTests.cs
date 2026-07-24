using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class WorkstationRestartTests
{
    [Fact]
    public void PendingRestartStartsOnlyWhenExitPhaseRuns()
    {
        WorkstationNetwork.CancelPendingRestart();
        int startCount = 0;
        try
        {
            bool scheduled = WorkstationNetwork.TryScheduleRestart(
                Environment.ProcessPath!,
                AppContext.BaseDirectory,
                "test");

            Assert.True(scheduled);
            Assert.True(WorkstationNetwork.IsRestartPending);
            Assert.Equal(0, startCount);

            bool started = WorkstationNetwork.TryStartPendingRestart(info =>
            {
                startCount++;
                Assert.Equal(Environment.ProcessPath, info.FileName);
                Assert.Equal(AppContext.BaseDirectory, info.WorkingDirectory);
                Assert.Equal(
                    ["--wait-for-process-exit", Environment.ProcessId.ToString()],
                    info.ArgumentList);
                return 12345;
            });

            Assert.True(started);
            Assert.Equal(1, startCount);
            Assert.False(WorkstationNetwork.IsRestartPending);
        }
        finally
        {
            WorkstationNetwork.CancelPendingRestart();
        }
    }

    [Fact]
    public void RestartWithoutParentArgumentDoesNotWait()
    {
        Assert.True(WorkstationNetwork.WaitForRestartParentExit([], 0, out string error));
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("--wait-for-process-exit")]
    [InlineData("--wait-for-process-exit", "invalid")]
    [InlineData("--wait-for-process-exit", "0")]
    public void InvalidRestartParentArgumentIsRejected(params string[] arguments)
    {
        Assert.False(WorkstationNetwork.WaitForRestartParentExit(arguments, 0, out string error));
        Assert.Contains("参数无效", error, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedShutdownCanCancelPendingRestart()
    {
        WorkstationNetwork.CancelPendingRestart();
        Assert.True(WorkstationNetwork.TryScheduleRestart(
            Environment.ProcessPath!,
            AppContext.BaseDirectory,
            "test"));

        WorkstationNetwork.CancelPendingRestart();

        Assert.False(WorkstationNetwork.IsRestartPending);
        Assert.False(WorkstationNetwork.TryStartPendingRestart(_ => 12345));
    }
}
