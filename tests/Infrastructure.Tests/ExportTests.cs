using System.Text;
using System.Text.Json;
using DlutTimetable.Domain;
using DlutTimetable.Infrastructure;

namespace DlutTimetable.Tests;

public sealed class ExportTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"DlutExportTests-"+Guid.NewGuid().ToString("N"));
    public ExportTests()=>Directory.CreateDirectory(root);
    public void Dispose()
    {
        var target=new DirectoryInfo(root);
        if(target.Parent?.FullName!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !target.Name.StartsWith("DlutExportTests-",StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        target.Delete(true);
    }
    [Fact]
    public async Task InterruptedExportAndProtectedDestinationsPreserveExistingBytes()
    {
        var path=Path.Combine(root,"export.json");await File.WriteAllTextAsync(path,"previous-export");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>AtomicExport.WriteAsync(path,"new-export"u8.ToArray(),[],cancelled.Token));
        Assert.Equal("previous-export",await File.ReadAllTextAsync(path));Assert.Empty(Directory.GetFiles(root,"*.tmp"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>AtomicExport.WriteAsync(path,"bad-overwrite"u8.ToArray(),[root]));
        Assert.Equal("previous-export",await File.ReadAllTextAsync(path));
        if(OperatingSystem.IsWindows())
        {
            using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                await Assert.ThrowsAsync<IOException>(()=>AtomicExport.WriteAsync(path,"cannot-replace-locked-file"u8.ToArray(),[]));
            Assert.Equal("previous-export",await File.ReadAllTextAsync(path));Assert.Empty(Directory.GetFiles(root,"*.tmp"));
        }
        await AtomicExport.WriteAsync(path,"complete-export"u8.ToArray(),[]);
        Assert.Equal("complete-export",await File.ReadAllTextAsync(path));Assert.Empty(Directory.GetFiles(root,"*.tmp"));
    }
    [Fact]
    public void DiagnosticsSelectsOnlyStructuralFieldsFromPersonalDataset()
    {
        var value=Samples.Dataset with {AccountId="PrivateAccountSecret",Source="PrivateSourceSecret",AdapterVersion="PrivateAdapterSecret",Notes="PrivateNoteSecret",
            Term=Samples.Dataset.Term with {Id="PrivateTermSecret",SchoolYear="PrivateYearSecret"},
            Meetings=[Samples.Dataset.Meetings[0] with {Name="PrivateCourseSecret",Location="PrivateLocationSecret",TeacherNames=["PrivateTeacherSecret"]}]};
        var bytes=DiagnosticExport.Create(ValidatedDataset.Create(value),new(TimetableState.Locked,1,false,true,false,80,12,false,false,null,false));
        var text=Encoding.UTF8.GetString(bytes);Assert.DoesNotContain("Private",text,StringComparison.Ordinal);
        using var document=JsonDocument.Parse(bytes);var data=document.RootElement.GetProperty("data");
        Assert.Equal(1,data.GetProperty("meetingCount").GetInt32());Assert.Equal("Term",data.GetProperty("coverage").GetString());
        Assert.Equal("Locked",document.RootElement.GetProperty("ui").GetProperty("state").GetString());
    }
}
