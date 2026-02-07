#if IOS || MACCATALYST
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Handlers;
using WebKit;

namespace MauiDevFlow.Blazor;

/// <summary>
/// iOS/Mac Catalyst implementation of the Blazor WebView debug service.
/// Uses WKWebView for JavaScript evaluation.
/// </summary>
public class BlazorWebViewDebugService : BlazorWebViewDebugServiceBase
{
    private WKWebView? _webView;

    public BlazorWebViewDebugService() { }

    protected override bool HasWebView => _webView != null;

    protected override async Task<string?> EvaluateJavaScriptAsync(string script)
    {
        if (_webView == null) return null;
        var result = await _webView.EvaluateJavaScriptAsync(script);
        return result?.ToString();
    }

    protected override void ReloadWebView() => _webView!.Reload();

    protected override void NavigateWebView(string url)
    {
        var request = new Foundation.NSUrlRequest(new Foundation.NSUrl(url));
        _webView!.LoadRequest(request);
    }

    public override void ConfigureHandler()
    {
        Log("[BlazorDevFlow] ConfigureHandler called, appending to BlazorWebViewMapper");

        BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping("ChobitsuDebug", async (handler, view) =>
        {
            Log("[BlazorDevFlow] ChobitsuDebug mapper callback triggered");

            if (handler.PlatformView is WKWebView wkWebView)
            {
                _webView = wkWebView;
                Log("[BlazorDevFlow] WKWebView captured successfully");
                await OnWebViewCapturedAsync();
            }
            else
            {
                Log($"[BlazorDevFlow] PlatformView is not WKWebView: {handler.PlatformView?.GetType().Name ?? "null"}");
            }
        });
    }
}
#endif
