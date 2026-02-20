using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Platform.Maui.Linux.Gtk4.Hosting;
using MauiDevFlow.Agent.Gtk;
using MauiDevFlow.Blazor.Gtk;

namespace SampleMauiApp;

public static partial class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiAppLinuxGtk4<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		// Blazor WebView — register services then override handler for GTK
		builder.Services.AddMauiBlazorWebView();
		builder.ConfigureMauiHandlers(handlers =>
		{
			handlers.AddHandler<IBlazorWebView, Platform.Maui.Linux.Gtk4.BlazorWebView.BlazorWebViewHandler>();
		});

		// Shared data
		builder.Services.AddSingleton<TodoService>();

		// Pages (DI-resolved by Shell's DataTemplate)
		builder.Services.AddTransient<MainPage>();
		builder.Services.AddTransient<BlazorTodoPage>();

#if DEBUG
		builder.Logging.AddDebug();
		builder.AddMauiDevFlowAgent(options => { options.Port = 9223; });
		builder.AddMauiBlazorDevFlowTools();
#endif

		return builder.Build();
	}
}
