namespace uk.osric.copilot.Web {
    using uk.osric.copilot.Infrastructure;
    using uk.osric.copilot.Services;

    /// <summary>Application entry point.</summary>
    internal sealed class Program {
        private static async Task Main(string[] args) {
            var builder = WebApplication.CreateBuilder(args);
            builder.Host.UseSystemd();
            builder.AddCopilotServices();

            var app = builder.Build();
            await app.InitialiseDatabaseAsync();

            var metrics = app.Services.GetRequiredService<CopilotMetrics>();
            metrics.SetProjectCountCallback(() => {
                var root = app.Configuration.GetValue<string>("ProjectFoldersPath") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) {
                    return 0;
                }
                return Directory.EnumerateDirectories(root)
                    .Count(dir => Directory.Exists(Path.Combine(dir, ".git")));
            });

            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.MapControllers();
            app.MapPrometheusScrapingEndpoint();
            app.Run();
        }
    }
}

