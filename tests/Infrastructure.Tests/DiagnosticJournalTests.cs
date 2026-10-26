using System.Text;
using System.Text.Json;
using DlutTimetable.Domain;
using DlutTimetable.Infrastructure;

namespace DlutTimetable.Tests;

public sealed class DiagnosticJournalTests : IDisposable
{
    [Fact]
    public async Task StandaloneFaultWriterCorrelatesOnlyTypedErrorAndToleratesUnwritableStorage()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>DiagnosticJournal.WriteFaultAsync(root,(DiagnosticCode)999));
        var id=await DiagnosticJournal.WriteFaultAsync(root,DiagnosticCode.InstallationFailed);
        var text=await File.ReadAllTextAsync(Path.Combine(root,"runtime-diagnostics.jsonl"));
        Assert.Contains(id.ToString(),text);Assert.Contains("InstallationFailed",text);
        Assert.DoesNotContain(root,text);
        var blocked=Path.Combine(root,"blocked");await File.WriteAllTextAsync(blocked,"PrivatePathSecret");
        var unavailable=await DiagnosticJournal.WriteFaultAsync(blocked,DiagnosticCode.UninstallationFailed);
        Assert.NotEqual(Guid.Empty,unavailable);Assert.Equal("PrivatePathSecret",await File.ReadAllTextAsync(blocked));
    }
    private readonly string root=Path.Combine(Path.GetTempPath(),"DlutJournalTests-"+Guid.NewGuid().ToString("N"));
    public DiagnosticJournalTests()=>Directory.CreateDirectory(root);
    public void Dispose()
    {
        var target=new DirectoryInfo(root);
        if(target.Parent?.FullName!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !target.Name.StartsWith("DlutJournalTests-",StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test directory.");
        target.Delete(true);
    }
    [Fact]
    public async Task ConcurrentSamplesRemainOrderedAndBoundedAndSnapshotsCannotMutateTheRing()
    {
        await using var journal=new DiagnosticJournal(root);
        Parallel.For(0,5000,_=>journal.Record(DiagnosticCode.MaterialSample,bytes:4096,elapsedMilliseconds:1.5,persist:false));
        var snapshot=journal.Snapshot();Assert.Equal(4096,snapshot.Entries.Length);Assert.Equal(904,snapshot.Evicted);
        Assert.Equal(905,snapshot.Entries[0].Sequence);Assert.Equal(5000,snapshot.Entries[^1].Sequence);
        Assert.Equal(snapshot.Entries.Select(e=>e.Sequence).Order(),snapshot.Entries.Select(e=>e.Sequence));
        snapshot.Entries[0]=snapshot.Entries[^1];Assert.Equal(905,journal.Snapshot().Entries[0].Sequence);
        Assert.Empty(Directory.GetFiles(root)); // Samples are not streamed to disk.
    }
    [Fact]
    public async Task CorrelationAndRotationKeepThreeBoundedParseableFilesAndTheLatestEvent()
    {
        var journal=new DiagnosticJournal(root,512);
        var id=journal.Record(DiagnosticCode.SchoolRefreshStarted);
        journal.Record(DiagnosticCode.SchoolRefreshSucceeded,id,count:25);
        for(var i=0;i<30;i++)journal.Record(DiagnosticCode.SceneChanged,state:TimetableState.Locked);
        await journal.DisposeAsync();
        var snapshot=journal.Snapshot();Assert.Equal(id,snapshot.Entries[1].CorrelationId);
        Assert.Equal(0,snapshot.LogWriteFailures);Assert.Equal(0,snapshot.LogQueueDropped);
        var files=Directory.GetFiles(root);Assert.Equal(3,files.Length);
        foreach(var path in files)
        {
            Assert.InRange(new FileInfo(path).Length,1,512);
            foreach(var line in File.ReadAllLines(path))using(JsonDocument.Parse(line)){}
        }
        using var latest=JsonDocument.Parse(File.ReadAllLines(Path.Combine(root,"runtime-diagnostics.jsonl"))[^1]);
        Assert.Equal(32,latest.RootElement.GetProperty("Sequence").GetInt64());
        journal.Record(DiagnosticCode.SessionExiting);Assert.Equal(32,journal.Snapshot().Entries.Length);
        await journal.DisposeAsync();
    }
    [Fact]
    public async Task UnwritableJournalPreservesInMemoryEvidenceAndDoesNotRetryForever()
    {
        var blocked=Path.Combine(root,"blocked");await File.WriteAllTextAsync(blocked,"original");
        var journal=new DiagnosticJournal(blocked);journal.Record(DiagnosticCode.DatabaseReadFailed);
        await journal.DisposeAsync();
        Assert.Single(journal.Snapshot().Entries);Assert.Equal(1,journal.Snapshot().LogWriteFailures);
        Assert.Equal("original",await File.ReadAllTextAsync(blocked));
    }
    [Fact]
    public async Task ExportUsesOnlyTypedEvidenceAndRejectsInvalidOrUnboundedTelemetry()
    {
        await using var journal=new DiagnosticJournal(root);
        var id=journal.Record(DiagnosticCode.DataOperationFailed,persist:false);
        var ui=new DiagnosticUiState(TimetableState.Locked,1,false,true,false,80,12,false,false,null,false);
        var value=Samples.Dataset with {AccountId="CookieTokenStudentSecret",Notes="PrivateCourseSecret"};
        var text=Encoding.UTF8.GetString(DiagnosticExport.Create(ValidatedDataset.Create(value),ui,journal.Snapshot()));
        Assert.DoesNotContain("Secret",text);Assert.DoesNotContain(root,text);Assert.Contains(id.ToString(),text);
        Assert.Contains("DataOperationFailed",text);
        Assert.Throws<ArgumentOutOfRangeException>(()=>journal.Record((DiagnosticCode)999));
        Assert.Throws<ArgumentOutOfRangeException>(()=>journal.Record(DiagnosticCode.MaterialSample,elapsedMilliseconds:double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(()=>journal.Record(DiagnosticCode.MaterialSample,bytes:-1));
        Assert.Single(journal.Snapshot().Entries);
        Assert.Throws<ArgumentException>(()=>DiagnosticExport.Create(null,ui,new(new DiagnosticEntry[4097],0,0,0)));
        Assert.DoesNotContain(typeof(DiagnosticEntry).GetProperties(),p=>p.PropertyType==typeof(string));
    }
}
