using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Hosting;
using MauiDevFlow.Agent.Core;
using MauiDevFlow.Logging;

namespace MauiDevFlow.Agent.Gtk;

/// <summary>
/// Extension methods for registering MauiDevFlow Agent in Maui.Gtk apps.
/// </summary>
public static class GtkAgentServiceExtensions
{
    /// <summary>
    /// Adds the MauiDevFlow Agent to a Maui.Gtk app builder.
    /// The agent will start automatically when the GTK application activates.
    /// </summary>
    public static MauiAppBuilder AddMauiDevFlowAgent(this MauiAppBuilder builder, Action<AgentOptions>? configure = null)
    {
        var options = new AgentOptions();
        configure?.Invoke(options);

        // Check AssemblyMetadata for port override
        if (options.Port == AgentOptions.DefaultPort)
        {
            var metaPort = ReadAssemblyMetadataPort();
            if (metaPort.HasValue)
                options.Port = metaPort.Value;
        }

        var service = new GtkAgentService(options);
        builder.Services.AddSingleton<DevFlowAgentService>(service);

        if (options.EnableFileLogging)
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "mauidevflow-logs");
            var logProvider = new FileLogProvider(logDir, options.MaxLogFileSize, options.MaxLogFiles);
            service.SetLogProvider(logProvider);
            builder.Logging.AddProvider(logProvider);
        }

        return builder;
    }

    /// <summary>
    /// Starts the MauiDevFlow agent. Call this after the MAUI Application is available.
    /// Typically called from GtkMauiApplication.OnActivate or after window creation.
    /// </summary>
    public static void StartDevFlowAgent(this Application app)
    {
        // Resolve the service from the app's handler service provider
        var service = GetAgentService(app);
        if (service != null)
        {
            service.Start(app, app.Dispatcher);
        }
    }

    private static DevFlowAgentService? GetAgentService(Application app)
    {
        try
        {
            return app.Handler?.MauiContext?.Services.GetService<DevFlowAgentService>();
        }
        catch
        {
            return null;
        }
    }

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
        catch { }
        return null;
    }
}
