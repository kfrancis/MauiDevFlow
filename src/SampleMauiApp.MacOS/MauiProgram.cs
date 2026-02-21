using Microsoft.Extensions.Logging;
using Microsoft.Maui.Platform.MacOS.Hosting;
using Microsoft.Maui.Essentials.MacOS;
using MauiDevFlow.Agent;
using MauiDevFlow.Blazor;

namespace SampleMauiApp;

public static partial class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiAppMacOS<App>()
			.AddMacOSEssentials()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		// Blazor WebView
		builder.Services.AddMauiBlazorWebView();
		builder.AddMacOSBlazorWebView();

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
