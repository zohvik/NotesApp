using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NotesApp.Client.Data;
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

		builder.Services.AddSingleton<NotesViewModel>();
		builder.Services.AddSingleton<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
