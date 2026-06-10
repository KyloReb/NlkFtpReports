using NlkFtpReports.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ──
// Bind NlkFtpSettings from appsettings.json section
builder.Services.Configure<NlkFtpSettings>(
    builder.Configuration.GetSection("NlkFtpSettings"));

// ── DI Registration ──
builder.Services.AddScoped<IArchiveService, ArchiveService>();
builder.Services.AddSingleton<ArchiveWatcherService>();
builder.Services.AddSingleton<SearchService>();

// ── Blazor Interactive Server ──
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<NlkFtpReports.Components.App>()
    .AddInteractiveServerRenderMode();

// ── Start file watcher ──
var watcher = app.Services.GetRequiredService<ArchiveWatcherService>();
watcher.Start();

app.Run();

/// <summary>
/// Strongly-typed settings from appsettings.json section "NlkFtpSettings".
/// Centralized here so all services read from one source.
/// </summary>
public class NlkFtpSettings
{
    /// <summary>Path to the folder containing .rar files.</summary>
    public string WatchDirectory { get; set; } = string.Empty;

    /// <summary>Static password used by all daily .rar archives.</summary>
    public string ArchivePassword { get; set; } = string.Empty;
}
