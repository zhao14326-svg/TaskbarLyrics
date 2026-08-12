using System.IO;
using System.Reflection;
using Microsoft.Web.WebView2.Core;

namespace TaskbarLyrics.Helpers;

/// <summary>
/// Virtual host serving embedded HTML/CSS/JS/font assets to WebView2 via https://appassets.local/.
/// </summary>
public static class AppAssetServer
{
    private static readonly Dictionary<string, string> MimeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"]  = "text/css; charset=utf-8",
        [".js"]   = "text/javascript; charset=utf-8",
        [".otf"]  = "font/otf",
        [".ttf"]  = "font/ttf",
        [".woff"] = "font/woff",
        [".woff2"]= "font/woff2",
        [".png"]  = "image/png",
        [".svg"]  = "image/svg+xml",
    };

    private static CoreWebView2Environment? _env;

    public static void Register(CoreWebView2 webView)
    {
        _env = webView.Environment;
        webView.AddWebResourceRequestedFilter("https://appassets.local/*", CoreWebView2WebResourceContext.All);
        webView.WebResourceRequested += OnWebResourceRequested;
    }

    private static void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        if (string.IsNullOrEmpty(path)) return;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var prefix = ext is ".otf" or ".ttf" or ".woff" or ".woff2"
            ? "TaskbarLyrics.Assets.Fonts"
            : "TaskbarLyrics.Assets.Web";

        var resourceName = prefix + "." + path;

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null || _env == null) return;

            var mime = MimeMap.TryGetValue(ext, out var m) ? m : "application/octet-stream";
            var headers = "Content-Type: " + mime + "\r\nCache-Control: no-cache\r\n";
            e.Response = _env.CreateWebResourceResponse(stream, 200, "OK", headers);
        }
        catch
        {
            // 404
        }
    }
}
