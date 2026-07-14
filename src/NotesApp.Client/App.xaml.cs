using NotesApp.Client.Theming;
using NotesApp.Client.ViewModels;

namespace NotesApp.Client;

public partial class App : Application
{
	private readonly NotesViewModel _viewModel;

	// MAUI resolves App from the service container, so the shared ViewModel can
	// be constructor-injected here just like anywhere else.
	public App(NotesViewModel viewModel)
	{
		InitializeComponent();
		_viewModel = viewModel;

		// Seed the theme color keys into Application.Resources before any page
		// loads, so {DynamicResource Bg} etc. resolve immediately.
		ThemeManager.ApplySaved();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new AppShell());

		// Auto-save runs ~0.8s behind typing, so edits can be pending when the
		// user leaves. Losing focus (Cmd+Tab, click away) does a full async
		// flush; app close does a synchronous one - awaits don't reliably
		// complete during teardown.
		window.Deactivated += (_, _) => _ = _viewModel.FlushPendingEditsAsync();
		window.Destroying += (_, _) => _viewModel.FlushPendingEditsSync();

		return window;
	}
}
