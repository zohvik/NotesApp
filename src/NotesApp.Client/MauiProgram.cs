using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

		// A desktop/mobile app has no per-request boundary like an ASP.NET Core API does,
		// so the DbContext is registered as a single shared instance for the app's lifetime.
		builder.Services.AddDbContext<NotesDbContext>(
			options => options.UseSqlite($"Data Source={dbPath}"),
			contextLifetime: ServiceLifetime.Singleton,
			optionsLifetime: ServiceLifetime.Singleton);

		// Client and API both run on this same Mac for now; this address will need
		// to change once the API is reachable somewhere other than localhost.
		builder.Services.AddHttpClient<SyncService>(client =>
			client.BaseAddress = new Uri("https://localhost:7105"))
#if DEBUG
			// The app's sandboxed network stack doesn't trust the ASP.NET Core
			// dev-HTTPS certificate the same way the host machine's command line does.
			// Only ever accept an unverified certificate in local debug builds.
			.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
			{
				ServerCertificateCustomValidationCallback = (_, _, _, _) => true
			})
#endif
			;

		builder.Services.AddSingleton<NotesViewModel>();
		builder.Services.AddSingleton<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
