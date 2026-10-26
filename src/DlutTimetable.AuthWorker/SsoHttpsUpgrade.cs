using DlutTimetable.Contracts;
using DlutTimetable.Infrastructure.Academic;
using Microsoft.Web.WebView2.Core;

namespace DlutTimetable.AuthWorker;

internal static class SsoHttpsUpgrade
{
    public static void Attach(CoreWebView2 core,IReadOnlyCollection<string> allowedHosts,SchoolAccessPolicy? access=null)
    {
        core.AddWebResourceRequestedFilter("http://sso.dlut.edu.cn/*",CoreWebView2WebResourceContext.All);
        if(access?.WebVpnGranted==true)core.AddWebResourceRequestedFilter("https://webvpn.dlut.edu.cn/*",CoreWebView2WebResourceContext.All);
        core.WebResourceRequested+=(_,e)=>
        {
            // Filters belong to CoreWebView2, not to this event subscriber. Other
            // features can add their own filters; never intercept their requests.
            if(!Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out var address))return;
            var insecure=access?.IsInsecureSso(e.Request.Uri) ?? (address.Scheme=="http" && address.IdnHost=="sso.dlut.edu.cn");
            if(!insecure)return;
            var target=access is null?SchoolNavigation.SecureSsoTarget(e.Request.Uri,allowedHosts):access.SecureSsoTarget(e.Request.Uri);
            // A local 307 lets Chromium preserve POST and its body without reading credentials
            // into managed code. The HTTP request never reaches the school server.
            e.Response=target is null
                ? core.Environment.CreateWebResourceResponse(null,403,"Forbidden","")
                : core.Environment.CreateWebResourceResponse(null,307,"Temporary Redirect","Location: "+target+"\r\nCache-Control: no-store");
        };
    }
}
