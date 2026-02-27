using Microsoft.Extensions.Logging;
using MauiDevFlow.Agent;
using MauiDevFlow.Blazor;

namespace SampleMauiApp;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		// Blazor WebView
		builder.Services.AddMauiBlazorWebView();

		// Shared data
		builder.Services.AddSingleton<TodoService>();

		// HTTP client factory (for network monitoring demo)
		builder.Services.AddHttpClient();

		// Pages (DI-resolved by Shell's DataTemplate)
		builder.Services.AddTransient<MainPage>();
		builder.Services.AddTransient<BlazorTodoPage>();
		builder.Services.AddTransient<NetworkTestPage>();

#if DEBUG
		//builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
		builder.AddMauiDevFlowAgent(options => { options.Port = 9223; });
		builder.AddMauiBlazorDevFlowTools();
#endif

		return builder.Build();
	}
}
