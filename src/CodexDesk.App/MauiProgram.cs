using InterceptKit.Core;
using InterceptKit.Presentation;
using InterceptKit.WebView;
using InterceptKit.WebView.Windows;
using Microsoft.Extensions.Logging;

namespace CodexDesk;

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

		builder.Services.AddSingleton<CodexDesk.Services.CodexInteractiveChatService>();
		builder.Services.AddSingleton<CodexDesk.Services.CodexPromptMacroService>();
		builder.Services.AddSingleton<CodexDesk.Services.BrowserStateService>();
		builder.Services.AddSingleton<IInterceptSessionStore, InterceptSessionStore>();
		builder.Services.AddSingleton<WebViewInterceptRuntimeState>();
		builder.Services.AddSingleton<WebView2InterceptAdapter>();
		builder.Services.AddSingleton<IWebViewInterceptPlatformAdapter>(static services => services.GetRequiredService<WebView2InterceptAdapter>());
		builder.Services.AddSingleton<WebView2InterceptController>();
		builder.Services.AddSingleton<IInterceptDecisionSink>(static services => services.GetRequiredService<WebView2InterceptController>());
		builder.Services.AddSingleton<InterceptWorkspaceService>();
		builder.Services.AddSingleton<WebViewInterceptPanelState>();
		builder.Services.AddSingleton<MainPage>();
		builder.Services.AddMauiBlazorWebView();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
