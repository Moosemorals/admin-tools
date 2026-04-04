// SPDX-FileCopyrightText: Copyright (c) 2026 Osric Wilkinson
// SPDX-License-Identifier: MIT

namespace uk.osric.copilot.Web {
    using System.Reflection;
    using System.Threading.Channels;
    using Microsoft.EntityFrameworkCore;
    using MimeKit;
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
            var copilotOpts = builder.Configuration.GetSection("Copilot").Get<CopilotOptions>() ?? new CopilotOptions();
            var dbPath = copilotOpts.DatabasePath;
            var copilotUrl = copilotOpts.CopilotUrl;

            builder.Services.AddDbContextFactory<CopilotDbContext>(opts =>
                opts.UseSqlite($"Data Source={dbPath}"));

            builder.Services.AddSingleton<SseBroadcaster>();
            builder.Services.AddSingleton<SessionRepository>();

            builder.Services.Configure<CopilotOptions>(builder.Configuration.GetSection("Copilot"));
            builder.Services.AddSingleton<CertificateService>();
            builder.Services.AddSingleton<EmailMetrics>();
            builder.Services.AddSingleton<CopilotMetrics>();
            builder.Services.AddSingleton<SmtpSenderService>();

            // CopilotService requires copilotUrl at construction time, so we use a factory
            // delegate rather than the automatic constructor injection.
            builder.Services.AddSingleton<CopilotService>(sp =>
                new CopilotService(
                    sp.GetRequiredService<ILogger<CopilotService>>(),
                    sp.GetRequiredService<SessionRepository>(),
                    sp.GetRequiredService<SseBroadcaster>(),
                    sp.GetRequiredService<CopilotMetrics>(),
                    sp.GetRequiredService<SmtpSenderService>(),
                    copilotUrl));

            // Register as both the concrete type (for direct resolution) and the hosted service.
            builder.Services.AddHostedService(sp => sp.GetRequiredService<CopilotService>());

            var channelCapacity = copilotOpts.Email.ChannelCapacity;
            var emailChannel = Channel.CreateBounded<MimeMessage>(new BoundedChannelOptions(channelCapacity) {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropWrite,
            });
            builder.Services.AddSingleton(emailChannel.Writer);
            builder.Services.AddSingleton(emailChannel.Reader);

            builder.Services.AddHostedService<ImapListenerService>();
            builder.Services.AddHostedService<EmailProcessorService>();
            builder.Services.AddHostedService<CopilotOutboundEmailService>();

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
                    m.AddMeter("uk.osric.copilot");
                    m.AddPrometheusExporter();
                })
                .WithTracing(t => {
                    t.AddAspNetCoreInstrumentation();
                    t.AddHttpClientInstrumentation();
                    t.AddSource("uk.osric.copilot");
                    t.AddEntityFrameworkCoreInstrumentation();
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"))) {
                        t.AddOtlpExporter();
                    }
                });

            return builder;
        }
    }
}

