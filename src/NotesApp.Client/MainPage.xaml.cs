using System.ComponentModel;
using System.Text;
using System.Text.Json;
using NotesApp.Client.Ai;
using NotesApp.Client.Text;
using NotesApp.Client.Theming;
using NotesApp.Client.ViewModels;

namespace NotesApp.Client;

public partial class MainPage : ContentPage
{
	private readonly NotesViewModel _viewModel;
	private readonly AiService _aiService;

	// The editor <-> C# bridge is polled: JS queues outbound messages that we
	// collect via window.__drain(), and we push inbound messages via
	// window.__inbox(). We use EvaluateJavaScriptAsync because the HybridWebView
	// message bridge (window.HybridWebView) isn't injected reliably here.
	private IDispatcherTimer? _pollTimer;
	private bool _draining;
	private bool _editorReady;
	private string _pendingHtml = string.Empty;

	public MainPage(NotesViewModel viewModel, AiService aiService)
	{
		InitializeComponent();
		_viewModel = viewModel;
		_aiService = aiService;
		BindingContext = viewModel;

		_viewModel.EditorContentRequested += OnEditorContentRequested;
		_viewModel.EditorContentFetcher = FetchEditorContentAsync;
		_viewModel.PropertyChanged += OnViewModelPropertyChanged;

		ApplySidebarState();
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		// Poll the editor for outbound messages. 100ms keeps the initial content
		// load and ghost-text/autosave round-trips feeling immediate; an empty
		// drain is cheap, so the extra polls cost little on a desktop app.
		if (_pollTimer is null)
		{
			_pollTimer = Dispatcher.CreateTimer();
			_pollTimer.Interval = TimeSpan.FromMilliseconds(100);
			_pollTimer.Tick += async (_, _) => await DrainAsync();
			_pollTimer.Start();
		}

		// Show local data immediately (offline-first), then sync in the background.
		await _viewModel.LoadDataCommand.ExecuteAsync(null);
		_ = _viewModel.SyncCommand.ExecuteAsync(null);
	}

	// Repaint the WebView editor when the app theme changes, and resize the
	// sidebar column when it's collapsed/expanded (ColumnDefinition widths
	// aren't bindable from XAML, so the ViewModel flag is applied here).
	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(NotesViewModel.SelectedTheme) && _editorReady)
		{
			PushTheme();
		}
		else if (e.PropertyName == nameof(NotesViewModel.IsSidebarOpen))
		{
			ApplySidebarState();
		}
	}

	private void ApplySidebarState()
	{
		SidebarColumn.Width = _viewModel.IsSidebarOpen ? new GridLength(210) : new GridLength(0);
		SidebarPane.IsVisible = _viewModel.IsSidebarOpen;
	}

	// The ViewModel asks us to (re)load the editor's content.
	private void OnEditorContentRequested(string html, bool keepHistory)
	{
		if (_editorReady)
		{
			LoadContent(html, keepHistory);
		}
		else
		{
			_pendingHtml = html; // pre-ready loads always start a fresh history
		}
	}

	// ---- Outbound: C# -> JS via window.__inbox(base64) ----

	private void SendToEditor(object payload)
	{
		var json = JsonSerializer.Serialize(payload);
		var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
		MainThread.BeginInvokeOnMainThread(async () =>
		{
			try { await EditorWebView.EvaluateJavaScriptAsync($"window.__inbox('{b64}')"); }
			catch { /* editor not ready yet */ }
		});
	}

	// Each content load gets a new epoch. 'changed' messages echo the epoch they
	// were typed under, so a message that describes a PREVIOUS note (drained but
	// not yet dispatched when the user switched) can be recognized and dropped
	// instead of overwriting the newly opened note.
	private int _contentEpoch;

	private void LoadContent(string html, bool keepHistory = false) =>
		SendToEditor(new { action = "load", html, epoch = ++_contentEpoch, keepHistory });

	private void PushTheme()
	{
		var t = ThemeManager.CurrentTheme;
		SendToEditor(new
		{
			action = "theme",
			theme = new
			{
				bg = t.Bg,
				text = t.TextPrimary,
				muted = t.TextMuted,
				accent = t.Accent,
				divider = t.Divider,
				dark = t.IsDark
			}
		});
	}

	// ---- Inbound: JS -> C# via polling window.__drain() ----

	private async Task DrainAsync()
	{
		if (_draining)
		{
			return;
		}

		_draining = true;
		try
		{
			var raw = await EditorWebView.EvaluateJavaScriptAsync("window.__drain ? window.__drain() : ''");
			var b64 = Sanitize(raw);
			if (b64.Length == 0)
			{
				return;
			}

			string json;
			try { json = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
			catch { return; }

			using var doc = JsonDocument.Parse(json);
			foreach (var el in doc.RootElement.EnumerateArray())
			{
				await DispatchAsync(el);
			}
		}
		catch
		{
			// Ignore transient evaluation errors.
		}
		finally
		{
			_draining = false;
		}
	}

	private async Task DispatchAsync(JsonElement el)
	{
		var type = el.GetProperty("type").GetString();
		switch (type)
		{
			case "ready":
				_editorReady = true;
				PushTheme();
				LoadContent(_pendingHtml);
				break;

			case "changed":
				// Drop stale messages from a previously loaded note (see _contentEpoch).
				var epoch = el.TryGetProperty("epoch", out var e) ? e.GetInt32() : 0;
				if (epoch == _contentEpoch)
				{
					_viewModel.OnEditorContentChanged(el.GetProperty("html").GetString() ?? string.Empty);
				}
				break;

			case "complete":
				var id = el.GetProperty("id").GetInt32();
				var context = el.GetProperty("context").GetString() ?? string.Empty;
				await HandleCompletionAsync(id, context);
				break;

			// Block menu's "Ask AI": rewrite one block of the note per the user's
			// instruction. Runs through the same API endpoint as the AI panel.
			case "blockAi":
				var blockId = el.GetProperty("id").GetInt32();
				var blockEpoch = el.TryGetProperty("epoch", out var be) ? be.GetInt32() : 0;
				var blockText = el.GetProperty("text").GetString() ?? string.Empty;
				var instruction = el.GetProperty("instruction").GetString() ?? string.Empty;
				_ = HandleBlockAiAsync(blockId, blockEpoch, blockText, instruction);
				break;
		}
	}

	// Fire-and-forget so a slow model doesn't stall the message-drain loop; the
	// editor shows the pending state on the block until the reply lands. The
	// epoch rides along so the editor can drop results for a note that's no
	// longer open.
	private async Task HandleBlockAiAsync(int id, int epoch, string text, string instruction)
	{
		if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(instruction))
		{
			SendToEditor(new { action = "blockAiResult", id, epoch, error = true });
			return;
		}

		try
		{
			var result = await _aiService.RewriteAsync(text, instruction);
			SendToEditor(new { action = "blockAiResult", id, epoch, html = MarkdownConverter.ToHtml(result) });
		}
		catch
		{
			// Model/API failure: clear the block's pending state; the AI panel's
			// status line is not involved here, so there's nothing else to show.
			SendToEditor(new { action = "blockAiResult", id, epoch, error = true });
		}
	}

	// EvaluateJavaScriptAsync can return the string wrapped in quotes; strip them.
	// base64 never contains quotes, so this is safe.
	private static string Sanitize(string? raw)
	{
		if (string.IsNullOrEmpty(raw))
		{
			return string.Empty;
		}

		raw = raw.Trim();
		if (raw.Length >= 2 && raw.StartsWith('"') && raw.EndsWith('"'))
		{
			raw = raw[1..^1];
		}

		return raw is "null" ? string.Empty : raw;
	}

	// Pulls the editor's current HTML directly (bypassing the debounced 'changed'
	// path) so a save can never miss the very latest keystrokes.
	private async Task<string?> FetchEditorContentAsync()
	{
		if (!_editorReady)
		{
			return null;
		}

		try
		{
			var raw = await EditorWebView.EvaluateJavaScriptAsync("window.__flush ? window.__flush() : ''");
			var b64 = Sanitize(raw);
			if (b64.Length == 0)
			{
				return null;
			}

			var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
			using var doc = JsonDocument.Parse(json);
			return doc.RootElement.GetProperty("html").GetString();
		}
		catch
		{
			return null; // fall back to the last debounced content
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

			SendToEditor(new { action = "complete", id, text = suggestion });
		}
		catch
		{
			// Network/model hiccup: just skip this suggestion.
		}
	}
}
