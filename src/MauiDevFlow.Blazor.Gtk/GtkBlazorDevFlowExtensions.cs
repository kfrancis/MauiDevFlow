using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;

namespace MauiDevFlow.Blazor.Gtk;

/// <summary>
/// Extension methods for registering MauiDevFlow Blazor debug tools in Maui.Gtk apps.
/// </summary>
public static class GtkBlazorDevFlowExtensions
{
    /// <summary>
    /// Adds MauiDevFlow Blazor WebView debugging tools for WebKitGTK.
    /// Enables Chrome DevTools Protocol (CDP) access to BlazorWebView content on Linux.
    /// </summary>
    public static MauiAppBuilder AddMauiBlazorDevFlowTools(this MauiAppBuilder builder, bool enableLogging = false)
    {
        var service = new GtkBlazorWebViewDebugService();
        if (enableLogging)
            service.LogCallback = msg => System.Diagnostics.Debug.WriteLine(msg);

        builder.Services.AddSingleton(service);
        return builder;
    }

    /// <summary>
    /// Wires the Blazor CDP service to the Agent's /api/cdp endpoint via reflection.
    /// Call after both Agent and Blazor services are initialized.
    /// </summary>
    public static void WireBlazorCdpToAgent(this GtkBlazorWebViewDebugService blazorService)
    {
        Task.Run(async () =>
        {
            await Task.Delay(1000);
            try
            {
                var app = Microsoft.Maui.Controls.Application.Current;
                if (app == null) return;

                var services = app.Handler?.MauiContext?.Services;
                if (services == null) return;

                // Find DevFlowAgentService by scanning loaded assemblies
                Type? agentType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    agentType = asm.GetType("MauiDevFlow.Agent.Core.DevFlowAgentService");
                    if (agentType != null) break;
                }

                if (agentType == null)
                {
                    System.Diagnostics.Debug.WriteLine("[MauiDevFlow.Blazor.Gtk] Agent service type not found");
                    return;
                }

                var agentService = services.GetService(agentType);
                if (agentService == null)
                {
                    System.Diagnostics.Debug.WriteLine("[MauiDevFlow.Blazor.Gtk] Agent service not registered");
                    return;
                }

                // Wire CdpCommandHandler and CdpReadyCheck
                var handlerProp = agentType.GetProperty("CdpCommandHandler");
                var readyProp = agentType.GetProperty("CdpReadyCheck");

                if (handlerProp != null)
                    handlerProp.SetValue(agentService, new Func<string, Task<string>>(blazorService.SendCdpCommandAsync));

                if (readyProp != null)
                    readyProp.SetValue(agentService, new Func<bool>(() => blazorService.IsReady));

                // Wire WebViewLogCallback
                var writeLogMethod = agentType.GetMethod("WriteWebViewLog");
                if (writeLogMethod != null)
                {
                    blazorService.WebViewLogCallback = (level, message, exception) =>
                    {
                        try { writeLogMethod.Invoke(agentService, new object?[] { level, "WebView.Console", message, exception }); }
                        catch { }
                    };
                }

                System.Diagnostics.Debug.WriteLine("[MauiDevFlow.Blazor.Gtk] CDP wired to Agent");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MauiDevFlow.Blazor.Gtk] Wire failed: {ex.Message}");
            }
        });
    }
}
