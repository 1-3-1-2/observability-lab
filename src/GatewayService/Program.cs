using Prometheus;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

// ============================================================
// GatewayService — punto de entrada único con rate limiting
// Protege todos los servicios downstream de abuso y sobrecarga
// ============================================================

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddRateLimiter(options =>
{
    // Respuesta 429 cuando se supera el límite
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        context.HttpContext.Response.Headers["Retry-After"] = "60";
        await context.HttpContext.Response.WriteAsync(
            "{\"error\":\"Too many requests\",\"retry_after_seconds\":60}",
            cancellationToken);
    };

    // Límite global por IP
    // 60 peticiones por minuto — suficiente para uso normal
    // Cada IP tiene su propio contador independiente
    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(

        // Límite por minuto — 60 peticiones
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // Límite más estricto para creación de reservas
            if (context.Request.Path.StartsWithSegments("/api/bookings") &&
                context.Request.Method == "POST")
            {
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"bookings:{ip}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    });
            }

            return RateLimitPartition.GetFixedWindowLimiter(
                $"global:{ip}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 5
                });
        }),

        // Límite por segundo — máximo 10 peticiones/s para evitar bursts
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(
                $"burst:{ip}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromSeconds(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                });
        })
    );
});

var app = builder.Build();

app.UseRouting();
app.UseHttpMetrics();
app.UseRateLimiter();
app.MapMetrics();
app.MapReverseProxy();

app.Run();
