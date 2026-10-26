using System.Collections.Immutable;
using System.Text;
using DlutTimetable.Domain;
using DlutTimetable.Infrastructure;

namespace DlutTimetable.Tests;

public sealed class CsesExportTests
{
    [Fact]
    public void SparseWeeksRequireAnExplicitSnapshotInsteadOfFabricatedRecurrence()
    {
        var source=ValidatedDataset.Create(Samples.Dataset);
        Assert.Throws<InvalidDataException>(()=>CsesExport.Create(source));
        var text=Encoding.UTF8.GetString(CsesExport.Create(source,week:1));
        Assert.Contains("version: 1",text);Assert.Contains("weeks: all",text);
        Assert.Contains("start_time: \"08:00:00\"",text);Assert.Contains("end_time: \"09:45:00\"",text);
    }
    [Fact]
    public void WholeTermKeepsOddEvenVariantsAndProducesIndependentParserFixtures()
    {
        var data=Samples.Dataset;var meeting=data.Meetings[0];var all=Enumerable.Range(1,data.Term.WeekCount).ToImmutableArray();
        var source=ValidatedDataset.Create(data with {Meetings=[
            meeting with{Name="课程: \"同名\" 😀",TeacherNames=["教师甲\n助教乙"],Location="楼: 室 101",Weeks=all.Where(w=>w%2==1).ToImmutableArray()},
            meeting with{Id="m2",CourseId="other-class",Name="课程: \"同名\" 😀",TeacherNames=["教师丙"],Location="楼: 室 102",Weeks=all.Where(w=>w%2==0).ToImmutableArray()}
        ]});
        var bytes=CsesExport.Create(source);var text=new UTF8Encoding(false,true).GetString(bytes);
        Assert.Contains("weeks: odd",text);Assert.Contains("weeks: even",text);Assert.Contains("\\n助教乙",text);
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"DlutTimetable.slnx")))root=root.Parent;
        var directory=Path.Combine(root!.FullName,"artifacts/exchange-stage");Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory,"cses-odd-even.yaml"),bytes);
        File.WriteAllBytes(Path.Combine(directory,"cses-week.yaml"),CsesExport.Create(ValidatedDataset.Create(data),week:1));
        File.WriteAllBytes(Path.Combine(directory,"sparse-weeks.ics"),ICalendarExport.Create(ValidatedDataset.Create(data),exportedAt:new(2026,10,25,0,0,0,TimeSpan.Zero)));
    }
    [Fact]
    public void UncoveredAndPartialDaysCannotBePresentedAsCompleteWeekTemplates()
    {
        var data=Samples.Dataset with{Coverage=new("WEEK",[1]),Meetings=[Samples.Dataset.Meetings[0] with{Weeks=[1]}]};
        Assert.Throws<InvalidDataException>(()=>CsesExport.Create(ValidatedDataset.Create(data),week:2));
        Assert.Throws<InvalidDataException>(()=>CsesExport.Create(ValidatedDataset.Create(data)));
        data=data with{Coverage=new("DAY",[1],new(2026,9,1))};
        Assert.Throws<InvalidDataException>(()=>CsesExport.Create(ValidatedDataset.Create(data),week:1));
    }
}
