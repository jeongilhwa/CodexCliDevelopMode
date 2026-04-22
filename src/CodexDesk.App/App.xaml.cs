namespace CodexDesk;

public partial class App : Application
{
	private readonly MainPage _mainPage;

	public App(MainPage mainPage)
	{
		InitializeComponent();
		_mainPage = mainPage;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(_mainPage)
		{
			Title = string.Empty,
			Width = 1720,
			Height = 980,
			MinimumWidth = 1280,
			MinimumHeight = 760
		};
	}
}
