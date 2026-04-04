namespace uk.osric.copilot.Web {
    using Microsoft.Extensions.Options;
    using uk.osric.copilot.Configuration;
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
            var copilotOpts = app.Services.GetRequiredService<IOptions<CopilotOptions>>().Value;
            metrics.SetProjectCountCallback(() => {
                return ProjectFolderHelper.EnumerateGitRepositories(copilotOpts.ProjectFoldersPath).Count();
            });

            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.MapControllers();
            app.MapPrometheusScrapingEndpoint();
            app.Run();
        }
    }
}

