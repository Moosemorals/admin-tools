namespace uk.osric.copilot.Web {
    using Microsoft.EntityFrameworkCore;
    using uk.osric.copilot.Data;
    using uk.osric.copilot.Services;

    internal static class AppServiceExtensions {
        /// <summary>
        /// Registers EF Core, the SSE broadcaster, the session repository, and the
        /// Copilot hosted service.  Call this on <see cref="WebApplicationBuilder"/>
        /// before <see cref="WebApplicationBuilder.Build"/>.
        /// </summary>
        internal static WebApplicationBuilder AddCopilotServices(this WebApplicationBuilder builder) {
            var dbPath = builder.Configuration.GetValue<string>("DatabasePath") ?? "copilot-sessions.db";
            builder.Services.AddDbContextFactory<CopilotDbContext>(opts =>
                opts.UseSqlite($"Data Source={dbPath}"));

            var copilotUrl = builder.Configuration.GetValue<string>("CopilotUrl");
            builder.Services.AddSingleton<SseBroadcaster>();
            builder.Services.AddSingleton<SessionRepository>();

            // CopilotService requires copilotUrl at construction time, so we use a factory
            // delegate rather than the automatic constructor injection.
            builder.Services.AddSingleton<CopilotService>(sp =>
                new CopilotService(
                    sp.GetRequiredService<ILogger<CopilotService>>(),
                    sp.GetRequiredService<SessionRepository>(),
                    sp.GetRequiredService<SseBroadcaster>(),
                    copilotUrl));

            // Register as both the concrete type (for direct resolution) and the hosted service.
            builder.Services.AddHostedService(sp => sp.GetRequiredService<CopilotService>());

            return builder;
        }
    }
}
