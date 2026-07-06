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
		await _viewModel.LoadNotesCommand.ExecuteAsync(null);
	}
}
