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

		builder.Services.AddSingleton<CodexDesk.Services.CompletedChatsCollector>();
		builder.Services.AddSingleton<CodexDesk.Services.PlaywrightCompletedChatsCollectionService>();
		builder.Services.AddSingleton<CodexDesk.Services.CodexInteractiveChatService>();
		builder.Services.AddSingleton<CodexDesk.Services.CodexPromptMacroService>();
		builder.Services.AddSingleton<CodexDesk.Services.NaverTalkBrowserService>();
		builder.Services.AddSingleton<MainPage>();
		builder.Services.AddMauiBlazorWebView();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
