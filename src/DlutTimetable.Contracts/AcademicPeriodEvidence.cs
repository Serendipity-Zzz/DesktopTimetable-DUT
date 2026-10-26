using System.Text.Json;

namespace DlutTimetable.Contracts;

// Numeric timing evidence only; no labels, identifiers, campus names or personal values.
public sealed record AcademicPeriodEvidence(int Index,int StartTime,int EndTime)
{
    public static AcademicPeriodEvidence[]? ReadCompleteArray(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Array || value.GetArrayLength() is <1 or >24)return null;
        var result=new List<AcademicPeriodEvidence>();
        foreach(var item in value.EnumerateArray())
        {
            if(item.ValueKind!=JsonValueKind.Object)return null;
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in item.EnumerateObject())if(!names.Add(property.Name))return null;
            var upper=item.TryGetProperty("indexNo",out var index);var lower=item.TryGetProperty("indexno",out var lowerIndex);
            if(upper==lower)return null;
            if(!upper)index=lowerIndex;
            if(index.ValueKind!=JsonValueKind.Number || !index.TryGetInt32(out var number) || number is <1 or >24
                || !Clock(item,"startTime",out var start) || !Clock(item,"endTime",out var end) || start>=end)return null;
            result.Add(new(number,start,end));
        }
        var sorted=result.OrderBy(unit=>unit.Index).ToArray();
        if(!sorted.Select(unit=>unit.Index).SequenceEqual(Enumerable.Range(1,sorted.Length))
            || sorted.Zip(sorted.Skip(1)).Any(pair=>pair.First.EndTime>pair.Second.StartTime))return null;
        return sorted;
    }
    private static bool Clock(JsonElement item,string field,out int time)
    {
        time=0;
        return item.TryGetProperty(field,out var value) && value.ValueKind==JsonValueKind.Number
            && value.TryGetInt32(out time) && time is >=0 and <=2359 && time%100<60;
    }
}
