using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NotesApp.Client.Ai;
using NotesApp.Client.Data;
using NotesApp.Client.Sync;
using NotesApp.Client.ViewModels;

namespace NotesApp.Client;

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
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		var dbPath = Path.Combine(FileSystem.AppDataDirectory, "notes.db");

		// EF Core contexts are not thread-safe, so the app uses two arrangements:
		//  - a factory, for background work (sync) to create its own short-lived
		//    contexts that can never collide with the UI's queries;
		//  - one long-lived context for the ViewModel, whose tracked entities
		//    back the UI (list selection relies on stable instances).
		builder.Services.AddDbContextFactory<NotesDbContext>(
			options => options.UseSqlite($"Data Source={dbPath}"));
		builder.Services.AddSingleton<NotesDbContext>(sp =>
			sp.GetRequiredService<IDbContextFactory<NotesDbContext>>().CreateDbContext());

		// Client and API both run on this same Mac for now; this address will need
		// to change once the API is reachable somewhere other than localhost.
		builder.Services.AddHttpClient<SyncService>(client =>
			client.BaseAddress = new Uri("https://localhost:7105"))
#if DEBUG
			// The app's sandboxed network stack doesn't trust the ASP.NET Core
			// dev-HTTPS certificate the same way the host machine's command line
			// does. Debug builds accept the unverified cert for LOCALHOST ONLY —
			// never blanket-trust every certificate.
			.ConfigurePrimaryHttpMessageHandler(() => DevLocalhostHandler())
#endif
			;

		// Same API host as SyncService. Given a longer timeout because a local LLM
		// can take several seconds to produce a summary on first run.
		builder.Services.AddHttpClient<AiService>(client =>
		{
			client.BaseAddress = new Uri("https://localhost:7105");
			client.Timeout = TimeSpan.FromMinutes(2);
		})
#if DEBUG
			.ConfigurePrimaryHttpMessageHandler(() => DevLocalhostHandler())
#endif
			;

		builder.Services.AddSingleton<NotesViewModel>();
		builder.Services.AddSingleton<MainPage>();

#if DEBUG
		// Accepts certificate errors only for local dev hosts; anything else
		// still gets full certificate validation even in debug builds.
		static HttpClientHandler DevLocalhostHandler() => new()
		{
			ServerCertificateCustomValidationCallback = (msg, _, _, errors) =>
				errors == System.Net.Security.SslPolicyErrors.None
				|| msg.RequestUri?.Host is "localhost" or "127.0.0.1"
		};
#endif

#if DEBUG
		builder.Logging.AddDebug();
		builder.Services.AddHybridWebViewDeveloperTools();
#endif

		return builder.Build();
	}
}
