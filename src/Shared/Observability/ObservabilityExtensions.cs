using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

        // Maps GET /metrics and injects the trace id into every response
        // header + ProblemDetails, without every service's Program.cs having
        // to opt in explicitly.
        builder.Services.AddSingleton<IStartupFilter, ObservabilityStartupFilter>();

        // Enriches ProblemDetails with the trace id so error responses
        // carry the same id as the response header and the Jaeger trace.
        // Services call AddProblemDetails() themselves; this customisation
        // must be registered after that call, but AddObservability() is
        // always the first call in Program.cs so we register it here and it
        // stacks fine (customisations are additive).
        builder.Services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = ctx =>
            {
                var traceId = Activity.Current?.TraceId.ToString();
                if (traceId is not null)
                    ctx.ProblemDetails.Extensions["traceId"] = traceId;
            };
        });

        return builder;
    }

    private sealed class ObservabilityStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.UseOpenTelemetryPrometheusScrapingEndpoint();

                // Stamp every response with the current trace id so callers
                // (the frontend, a curl session, a support ticket) can look
                // up the exact trace in Jaeger without digging through logs.
                // The trace id is the correlation id - see decisions.md.
                app.Use(async (context, nextMiddleware) =>
                {
                    var traceId = Activity.Current?.TraceId.ToString();
                    if (traceId is not null)
                        context.Response.Headers["X-Trace-Id"] = traceId;

                    await nextMiddleware(context);
                });

                next(app);
            };
    }
}
