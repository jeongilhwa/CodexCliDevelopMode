using InterceptKit.Core;
using InterceptKit.Presentation;
using InterceptKit.WebView;
using InterceptKit.WebView.Windows;
using Microsoft.Extensions.Logging;

namespace InterceptKit.App;

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

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<IInterceptSessionStore, InterceptSessionStore>();
        builder.Services.AddSingleton<WebViewInterceptRuntimeState>();
        builder.Services.AddSingleton<WebView2InterceptAdapter>();
        builder.Services.AddSingleton<IWebViewInterceptPlatformAdapter>(static services => services.GetRequiredService<WebView2InterceptAdapter>());
        builder.Services.AddSingleton<WebView2InterceptController>();
        builder.Services.AddSingleton<IInterceptDecisionSink>(static services => services.GetRequiredService<WebView2InterceptController>());
        builder.Services.AddSingleton<InterceptWorkspaceService>();
        builder.Services.AddSingleton<MainPage>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
