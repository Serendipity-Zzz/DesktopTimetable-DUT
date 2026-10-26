using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DlutTimetable.Contracts;

public static class WorkerWire
{
    private static readonly JsonSerializerOptions Options=new()
    { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=16 };
    public static async Task WriteAsync<T>(Stream stream,T message,CancellationToken ct=default)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(message,Options);
        if(bytes.Length is <1 or >WorkerProtocol.MaxMessageBytes)throw new InvalidDataException("Worker message exceeds the wire limit.");
        var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);
        await stream.WriteAsync(header,ct);await stream.WriteAsync(bytes,ct);await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream,CancellationToken ct=default)
    {
        var header=new byte[4];await stream.ReadExactlyAsync(header,ct);
        var length=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(length is <1 or >WorkerProtocol.MaxMessageBytes)throw new InvalidDataException("Invalid worker frame length.");
        var bytes=new byte[length];await stream.ReadExactlyAsync(bytes,ct);
        try{return JsonSerializer.Deserialize<T>(bytes,Options)??throw new InvalidDataException("Empty worker message.");}
        catch(JsonException ex){throw new InvalidDataException("Invalid worker JSON.",ex);}
    }
    public static void Validate(WorkerRequest request,string nonce,DateTimeOffset now)
    {
        if(request.ProtocolVersion!=WorkerProtocol.Version || request.RequestId==Guid.Empty || request.Generation.Value<0
            || !SameNonce(request.Nonce,nonce) || !Enum.IsDefined(request.TaskKind) || !AuthProfileLocation.ValidScope(request.ProfileScopeId)
            || request.DeadlineUtc<=now || request.DeadlineUtc>now.AddMinutes(15)
            || request.AllowedHosts is null || request.AllowedHosts.Length is <1 or >8
            || request.AllowedHosts.Any(host=>string.IsNullOrWhiteSpace(host) || host.Length>253
                || Uri.CheckHostName(host)!=UriHostNameType.Dns || host!=host.ToLowerInvariant()))
            throw new InvalidDataException("Worker request rejected.");
    }
    public static void Validate(WorkerResponse response,WorkerRequest request,AccountGeneration current)
    {
        Match(response.ProtocolVersion,response.RequestId,response.Generation,response.Nonce,request);
        if(current!=request.Generation || !Enum.IsDefined(response.Status)
            || response.Diagnostics is {} diagnostics && (diagnostics.BrowserProcessId<0 || diagnostics.ProcessIds is null
                || diagnostics.ProcessIds.Length>128 || diagnostics.ProcessIds.Any(id=>id<=0))
            || (response.Status==WorkerStatus.Completed ? string.IsNullOrWhiteSpace(response.DatasetJson) : response.DatasetJson is not null))
            throw new InvalidDataException("Worker result rejected.");
        if(response.Diagnostics?.Observation is {} report)
        {
            if(report.Version!=1 || report.Responses is null || report.Responses.Length>SchemaObservation.MaximumEntries
                || report.PublicScriptPaths is {} scripts && (scripts.Length>PublicSchoolAssets.MaximumScriptPaths || scripts.Any(path=>path is null
                    || PublicSchoolAssets.AcademicScriptPath("http://jxgl.dlut.edu.cn"+path)!=path))
                || report.SkippedBusy<0 || report.Rejected<0 || response.Status!=WorkerStatus.UnsupportedSchema
                || report.Responses.Any(item=>item is null || !SchoolNavigation.Hosts.Contains(item.Host) || item.Route is null || item.Route.Length>2048
                    || item.QueryNames is null || item.QueryNames.Length>24 || item.QueryNames.Any(name=>name is null || name.Length>128)
                    || item.ShapeJson is null || Encoding.UTF8.GetByteCount(item.ShapeJson)>SchemaObservation.MaximumShapeBytes
                    || item.Bytes is <0 or >WorkerProtocol.MaxMessageBytes || item.Status is <100 or >599))
                throw new InvalidDataException("Worker observation rejected.");
        }
        if(response.Diagnostics is {Processes:{} processes} details)
        {
            if(processes.Length>128 || details.ProcessIds.Distinct().Count()!=details.ProcessIds.Length
                || processes.Any(p=>p is null || p.ProcessId<=0 || !Enum.IsDefined(p.Role)
                    || p.FirstSeenMilliseconds<0 || p.LastSeenMilliseconds<p.FirstSeenMilliseconds
                    || p.LastSeenMilliseconds>16*60*1000
                    || p.ExitObservedMilliseconds is {} exited && (exited<p.LastSeenMilliseconds || exited>16*60*1000 || !details.BrowserExited))
                || !processes.Select(p=>p.ProcessId).Order().SequenceEqual(details.ProcessIds.Order())
                || details.BrowserExited && processes.Any(p=>p.ExitObservedMilliseconds is null)
                || details.BrowserProcessId>0 && !processes.Any(p=>p.ProcessId==details.BrowserProcessId && p.Role==BrowserProcessRole.Browser))
                throw new InvalidDataException("Worker process observations rejected.");
        }
    }
    public static void Validate(WorkerControl control,WorkerRequest request)
    {
        Match(control.ProtocolVersion,control.RequestId,control.Generation,control.Nonce,request);
        if(!Enum.IsDefined(control.Kind))throw new InvalidDataException("Worker control rejected.");
    }
    private static void Match(int version,Guid id,AccountGeneration generation,string nonce,WorkerRequest expected)
    {
        if(version!=WorkerProtocol.Version || id!=expected.RequestId || generation!=expected.Generation || !SameNonce(nonce,expected.Nonce))
            throw new InvalidDataException("Worker envelope mismatch.");
    }
    public static bool SameNonce(string? a,string? b)=>a is {Length:64} && b is {Length:64}
        && a.All(Uri.IsHexDigit) && b.All(Uri.IsHexDigit)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a),Encoding.ASCII.GetBytes(b));
}
