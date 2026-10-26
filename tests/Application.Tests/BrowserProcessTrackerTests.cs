using DlutTimetable.Application;
using DlutTimetable.Contracts;

namespace DlutTimetable.Tests;

public sealed class BrowserProcessTrackerTests
{
    private sealed class Clock:TimeProvider
    {
        public long Milliseconds;
        public override long GetTimestamp()=>Milliseconds;
        public override long TimestampFrequency=>1000;
    }
    [Fact]
    public void ObservationsPreserveFirstSeenAndRequireIndependentExitConfirmation()
    {
        var clock=new Clock();var tracker=new BrowserProcessTracker(clock);
        tracker.Observe(10,BrowserProcessRole.Browser);clock.Milliseconds=40;
        tracker.Observe(10,BrowserProcessRole.Browser);tracker.Observe(20,BrowserProcessRole.Renderer);
        Assert.Null(tracker.Snapshot()[0].ExitObservedMilliseconds);
        Assert.Equal(0,tracker.Snapshot()[0].FirstSeenMilliseconds);
        Assert.Equal(40,tracker.Snapshot()[0].LastSeenMilliseconds);
        clock.Milliseconds=90;tracker.ConfirmExited([10]);
        Assert.Equal(90,tracker.Snapshot()[0].ExitObservedMilliseconds);
        Assert.Null(tracker.Snapshot()[1].ExitObservedMilliseconds);
        // A process discovered during the wait was not part of the confirmed set.
        tracker.Observe(30,BrowserProcessRole.Gpu);
        Assert.Null(tracker.Snapshot()[2].ExitObservedMilliseconds);
        clock.Milliseconds=100;tracker.Observe(10,BrowserProcessRole.Browser);
        Assert.Null(tracker.Snapshot()[0].ExitObservedMilliseconds);
        tracker.ConfirmExited(tracker.ProcessIds);
        Assert.All(tracker.Snapshot(),p=>Assert.Equal(100,p.ExitObservedMilliseconds));
    }
    [Fact]
    public void TrackingIsBoundedAndOverflowCannotSilentlyClaimCompleteCoverage()
    {
        var tracker=new BrowserProcessTracker(new Clock());
        for(var i=1;i<=128;i++)tracker.Observe(i,BrowserProcessRole.Renderer);
        Assert.False(tracker.Truncated);
        tracker.Observe(129,BrowserProcessRole.Utility);
        Assert.True(tracker.Truncated);Assert.Equal(128,tracker.Snapshot().Length);
        Assert.DoesNotContain(129,tracker.ProcessIds);
        Assert.Throws<ArgumentOutOfRangeException>(()=>tracker.Observe(0,BrowserProcessRole.Browser));
        Assert.Throws<ArgumentOutOfRangeException>(()=>tracker.Observe(10,(BrowserProcessRole)99));
    }
    [Fact]
    public async Task WireRoundtripKeepsRolesAndRejectsUnboundOrImpossibleLifetimes()
    {
        var request=new WorkerRequest(WorkerProtocol.Version,Guid.NewGuid(),new(1),new string('A',64),WorkerTaskKind.InteractiveLogin,SchoolNavigation.Hosts.ToArray(),DateTimeOffset.UtcNow.AddMinutes(1));
        var observed=new BrowserProcessObservation(12,BrowserProcessRole.Browser,5,20,30);
        var diagnostics=new WorkerDiagnostics(12,[12],true,Processes:[observed]);
        WorkerResponse Response(WorkerDiagnostics value)=>new(WorkerProtocol.Version,request.RequestId,request.Generation,request.Nonce,WorkerStatus.Cancelled,null,value);
        using var stream=new MemoryStream();await WorkerWire.WriteAsync(stream,Response(diagnostics));stream.Position=0;
        var read=await WorkerWire.ReadAsync<WorkerResponse>(stream);WorkerWire.Validate(read,request,request.Generation);
        Assert.Equal(observed,Assert.Single(read.Diagnostics!.Processes!));
        foreach(var invalid in new[]{diagnostics with{Processes=[observed with{ProcessId=13}]},
            diagnostics with{Processes=[observed,observed]},diagnostics with{Processes=[null!]},
            diagnostics with{Processes=[observed with{Role=(BrowserProcessRole)99}]},
            diagnostics with{Processes=[observed with{FirstSeenMilliseconds=-1}]},
            diagnostics with{Processes=[observed with{LastSeenMilliseconds=4}]},
            diagnostics with{Processes=[observed with{ExitObservedMilliseconds=19}]},
            diagnostics with{Processes=[observed with{ExitObservedMilliseconds=16*60*1000+1}]},
            diagnostics with{Processes=[observed with{ExitObservedMilliseconds=null}]},diagnostics with{BrowserExited=false}})
            Assert.Throws<InvalidDataException>(()=>WorkerWire.Validate(Response(invalid),request,request.Generation));
        Assert.Throws<InvalidDataException>(()=>WorkerWire.Validate(read with{ProtocolVersion=3},request,request.Generation));
    }
}
