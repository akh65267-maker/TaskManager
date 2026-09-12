using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;

namespace Observability;

public static class ObservabilityExtensions
{
    /// <summary>
    /// Wires up structured logging (Serilog, enriched with the current trace/span
    /// id), distributed tracing (OpenTelemetry, exported via OTLP to Jaeger) and
    /// metrics (OpenTelemetry, scraped by Prometheus from GET /metrics).
    /// The trace id doubles as the correlation id: it's generated per request and
    /// - via MassTransit's native ActivitySource - propagates automatically onto
    /// outgoing messages and back out the other side in a consumer, so one id
    /// ties a request together across every service and every log line without
    /// a second, hand-rolled correlation header.
    /// </summary>
    public static WebApplicationBuilder AddObservability(
        this WebApplicationBuilder builder,
        string serviceName,
        params string[] additionalActivitySources)
    {
        builder.Host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithSpan()
            .Enrich.WithProperty("Service", serviceName)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] ({Service}) [{TraceId}] {Message:lj}{NewLine}{Exception}"));

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                foreach (var source in additionalActivitySources)
                    tracing.AddSource(source);

                // Endpoint/protocol/headers are read from the standard
                // OTEL_EXPORTER_OTLP_* environment variables - no endpoint is
                // hardcoded here so each environment (docker-compose, later a
                // real cluster) configures it independently.
                tracing.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics
                    // ASP.NET Core's own meters give the RED signals (request
                    // rate, error rate, duration histogram) without any
                    // hand-written instrumentation.
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    // MassTransit's meter: consume/publish counts, consumer
                    // duration, in-flight messages. A no-op in the services
                    // that have no bus.
                    .AddMeter("MassTransit")
                    // Every application-owned meter in this repository is named
                    // "TaskManager.<area>" so one wildcard picks all of them up.
                    .AddMeter("TaskManager.*")
                    // Pull, not push: Prometheus scrapes /metrics on each
                    // service. Deliberately *not* the OTLP exporter used for
                    // traces - scraping keeps target up/down health, which a
                    // push pipeline loses. If a second metrics backend ever
                    // appears, put an OTel Collector in front and switch this
                    // to AddOtlpExporter().
                    .AddPrometheusExporter();
            });

        // Maps GET /metrics without every service's Program.cs having to call
        // UseOpenTelemetryPrometheusScrapingEndpoint() itself.
        builder.Services.AddSingleton<IStartupFilter, PrometheusScrapingEndpointStartupFilter>();

        return builder;
    }

    private sealed class PrometheusScrapingEndpointStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.UseOpenTelemetryPrometheusScrapingEndpoint();
                next(app);
            };
    }
}
