using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;
using MauiDevFlow.Agent.Logging;

namespace MauiDevFlow.Agent;

/// <summary>
/// Extension methods for registering MauiDevFlow Agent in the MAUI DI container.
/// </summary>
public static class AgentServiceExtensions
{
    /// <summary>
    /// Adds the MauiDevFlow Agent to the MAUI app builder.
    /// The agent will start automatically when the app starts.
    /// </summary>
    public static MauiAppBuilder AddMauiDevFlowAgent(this MauiAppBuilder builder, Action<AgentOptions>? configure = null)
    {
        var options = new AgentOptions();
        configure?.Invoke(options);

        // If port wasn't explicitly set in code, check AssemblyMetadata (from -p:MauiDevFlowPort=XXXX)
        if (options.Port == AgentOptions.DefaultPort)
        {
            var metaPort = ReadAssemblyMetadataPort();
            if (metaPort.HasValue)
                options.Port = metaPort.Value;
        }

        var service = new DevFlowAgentService(options);
        builder.Services.AddSingleton(service);

        if (options.EnableFileLogging)
        {
            var logDir = Path.Combine(FileSystem.CacheDirectory, "mauidevflow-logs");
            var logProvider = new FileLogProvider(logDir, options.MaxLogFileSize, options.MaxLogFiles);
            service.SetLogProvider(logProvider);
            builder.Logging.AddProvider(logProvider);
        }

        builder.ConfigureLifecycleEvents(lifecycle =>
        {
#if ANDROID
            lifecycle.AddAndroid(android =>
            {
                android.OnResume(activity =>
                {
                    var app = Application.Current;
                    if (app != null)
                        service.Start(app, app.Dispatcher);
                });
            });
#elif IOS || MACCATALYST
            lifecycle.AddiOS(ios =>
            {
                ios.FinishedLaunching((_, _) =>
                {
                    // Retry until Application.Current is available
                    Task.Run(async () =>
                    {
                        for (int i = 0; i < 30; i++)
                        {
                            await Task.Delay(500);
                            var app = Application.Current;
                            if (app != null)
                            {
                                app.Dispatcher.Dispatch(() => service.Start(app, app.Dispatcher));
                                Console.WriteLine($"[MauiDevFlow] Agent started on port {options.Port}");
                                return;
                            }
                        }
                        Console.WriteLine("[MauiDevFlow] Failed to start agent: Application.Current was null after 30 retries");
                    });
                    return true;
                });
            });
#elif WINDOWS
            lifecycle.AddWindows(windows =>
            {
                var started = false;
                windows.OnActivated((window, args) =>
                {
                    if (started) return;
                    var app = Application.Current;
                    if (app != null)
                    {
                        started = true;
                        app.Dispatcher.Dispatch(() => service.Start(app, app.Dispatcher));
                        Console.WriteLine($"[MauiDevFlow] Agent started on port {options.Port}");
                    }
                });
            });
#endif
        });

        return builder;
    }

    /// <summary>
    /// Reads MauiDevFlowPort from AssemblyMetadataAttribute injected by the .targets file
    /// when the app is built with -p:MauiDevFlowPort=XXXX.
    /// </summary>
    private static int? ReadAssemblyMetadataPort()
    {
        try
        {
            var attrs = System.Reflection.Assembly.GetEntryAssembly()?
                .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false);

            if (attrs != null)
            {
                foreach (System.Reflection.AssemblyMetadataAttribute attr in attrs)
                {
                    if (attr.Key == "MauiDevFlowPort" && int.TryParse(attr.Value, out var port))
                        return port;
                }
            }
        }
        catch { /* ignore reflection failures */ }
        return null;
    }
}
