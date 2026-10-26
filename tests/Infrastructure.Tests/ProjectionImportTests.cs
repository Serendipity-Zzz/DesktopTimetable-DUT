using System.Text.Json;
using System.Text.Json.Nodes;
using DlutTimetable.Domain;
using DlutTimetable.Infrastructure;

namespace DlutTimetable.Tests;

public sealed class ProjectionImportTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"DlutProjectionTests-"+Guid.NewGuid().ToString("N"));
    public ProjectionImportTests()=>Directory.CreateDirectory(directory);
    public void Dispose()
    {
        var target=new DirectoryInfo(directory);
        if(target.Parent?.FullName!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !target.Name.StartsWith("DlutProjectionTests-",StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        target.Delete(true);
    }
    [Theory]
    [InlineData("name")]
    [InlineData("weeks")]
    [InlineData("teacherNames")]
    public void MissingRequiredMeetingFieldsAreNotTreatedAsAnEmptySchedule(string field)
    {
        var json=JsonNode.Parse(DatasetJson.Serialize(ValidatedDataset.Create(Samples.Dataset)))!;
        Assert.True(json["meetings"]![0]!.AsObject().Remove(field));
        Assert.ThrowsAny<JsonException>(()=>DatasetJson.Parse(JsonSerializer.SerializeToUtf8Bytes(json)));
    }
    [Fact]
    public async Task ExplicitDatedBatchReplacesOldWeekRulesInsteadOfMergingThem()
    {
        using var store=new SqliteStore(Path.Combine(directory,"current.db"));
        await store.ReplaceAsync(ValidatedDataset.Create(Samples.Dataset));
        var actual=new DateOnly(2026,9,6);
        var dated=Samples.Dataset with{Coverage=new("DAY",[1],actual),
            Meetings=[Samples.Dataset.Meetings[0] with{Id="source-actual-date",Weekday=7,Weeks=[1]}]};
        await store.ReplaceAsync(ValidatedDataset.Create(dated));
        var stored=(await store.ReadAsync())!;var week=TimetableProjector.Week(stored,1);
        Assert.Equal(actual,Assert.Single(week.Occurrences).Date);Assert.Equal(CoverageStatus.PartialDay,week.Coverage);
        Assert.Equal(CoverageStatus.NotFetched,TimetableProjector.Week(stored,3).Coverage);
        Assert.Equal(TodayScheduleStatus.NotFetched,TimetableProjector.Today(stored,new(2026,9,1,8,0,0,TimeSpan.FromHours(8))).Status);
    }
}
