using System.Text.Json;
using DlutTimetable.Contracts;

namespace DlutTimetable.Tests;

public sealed class AcademicPeriodEvidenceTests
{
    private static object[] Units(int count=12)=>Enumerable.Range(1,count).Select(index=>
    {
        var minute=8*60+(index-1)*50;
        return (object)new{indexNo=index,startTime=minute/60*100+minute%60,endTime=(minute+45)/60*100+(minute+45)%60,
            nameZh="PrivatePeriodLabel",campus="PrivateCampus",id=123456789};
    }).ToArray();
    [Fact]
    public void PreservesCompleteNumericTableWhileOrdinarySamplesStayBounded()
    {
        using var observer=new SchemaObservation();
        var report=observer.Create(new("http://jxgl.dlut.edu.cn/student/for-std/course-table"),"GET",200,
            JsonSerializer.SerializeToUtf8Bytes(new{result=new{units=Units()}}));
        using var shape=JsonDocument.Parse(report.ShapeJson);
        var array=shape.RootElement.GetProperty("result").GetProperty("units");
        Assert.Equal(2,array.GetProperty("samples").GetArrayLength());
        var complete=array.GetProperty("timingUnits");Assert.Equal(12,complete.GetArrayLength());
        Assert.Equal(12,complete[11].GetProperty("indexNo").GetInt32());
        Assert.All(complete.EnumerateArray(),unit=>Assert.Equal(3,unit.EnumerateObject().Count()));
        foreach(var personal in new[]{"PrivatePeriodLabel","PrivateCampus","123456789"})Assert.DoesNotContain(personal,report.ShapeJson);
    }
    [Fact]
    public void SummaryIsAcademicOnlyAndTotalOutputIsLimitedTo24Units()
    {
        var body=JsonSerializer.SerializeToUtf8Bytes(new{first=Units(),second=Units(),third=Units()});
        using var observer=new SchemaObservation();
        var academic=observer.Create(new("http://jxgl.dlut.edu.cn/student/for-std/course-table"),"GET",200,body);
        using var shape=JsonDocument.Parse(academic.ShapeJson);
        var units=shape.RootElement.EnumerateObject().SelectMany(property=>
            property.Value.TryGetProperty("timingUnits",out var table)?table.EnumerateArray().ToArray():[]).ToArray();
        Assert.Equal(24,units.Length);
        var portal=observer.Create(new("https://portal.dlut.edu.cn/api"),"GET",200,body);
        Assert.DoesNotContain("timingUnits",portal.ShapeJson);
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"indexNo\":0,\"startTime\":800,\"endTime\":845}]")]
    [InlineData("[{\"indexNo\":2,\"startTime\":800,\"endTime\":845}]")]
    [InlineData("[{\"indexNo\":1,\"startTime\":800,\"endTime\":865}]")]
    [InlineData("[{\"indexNo\":1,\"startTime\":845,\"endTime\":800}]")]
    [InlineData("[{\"indexNo\":1,\"indexno\":1,\"startTime\":800,\"endTime\":845}]")]
    [InlineData("[{\"indexNo\":1,\"indexNo\":2,\"startTime\":800,\"endTime\":845}]")]
    [InlineData("[{\"indexNo\":1,\"startTime\":800,\"endTime\":900},{\"indexNo\":2,\"startTime\":850,\"endTime\":935}]")]
    [InlineData("[{\"indexNo\":1,\"startTime\":800,\"endTime\":845},{\"indexNo\":1,\"startTime\":850,\"endTime\":935}]")]
    public void AmbiguousOrInvalidTablesDoNotProduceCompleteEvidence(string json)
    {
        using var doc=JsonDocument.Parse(json);
        Assert.Null(AcademicPeriodEvidence.ReadCompleteArray(doc.RootElement));
    }
    [Fact]
    public void SortsByIndexAndSupportsThePublishedLowercaseIndexField()
    {
        using var doc=JsonDocument.Parse("""[{"indexno":2,"startTime":850,"endTime":935},{"indexno":1,"startTime":800,"endTime":845}]""");
        var units=AcademicPeriodEvidence.ReadCompleteArray(doc.RootElement)!;
        Assert.Equal([1,2],units.Select(unit=>unit.Index));
    }
    [Fact]
    public void OversizedArraysCannotBypassTheTimingEvidenceLimit()
    {
        using var doc=JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(Units(25)));
        Assert.Null(AcademicPeriodEvidence.ReadCompleteArray(doc.RootElement));
    }
}
