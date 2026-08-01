using Microsoft.EntityFrameworkCore;
using Ut2004Stats.Core.Data;
using Ut2004Stats.Core.Services;
using Ut2004Stats.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<StatsOptions>(
    builder.Configuration.GetSection(StatsOptions.SectionName));

var dbPath = builder.Configuration["Stats:DatabasePath"]
             ?? Path.Combine(AppContext.BaseDirectory, "ut2004stats.db");

// Ensure the directory exists before SQLite tries to create the file in it.
var dbDirectory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
if (!string.IsNullOrEmpty(dbDirectory)) Directory.CreateDirectory(dbDirectory);

builder.Services.AddDbContext<StatsDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddScoped<MatchImporter>();
builder.Services.AddScoped<StatsQueries>();
builder.Services.AddHostedService<LogWatcherService>();

var app = builder.Build();

// The schema is created from the model — there are no migrations to run, and the
// database is disposable: deleting it simply re-imports every log on the next scan.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StatsDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// A tiny liveness endpoint so container orchestrators can health-check the app.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
