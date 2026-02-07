#if ANDROID
using Android.Webkit;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Handlers;
using AWebView = Android.Webkit.WebView;

namespace MauiDevFlow.Blazor;

/// <summary>
/// Android implementation of the Blazor WebView debug service.
/// Uses Android.Webkit.WebView.EvaluateJavascript with a callback wrapper.
/// </summary>
public class BlazorWebViewDebugService : BlazorWebViewDebugServiceBase
{
    private AWebView? _webView;

    public BlazorWebViewDebugService() { }

    protected override bool HasWebView => _webView != null;

    protected override Task<string?> EvaluateJavaScriptAsync(string script)
    {
        var tcs = new TaskCompletionSource<string?>();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (_webView == null)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                _webView.EvaluateJavascript(script, new JsValueCallback(value =>
                {
                    // Android returns "null" string for null/undefined JS results
                    if (value == "null" || value == null)
                        tcs.TrySetResult(null);
                    else
                    {
                        // Android returns JS values as JSON-encoded strings.
                        // For string results: "\"hello\"" -> need to JSON-deserialize to get "hello"
                        // For non-string results: "42", "[1,2]" -> just strip outer quotes if present
                        if (value.StartsWith("\"") && value.EndsWith("\""))
                        {
                            try
                            {
                                value = System.Text.Json.JsonSerializer.Deserialize<string>(value) ?? value;
                            }
                            catch
                            {
                                value = value[1..^1];
                            }
                        }
                        tcs.TrySetResult(value);
                    }
                }));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    protected override void ReloadWebView()
        => MainThread.BeginInvokeOnMainThread(() => _webView?.Reload());

    protected override void NavigateWebView(string url)
        => MainThread.BeginInvokeOnMainThread(() => _webView?.LoadUrl(url));

    public override void ConfigureHandler()
    {
        Log("[BlazorDevFlow] ConfigureHandler called (Android)");

        BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping("ChobitsuDebug", async (handler, view) =>
        {
            Log("[BlazorDevFlow] ChobitsuDebug mapper callback triggered (Android)");

            if (handler.PlatformView is AWebView androidWebView)
            {
                _webView = androidWebView;
                _webView.Settings.JavaScriptEnabled = true;
                Log("[BlazorDevFlow] Android WebView captured successfully");
                await OnWebViewCapturedAsync();
            }
            else
            {
                Log($"[BlazorDevFlow] PlatformView is not Android WebView: {handler.PlatformView?.GetType().Name ?? "null"}");
            }
        });
    }
}

/// <summary>
/// Wraps Android's IValueCallback for async JavaScript evaluation.
/// </summary>
internal class JsValueCallback : Java.Lang.Object, IValueCallback
{
    private readonly Action<string?> _callback;

    public JsValueCallback(Action<string?> callback)
    {
        _callback = callback;
    }

    public void OnReceiveValue(Java.Lang.Object? value)
    {
        _callback(value?.ToString());
    }
}
#endif
