using Microsoft.Extensions.DependencyInjection;
using NotesApp.Client.Theming;

namespace NotesApp.Client;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();

		// Seed the theme color keys into Application.Resources before any page
		// loads, so {DynamicResource Bg} etc. resolve immediately.
		ThemeManager.ApplySaved();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}