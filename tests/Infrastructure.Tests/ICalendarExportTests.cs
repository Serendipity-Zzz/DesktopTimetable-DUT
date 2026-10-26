using System.Collections.Immutable;
using System.Text;
using DlutTimetable.Domain;
using DlutTimetable.Infrastructure;

namespace DlutTimetable.Tests;

public sealed class ICalendarExportTests
{
    private static readonly DateTimeOffset Stamp=new(2026,10,25,0,0,0,TimeSpan.Zero);
    private static string Text(CurrentDataset data,CalendarAdjustment? calendar=null)=>Encoding.UTF8.GetString(ICalendarExport.Create(ValidatedDataset.Create(data),calendar,Stamp)).Replace("\r\n ","");
    private static string[] Uids(string text)=>text.Split("\r\n").Where(x=>x.StartsWith("UID:",StringComparison.Ordinal)).ToArray();
    [Fact]
    public void ExplicitDatesPreserveSparseWeeksAndSchoolTime()
    {
        var text=Text(Samples.Dataset);
        Assert.Equal(4,text.Split("BEGIN:VEVENT").Length-1);
        Assert.Contains("DTSTART:20260901T000000Z\r\n",text);
        Assert.Contains("DTEND:20260901T014500Z\r\n",text);
        Assert.Contains("DTSTART:20260915T000000Z\r\n",text);
        Assert.Contains("DTSTART:20260929T000000Z\r\n",text);
        Assert.Contains("DTSTART:20261020T000000Z\r\n",text);
        Assert.DoesNotContain("20260908T",text);Assert.DoesNotContain("RRULE",text);
        Assert.DoesNotContain("VALARM",text);
    }
    [Fact]
    public void StableIdentitySurvivesReexportDetailsAndLocalCalendarChanges()
    {
        var source=Samples.Dataset;var original=Uids(Text(source));
        Assert.Equal(original,Uids(Text(source)));
        var changed=source with {Meetings=[source.Meetings[0] with{Id="regenerated-academic-room-hash",Name="改名",Location="改教室"}]};
        Assert.Equal(original,Uids(Text(changed)));
        var calendar=new CalendarAdjustment(source.AccountId,source.Term.Id,source.Term.FirstTeachingMonday.AddDays(7),source.Term.WeekCount,source.Periods);
        Assert.Equal(original,Uids(Text(source,calendar)));
        Assert.NotEqual(original,Uids(Text(source with{AccountId="different-fictional-account"})));
        Assert.Equal(original.Length,original.Distinct().Count());
        Assert.DoesNotContain(source.AccountId,string.Join("",original));
    }
    [Fact]
    public void Utf8LinesFoldWithoutSplittingCharactersAndTextCannotInjectEvents()
    {
        var source=Samples.Dataset with{Meetings=[Samples.Dataset.Meetings[0] with{
            Name=new string('课',70)+"😀,;\\\r\nBEGIN:VEVENT",Location="楼,室;\\\n地点"}]};
        var bytes=ICalendarExport.Create(ValidatedDataset.Create(source),exportedAt:Stamp);
        var physical=new UTF8Encoding(false,true).GetString(bytes);
        Assert.All(physical.Split("\r\n"),line=>Assert.InRange(Encoding.UTF8.GetByteCount(line),0,75));
        Assert.Contains("\r\n ",physical);
        var unfolded=physical.Replace("\r\n ","");
        Assert.Equal(4,unfolded.Split("\r\nBEGIN:VEVENT\r\n").Length-1);
        Assert.Contains("😀\\,\\;\\\\\\nBEGIN:VEVENT",unfolded);
        Assert.Contains("LOCATION:楼\\,室\\;\\\\\\n地点",unfolded);
        Assert.EndsWith("END:VCALENDAR\r\n",physical);
    }
    [Fact]
    public void DayCoverageDoesNotFabricateAnEntireTermAndCancelledExportStops()
    {
        var source=Samples.Dataset with {Coverage=new("DAY",[1],new(2026,9,1)),Meetings=[Samples.Dataset.Meetings[0] with{Weeks=[1]}]};
        var validated=ValidatedDataset.Create(source);
        Assert.Single(Uids(Text(source)));
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(()=>ICalendarExport.Create(validated,cancellationToken:cancellation.Token));
        var empty=ValidatedDataset.Create(Samples.Dataset with{Meetings=ImmutableArray<Meeting>.Empty,VerifiedEmpty=true});
        Assert.Throws<InvalidDataException>(()=>ICalendarExport.Create(empty));
    }
}
