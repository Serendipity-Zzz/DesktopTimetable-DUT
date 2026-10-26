using DlutTimetable.Application;
using DlutTimetable.Contracts;

namespace DlutTimetable.Tests;

public sealed class ManualRefreshGateTests
{
    [Theory]
    [InlineData(CommandOrigin.Settings)]
    [InlineData(CommandOrigin.Tray)]
    [InlineData(CommandOrigin.Toolbar)]
    public async Task UserEntryWorksWithoutAnExistingAccountAndCoalescesDoubleClicks(CommandOrigin origin)
    {
        var gate=new ManualRefreshGate();var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var opened=0;
        var first=gate.RunAsync(origin,async _=>{opened++;await finish.Task;});
        Assert.True(gate.Busy);
        Assert.False(await gate.RunAsync(origin,_=>{opened++;return Task.CompletedTask;}));Assert.Equal(1,opened);
        finish.SetResult();Assert.True(await first);Assert.False(gate.Busy);
        Assert.True(await gate.RunAsync(origin,_=>{opened++;return Task.CompletedTask;}));Assert.Equal(2,opened);
    }
    [Theory]
    [InlineData(CommandOrigin.System)]
    [InlineData((CommandOrigin)999)]
    public async Task SystemAndUnknownOriginsNeverOpenABrowser(CommandOrigin origin)
    {
        var opened=0;var gate=new ManualRefreshGate();
        Assert.False(await gate.RunAsync(origin,_=>{opened++;return Task.CompletedTask;}));
        Assert.Equal(0,opened);Assert.False(gate.Busy);
    }
    [Fact]
    public async Task CancellationAndFailureReleaseAdmissionForExplicitRetry()
    {
        var gate=new ManualRefreshGate();using var cancel=new CancellationTokenSource();cancel.Cancel();var opened=0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>gate.RunAsync(CommandOrigin.Settings,_=>{opened++;return Task.CompletedTask;},cancel.Token));
        Assert.Equal(0,opened);Assert.False(gate.Busy);
        await Assert.ThrowsAsync<IOException>(()=>gate.RunAsync(CommandOrigin.Settings,_=>throw new IOException("synthetic")));
        Assert.False(gate.Busy);
        Assert.True(await gate.RunAsync(CommandOrigin.Settings,_=>Task.CompletedTask));
    }
}
