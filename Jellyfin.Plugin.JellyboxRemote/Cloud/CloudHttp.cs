using System.Net.Http.Headers;

namespace Jellyfin.Plugin.JellyboxRemote.Cloud;

internal static class CloudHttp
{
    public static HttpClient Create()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("JellyboxRemote", Plugin.Release));
        return http;
    }
}
