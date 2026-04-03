namespace uk.osric.copilot.Web {
    using uk.osric.copilot.Infrastructure;

    /// <summary>Application entry point.</summary>
    internal sealed class Program {
        private static async Task Main(string[] args) {
            var builder = WebApplication.CreateBuilder(args);
            builder.Host.UseSystemd();
            builder.AddCopilotServices();

            var app = builder.Build();
            await app.InitialiseDatabaseAsync();
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.MapCopilotApi();
            app.Run();
        }
    }
}
