using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Maksov.VideoService.BuildingBlocks.Observability;

public static class TelemetryExtensions
{
    /// <summary>
    /// Enables OpenTelemetry (traces and metrics, OTLP export) only when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set. Without a collector no telemetry is gathered, saving CPU and memory.
    /// </summary>
    public static IHostApplicationBuilder AddServiceTelemetry(
        this IHostApplicationBuilder builder,
        string serviceName,
        Action<TracerProviderBuilder>? tracing = null,
        Action<MeterProviderBuilder>? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return builder;
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t =>
            {
                t.AddSource(serviceName).AddNpgsql();
                tracing?.Invoke(t);
                t.AddOtlpExporter();
            })
            .WithMetrics(m =>
            {
                m.AddMeter(serviceName);
                metrics?.Invoke(m);
                m.AddOtlpExporter();
            });
        return builder;
    }
}

