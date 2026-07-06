using NotesApp.Client.ViewModels;

namespace NotesApp.Client;

public partial class MainPage : ContentPage
{
	private readonly NotesViewModel _viewModel;

	public MainPage(NotesViewModel viewModel)
	{
		InitializeComponent();
		_viewModel = viewModel;
		BindingContext = viewModel;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		// Show local data immediately (offline-first), then sync with the
		// server in the background without blocking the UI on network access.
		await _viewModel.LoadNotesCommand.ExecuteAsync(null);
		_ = _viewModel.SyncCommand.ExecuteAsync(null);
	}
}
