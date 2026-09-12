using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;

namespace Observability;

public static class ObservabilityExtensions
{
    /// <summary>
    /// Wires up structured logging (Serilog, enriched with the current trace/span
    /// id) and distributed tracing (OpenTelemetry, exported via OTLP to Jaeger).
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
            });

        return builder;
    }
}
