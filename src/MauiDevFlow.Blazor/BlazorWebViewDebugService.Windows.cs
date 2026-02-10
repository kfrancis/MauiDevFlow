#if WINDOWS
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Handlers;
using Microsoft.Web.WebView2.Core;
using WinUIWebView = Microsoft.UI.Xaml.Controls.WebView2;

namespace MauiDevFlow.Blazor;

/// <summary>
/// Windows implementation of the Blazor WebView debug service.
/// Uses WebView2's CoreWebView2.ExecuteScriptAsync for JavaScript evaluation.
/// All WebView2 API calls are marshalled to the UI thread.
/// </summary>
public class BlazorWebViewDebugService : BlazorWebViewDebugServiceBase
{
    private CoreWebView2? _coreWebView;
    private bool _hooked;

    public BlazorWebViewDebugService() { }

    protected override bool HasWebView => _coreWebView != null;

    protected override async Task<string?> EvaluateJavaScriptAsync(string script)
    {
        if (_coreWebView == null) return null;

        // WebView2 APIs are UI-thread-affinitized
        var result = await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            return await _coreWebView.ExecuteScriptAsync(script);
        });

        return DecodeWebView2Result(result);
    }

    /// <summary>
    /// Decodes the JSON-encoded result from WebView2's ExecuteScriptAsync.
    /// ExecuteScriptAsync always returns a JSON value: strings are quoted,
    /// numbers/booleans are bare, null is "null".
    /// </summary>
    private static string? DecodeWebView2Result(string? result)
    {
        if (string.IsNullOrEmpty(result) || result == "null")
            return null;

        try
        {
            using var doc = JsonDocument.Parse(result);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => doc.RootElement.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => doc.RootElement.GetRawText()
            };
        }
        catch
        {
            return result;
        }
    }

    protected override void ReloadWebView()
        => MainThread.BeginInvokeOnMainThread(() => _coreWebView?.Reload());

    protected override void NavigateWebView(string url)
        => MainThread.BeginInvokeOnMainThread(() => _coreWebView?.Navigate(url));

    public override void ConfigureHandler()
    {
        Log("[BlazorDevFlow] ConfigureHandler called (Windows)");

        BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping("ChobitsuDebug", async (handler, view) =>
        {
            Log("[BlazorDevFlow] ChobitsuDebug mapper callback triggered (Windows)");

            // Guard against duplicate mapper callbacks
            if (_coreWebView != null) return;

            if (handler.PlatformView is WinUIWebView webView2)
            {
                if (webView2.CoreWebView2 != null)
                {
                    _coreWebView = webView2.CoreWebView2;
                    Log("[BlazorDevFlow] WebView2 CoreWebView2 captured successfully");
                    await OnWebViewCapturedAsync();
                }
                else if (!_hooked)
                {
                    _hooked = true;
                    webView2.CoreWebView2Initialized += async (s, e) =>
                    {
                        if (_coreWebView != null) return;

                        if (e.Exception != null)
                        {
                            Log($"[BlazorDevFlow] CoreWebView2 initialization failed: {e.Exception.Message}");
                            return;
                        }

                        _coreWebView = webView2.CoreWebView2;
                        Log("[BlazorDevFlow] WebView2 CoreWebView2 captured via Initialized event");
                        await OnWebViewCapturedAsync();
                    };
                }
            }
            else
            {
                Log($"[BlazorDevFlow] PlatformView is not WebView2: {handler.PlatformView?.GetType().Name ?? "null"}");
            }
        });
    }
}
#endif
