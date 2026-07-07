using System.ComponentModel;
using System.Text.Json;
using NotesApp.Client.Ai;
using NotesApp.Client.Theming;
using NotesApp.Client.ViewModels;

namespace NotesApp.Client;

public partial class MainPage : ContentPage
{
	private readonly NotesViewModel _viewModel;
	private readonly AiService _aiService;

	// The WebView loads asynchronously; until it signals "ready" we stash the
	// content to load so nothing is lost if a note is opened before then.
	private bool _editorReady;
	private string _pendingHtml = string.Empty;

	public MainPage(NotesViewModel viewModel, AiService aiService)
	{
		InitializeComponent();
		_viewModel = viewModel;
		_aiService = aiService;
		BindingContext = viewModel;

		_viewModel.EditorContentRequested += OnEditorContentRequested;
		_viewModel.PropertyChanged += OnViewModelPropertyChanged;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		// Show local data immediately (offline-first), then sync in the background.
		await _viewModel.LoadDataCommand.ExecuteAsync(null);
		_ = _viewModel.SyncCommand.ExecuteAsync(null);
	}

	// Repaint the WebView editor when the app theme changes.
	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(NotesViewModel.SelectedTheme) && _editorReady)
		{
			_ = PushThemeAsync();
		}
	}

	// The ViewModel asks us to (re)load the editor's content.
	private void OnEditorContentRequested(string html)
	{
		if (_editorReady)
		{
			_ = LoadContentAsync(html);
		}
		else
		{
			_pendingHtml = html;
		}
	}

	private Task LoadContentAsync(string html)
	{
		var json = JsonSerializer.Serialize(html);
		return EditorWebView.EvaluateJavaScriptAsync($"window.loadContent({json})");
	}

	private Task PushThemeAsync()
	{
		var t = ThemeManager.CurrentTheme;
		var payload = JsonSerializer.Serialize(new
		{
			bg = t.Bg,
			text = t.TextPrimary,
			muted = t.TextMuted,
			accent = t.Accent,
			divider = t.Divider
		});
		return EditorWebView.EvaluateJavaScriptAsync($"window.setTheme({payload})");
	}

	// Messages coming from the editor's JavaScript.
	private async void OnEditorMessage(object? sender, HybridWebViewRawMessageReceivedEventArgs e)
	{
		if (string.IsNullOrEmpty(e.Message))
		{
			return;
		}

		try
		{
			using var doc = JsonDocument.Parse(e.Message);
			var type = doc.RootElement.GetProperty("type").GetString();

			switch (type)
			{
				case "ready":
					_editorReady = true;
					await PushThemeAsync();
					await LoadContentAsync(_pendingHtml);
					break;

				case "changed":
					var html = doc.RootElement.GetProperty("html").GetString() ?? string.Empty;
					_viewModel.OnEditorContentChanged(html);
					break;

				case "complete":
					var id = doc.RootElement.GetProperty("id").GetInt32();
					var context = doc.RootElement.GetProperty("context").GetString() ?? string.Empty;
					await HandleCompletionAsync(id, context);
					break;
			}
		}
		catch
		{
			// Ignore malformed messages rather than crash the UI.
		}
	}

	// Fetch a short AI continuation and hand it back to the editor as ghost text.
	private async Task HandleCompletionAsync(int id, string context)
	{
		try
		{
			var suggestion = await _aiService.CompleteAsync(context);
			if (string.IsNullOrEmpty(suggestion))
			{
				return;
			}

			var json = JsonSerializer.Serialize(suggestion);
			await EditorWebView.EvaluateJavaScriptAsync($"window.applyCompletion({id}, {json})");
		}
		catch
		{
			// Network/model hiccup: just skip this suggestion.
		}
	}
}
