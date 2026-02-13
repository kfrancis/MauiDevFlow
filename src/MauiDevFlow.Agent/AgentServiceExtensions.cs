using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;
using MauiDevFlow.Agent.Core;
using MauiDevFlow.Logging;

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

        // Read project identity from assembly metadata (injected by .targets)
        var project = ReadAssemblyMetadataProject() ?? "unknown";
        var tfm = ReadAssemblyMetadataTfm() ?? "unknown";

        // Try broker for port assignment first (must run on thread pool to avoid deadlock
        // with SynchronizationContext — AddMauiDevFlowAgent runs on the main thread)
        BrokerRegistration? brokerReg = null;
        if (options.Port == AgentOptions.DefaultPort)
        {
            try
            {
                string platform;
                string appName;
                try
                {
                    platform = DeviceInfo.Platform.ToString();
                    appName = AppInfo.Name ?? "unknown";
                }
                catch
                {
                    // MAUI not fully initialized yet during DI registration
                    platform = OperatingSystem.IsAndroid() ? "Android"
                        : OperatingSystem.IsIOS() ? "iOS"
                        : OperatingSystem.IsMacCatalyst() ? "MacCatalyst"
                        : OperatingSystem.IsWindows() ? "Windows"
                        : "Unknown";
                    appName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "unknown";
                }
                brokerReg = new BrokerRegistration(project, tfm, platform, appName);
                // Task.Run avoids deadlock: TryRegisterAsync uses await internally,
                // and the main thread has a SynchronizationContext that would deadlock
                // if we called .GetAwaiter().GetResult() directly.
                var assignedPort = Task.Run(() => brokerReg.TryRegisterAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
                if (assignedPort.HasValue)
                {
                    options.Port = assignedPort.Value;
                    Console.WriteLine($"[MauiDevFlow] Broker assigned port {assignedPort.Value}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MauiDevFlow] Broker registration failed: {ex.Message}");
                brokerReg?.Dispose();
                brokerReg = null;
            }
        }

        // Fall back to assembly metadata port if broker didn't assign one
        if (brokerReg?.AssignedPort == null)
        {
            var metaPort = ReadAssemblyMetadataPort();
            if (metaPort.HasValue)
                options.Port = metaPort.Value;
        }

        var service = new PlatformAgentService(options);
        if (brokerReg != null)
            service.SetBrokerRegistration(brokerReg);
        builder.Services.AddSingleton<DevFlowAgentService>(service);

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
    /// Reads MauiDevFlow metadata from AssemblyMetadataAttributes injected by the .targets file.
    /// </summary>
    private static string? ReadAssemblyMetadata(string key)
    {
        try
        {
            // Try entry assembly first (works on Mac Catalyst, Windows)
            var entry = System.Reflection.Assembly.GetEntryAssembly();
            if (entry != null)
            {
                var value = FindMetadataInAssembly(entry, key);
                if (value != null) return value;
            }

            // GetEntryAssembly() returns null on Android/iOS — scan loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                var value = FindMetadataInAssembly(asm, key);
                if (value != null) return value;
            }
        }
        catch { /* ignore reflection failures */ }
        return null;
    }

    private static int? ReadAssemblyMetadataPort()
    {
        var value = ReadAssemblyMetadata("MauiDevFlowPort");
        return value != null && int.TryParse(value, out var port) ? port : null;
    }

    internal static string? ReadAssemblyMetadataProject() => ReadAssemblyMetadata("MauiDevFlowProject");
    internal static string? ReadAssemblyMetadataTfm() => ReadAssemblyMetadata("MauiDevFlowTfm");

    private static string? FindMetadataInAssembly(System.Reflection.Assembly assembly, string key)
    {
        try
        {
            var attrs = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false);
            foreach (System.Reflection.AssemblyMetadataAttribute attr in attrs)
            {
                if (attr.Key == key)
                    return attr.Value;
            }
        }
        catch { /* ignore per-assembly reflection failures */ }
        return null;
    }
}
