using System.IO;
using System.Runtime.InteropServices;
using DlutTimetable.Contracts;
using Microsoft.Web.WebView2.Core;

namespace DlutTimetable.AuthWorker;

internal sealed class ResponseObserver : IAsyncDisposable
{
    private readonly CoreWebView2 core;
    private readonly Func<Uri,Uri?> canonicalize;
    private readonly SchemaObservation redactor=new();
    private readonly CancellationTokenSource lifetime=new();
    private readonly List<ResponseObservation> observations=[];
    private string[] publicScripts=[];
    private readonly Queue<(Task<Stream> Content,Uri Transport,Uri Uri,string Method,int Status,string Media)> queue=[];
    private Task pending=Task.CompletedTask;
    private bool armed,disposed,pumping;
    private int skipped,rejected;
    public int JsonCount=>observations.Count(item=>item.Outcome=="Json");
    public int PendingCount=>queue.Count+(pumping?1:0);
    public event Action<int>? Changed;
    public event Action<Uri,byte[]>? SchoolResponse;
    public event Action<SchoolResponseProblem>? SchoolProblem;
    public ResponseObserver(CoreWebView2 core,Func<Uri,Uri?>? canonicalize=null)
    {this.core=core;this.canonicalize=canonicalize??(uri=>SchoolNavigation.Allowed(uri.AbsoluteUri,SchoolNavigation.Hosts)?uri:null);core.WebResourceResponseReceived+=Received;}
    public void Arm()=>armed=true;
    public void Reset()
    {
        if(PendingCount!=0)throw new InvalidOperationException("Observation still processing.");
        armed=false;observations.Clear();skipped=0;rejected=0;publicScripts=[];redactor.ResetPageEvidence();
    }
    public ObservationReport? Report()=>observations.Count==0?null:new(1,DateTimeOffset.UtcNow,observations.ToArray(),skipped,rejected,publicScripts);
    public async Task ObservePublicScriptPathsAsync()
    {
        // Read script URLs only: no DOM text, forms, storage or page JavaScript variables.
        var encoded=await core.ExecuteScriptAsync($"Array.from(document.scripts,s=>s.src).filter(s=>s && s.length<=2048).slice(0,{PublicSchoolAssets.MaximumScriptCandidates})");
        var urls=System.Text.Json.JsonSerializer.Deserialize<string[]>(encoded)??[];
        publicScripts=PublicSchoolAssets.AcademicScriptPaths(urls.Select(value=>Uri.TryCreate(value,UriKind.Absolute,out var uri)?canonicalize(uri)?.AbsoluteUri:null).Where(value=>value is not null).Select(value=>value!));
    }
    private void Received(object? sender,CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if(!armed || disposed)return;
        var request=args.Request;
        if(!Uri.TryCreate(request.Uri,UriKind.Absolute,out var transport) || canonicalize(transport) is not {} uri
            || uri.IdnHost=="sso.dlut.edu.cn" || request.Method is not ("GET" or "POST"))return;
        var dataEndpoint=AcademicEndpoints.IsData(uri);
        if(dataEndpoint && args.Response.StatusCode>=400)
        {
            var code=args.Response.StatusCode;
            var retry=args.Response.Headers.Contains("Retry-After")?args.Response.Headers.GetHeader("Retry-After"):null;
            SchoolProblem?.Invoke(new(code==429?SchoolResponseFailure.RateLimited:code is 401 or 403?SchoolResponseFailure.NeedsLogin:SchoolResponseFailure.Unavailable,
                code==429 || code==503 && retry is not null?SchoolRetryAfter.Parse(retry,DateTimeOffset.UtcNow):null));
            Add(redactor.Metadata(uri,request.Method,code,"unknown","HttpFailure"));return;
        }
        var media=args.Response.Headers.Contains("Content-Type")?args.Response.Headers.GetHeader("Content-Type").Split(';')[0].Trim().ToLowerInvariant():"";
        var extension=Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if(new[]{".css",".js",".png",".jpg",".jpeg",".svg",".woff",".woff2",".ico",".gif"}.Contains(extension))return;
        if(media!="application/json" && media!="text/json" && !media.EndsWith("+json",StringComparison.Ordinal)
            && media is not ("text/plain" or "text/html" or "application/javascript" or "text/javascript" or ""))return;
        if(args.Response.StatusCode is >=300 and <400)
        {if(dataEndpoint)SchoolProblem?.Invoke(new(SchoolResponseFailure.NeedsLogin));Add(redactor.Metadata(uri,request.Method,args.Response.StatusCode,media.Length==0?"unknown":media,"Redirect"));return;}
        if(queue.Count>=32){skipped++;if(dataEndpoint)SchoolProblem?.Invoke(new(SchoolResponseFailure.Unreadable));return;}
        Task<Stream> content;
        try {content=args.Response.GetContentAsync();}
        catch(Exception ex) when(ex is COMException or InvalidOperationException or ArgumentException){content=Task.FromException<Stream>(ex);}
        queue.Enqueue((content,transport,uri,request.Method,args.Response.StatusCode,media.Length==0?"unknown":media));
        Changed?.Invoke(observations.Count);
        if(!pumping){pumping=true;pending=PumpAsync();}
    }
    private void Add(ResponseObservation observation)
    {
        if(observations.Any(item=>item.Route==observation.Route && item.Method==observation.Method && item.ShapeJson==observation.ShapeJson && item.Outcome==observation.Outcome))return;
        if(observations.Count>=SchemaObservation.MaximumEntries)
        {
            if(observation.Outcome!="Json"){skipped++;return;}
            var replace=observations.FindIndex(item=>item.Outcome!="Json");if(replace<0){skipped++;return;}
            observations.RemoveAt(replace);
        }
        observations.Add(observation);Changed?.Invoke(observations.Count);
    }
    private async Task PumpAsync()
    {
        try
        {
        while(!disposed && queue.TryDequeue(out var item))
        {
            try
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);deadline.CancelAfter(TimeSpan.FromSeconds(4));
                var content=item.Content;Stream stream;
                try {stream=await content.WaitAsync(deadline.Token);}
                catch(OperationCanceledException){_=DisposeLateAsync(content);throw;}
                using(stream)
                {
                    if(disposed)return;
                    if(canonicalize(item.Transport)?.AbsoluteUri!=item.Uri.AbsoluteUri)continue;
                    if(stream is null){if(AcademicEndpoints.IsData(item.Uri))SchoolProblem?.Invoke(new(SchoolResponseFailure.Unreadable));Add(redactor.Metadata(item.Uri,item.Method,item.Status,item.Media,"EmptyContent"));continue;}
                    var bytes=await Task.Run(async ()=>
                    {
                        using var output=new MemoryStream();var buffer=new byte[32768];
                        while(true)
                        {
                            var count=await stream.ReadAsync(buffer,deadline.Token);if(count==0)break;
                            if(output.Length+count>WorkerProtocol.MaxMessageBytes)throw new InvalidDataException("Observation too large.");
                            await output.WriteAsync(buffer.AsMemory(0,count),deadline.Token);
                        }
                        return output.ToArray();
                    },deadline.Token);
                    if(disposed)return;
                    if(canonicalize(item.Transport)?.AbsoluteUri!=item.Uri.AbsoluteUri)continue;
                    if(item.Status==200)SchoolResponse?.Invoke(item.Uri,bytes);
                    try {Add(redactor.Create(item.Uri,item.Method,item.Status,bytes) with {MediaType=item.Media});}
                    catch(System.Text.Json.JsonException)
                    {
                        Add(item.Media=="text/html" && redactor.ObserveAcademicPage(item.Uri,item.Method,item.Status,bytes) is {} page
                            ?page:redactor.Metadata(item.Uri,item.Method,item.Status,item.Media,"NotJson"));
                    }
                }
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or OperationCanceledException or COMException or InvalidOperationException or ArgumentException)
            {
                if(!disposed && canonicalize(item.Transport)?.AbsoluteUri!=item.Uri.AbsoluteUri)continue;
                rejected++;
                if(!disposed && AcademicEndpoints.IsData(item.Uri))SchoolProblem?.Invoke(new(ex switch
                {
                    OperationCanceledException=>SchoolResponseFailure.Timeout,
                    InvalidDataException=>SchoolResponseFailure.TooLarge,
                    _=>SchoolResponseFailure.Unreadable
                }));
                if(!disposed)Add(redactor.Metadata(item.Uri,item.Method,item.Status,item.Media,ex switch
                {
                    OperationCanceledException=>"Timeout",
                    COMException=>"ContentUnavailable_"+unchecked((uint)ex.HResult).ToString("X8"),
                    InvalidDataException=>"SizeOrShapeLimit",
                    IOException=>"ReadFailure",
                    InvalidOperationException=>"ContentState",
                    ArgumentException=>"InvalidArgument",
                    _=>"Rejected"
                }));
            }
        }
        }
        finally {pumping=false;if(!disposed)Changed?.Invoke(observations.Count);}
    }
    private static async Task DisposeLateAsync(Task<Stream> content)
    {try {using var stream=await content;}catch(Exception ex) when(ex is COMException or InvalidOperationException or IOException or ArgumentException){} }
    public async ValueTask DisposeAsync()
    {
        if(disposed)return;disposed=true;armed=false;core.WebResourceResponseReceived-=Received;
        while(queue.TryDequeue(out var queued))_=DisposeLateAsync(queued.Content);
        await lifetime.CancelAsync();
        try {await pending.WaitAsync(TimeSpan.FromSeconds(1));}catch(TimeoutException){}
        redactor.Dispose();lifetime.Dispose();
    }
}
