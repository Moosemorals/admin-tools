namespace uk.osric.copilot.Web {
    using System.Reflection;
    using Microsoft.EntityFrameworkCore;
    using OpenTelemetry.Metrics;
    using OpenTelemetry.Resources;
    using OpenTelemetry.Trace;
    using System.Text.Json;
    using uk.osric.copilot.Configuration;
    using uk.osric.copilot.Data;
    using uk.osric.copilot.Services;

    internal static class AppServiceExtensions {
        /// <summary>
        /// Registers EF Core, the SSE broadcaster, the session repository, the
        /// Copilot hosted service, and the MVC controller infrastructure.
        /// Call this on <see cref="WebApplicationBuilder"/> before
        /// <see cref="WebApplicationBuilder.Build"/>.
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

            builder.Services.Configure<CopilotOptions>(builder.Configuration.GetSection("Copilot"));
            builder.Services.AddSingleton<CertificateService>();
            builder.Services.AddSingleton<EmailMetrics>();

            // AddControllers brings in MVC routing and model binding.
            // JSON options are configured to match what the frontend expects: camelCase
            // output and case-insensitive input so callers can use any casing.
            builder.Services.AddControllers().AddJsonOptions(opts => {
                opts.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                opts.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            });

            var serviceVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

            builder.Services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddService("uk.osric.copilot", serviceVersion: serviceVersion))
                .WithMetrics(m => {
                    m.AddAspNetCoreInstrumentation();
                    m.AddHttpClientInstrumentation();
                    m.AddRuntimeInstrumentation();
                    m.AddMeter("uk.osric.copilot.email");
                    m.AddPrometheusExporter();
                })
                .WithTracing(t => {
                    t.AddAspNetCoreInstrumentation();
                    t.AddHttpClientInstrumentation();
                    t.AddSource("uk.osric.copilot");
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"))) {
                        t.AddOtlpExporter();
                    }
                });

            return builder;
        }
    }
}

