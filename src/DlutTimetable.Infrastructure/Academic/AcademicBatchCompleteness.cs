using System.Text.Json;

namespace DlutTimetable.Infrastructure.Academic;

internal static class AcademicBatchCompleteness
{
    private static InvalidDataException Incomplete()=>new("学校只返回了部分课表，未覆盖已有课表。请打开完整学期课表后重新读取。");
    public static void CheckEnvelope(JsonElement value,bool pagination=false)
    {
        if(value.ValueKind!=JsonValueKind.Object)return;
        int? total=null,capacity=null;
        foreach(var property in value.EnumerateObject())
        {
            switch(property.Name)
            {
                case "success" when property.Value.ValueKind==JsonValueKind.False:throw Incomplete();
                case "hasMore":case "hasNext":case "hasNextPage":
                    if(property.Value.ValueKind!=JsonValueKind.False)throw Incomplete();
                    break;
                case "nextPage":case "nextPageUrl":case "nextCursor":
                    if(property.Value.ValueKind!=JsonValueKind.Null && property.Value.ValueKind!=JsonValueKind.False
                        && !(property.Value.ValueKind==JsonValueKind.String && string.IsNullOrEmpty(property.Value.GetString())))throw Incomplete();
                    break;
                case "totalPages":case "pageCount":
                    if(!Integer(property.Value,out var pages) || pages!=1)throw Incomplete();
                    break;
                case "pageNumber":case "currentPage":case "pageNo":
                    if(!Integer(property.Value,out var page) || page is <0 or >1)throw Incomplete();
                    break;
                case "offset":case "pageIndex":
                    if(!Integer(property.Value,out var offset) || offset!=0)throw Incomplete();
                    break;
                case "total":case "totalElements":case "totalCount":
                    if(pagination){if(!Integer(property.Value,out var count) || count<0)throw Incomplete();total=count;}
                    break;
                case "limit":case "size":case "pageSize":
                    if(pagination){if(!Integer(property.Value,out var size) || size<1)throw Incomplete();capacity=size;}
                    break;
                case "cursor":case "endCursor":case "startCursor":
                    if(pagination && property.Value.ValueKind!=JsonValueKind.Null
                        && !(property.Value.ValueKind==JsonValueKind.String && string.IsNullOrEmpty(property.Value.GetString())))throw Incomplete();
                    break;
                case "result":case "data":case "meta":
                    CheckEnvelope(property.Value);break;
                case "pagination":case "paging":case "pageInfo":
                    CheckEnvelope(property.Value,true);break;
            }
        }
        if(total.HasValue && capacity.HasValue && total>capacity)throw Incomplete();
    }
    public static void CheckLessonScope(JsonElement configuration,AcademicScheduleBatch batch)
    {
        var scopes=new List<HashSet<string>>();
        foreach(var property in configuration.EnumerateObject())
        {
            if(property.Value.ValueKind!=JsonValueKind.Array)continue;
            if(property.Name=="lessonIds")
                scopes.Add(property.Value.EnumerateArray().Select(Id).ToHashSet(StringComparer.Ordinal));
            else if(property.Value.GetArrayLength()>0 && property.Value[0].ValueKind==JsonValueKind.Object
                && property.Value[0].TryGetProperty("teacherAssignmentList",out _) && property.Value[0].TryGetProperty("course",out _))
                scopes.Add(property.Value.EnumerateArray().Select(item=>item.ValueKind==JsonValueKind.Object && item.TryGetProperty("id",out var id)?Id(id):throw Incomplete()).ToHashSet(StringComparer.Ordinal));
        }
        var actual=batch.Lessons.Select(lesson=>lesson.Id).ToHashSet(StringComparer.Ordinal);
        if(scopes.Count==0 || scopes.Any(scope=>!scope.SetEquals(actual)))throw Incomplete();
    }
    public static void CheckScheduleCount(JsonElement root,int actual)
    {
        if(root.ValueKind!=JsonValueKind.Object)return;
        foreach(var property in root.EnumerateObject())
        {
            if(property.Name is "totalSchedules" or "scheduleCount")
            {if(!Integer(property.Value,out var total) || total!=actual)throw Incomplete();}
            else if(property.Name is "result" or "data")CheckScheduleCount(property.Value,actual);
        }
    }
    private static string Id(JsonElement value)=>value.ValueKind==JsonValueKind.Number && value.TryGetInt64(out var id) && id>0
        ?id.ToString(System.Globalization.CultureInfo.InvariantCulture):throw Incomplete();
    private static bool Integer(JsonElement value,out int number)
    {number=0;return value.ValueKind==JsonValueKind.Number && value.TryGetInt32(out number);}
}
