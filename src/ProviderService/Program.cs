using Prometheus;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// ============================================================
// ProviderService — simula integraciones con proveedores externos
// En el mundo real sería un adaptador hacia APIs de Amadeus,
// Sabre, o cualquier proveedor de contenido turístico
// ============================================================

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddHttpClient();

// Configuración de trazas — igual que en ApiService
// Al compartir el mismo colector de Jaeger, las trazas de ambos
// servicios se unen automáticamente por el trace_id propagado
// en las cabeceras HTTP
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("providerservice"))
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(opts =>
            {
                opts.Endpoint = new Uri("http://jaeger:4317");
            });
    });

var app = builder.Build();

app.UseRouting();
app.UseHttpMetrics();
app.MapMetrics();
app.MapControllers();

// Variable que controla la degradación artificial de latencia
// Permite simular un proveedor que se va poniendo más lento
// sin necesidad de reiniciar el servicio
var degradation = 0;

// Proveedor rápido — simula un proveedor con buena conectividad
// Latencia base: 50-300ms + degradación acumulada
app.MapGet("/availability", async () => {
    await Task.Delay(Random.Shared.Next(50, 300) + degradation);
    return Results.Ok(new {
        provider = "amadeus",
        available = true,
        price = Random.Shared.Next(200, 1500)
    });
});

// Proveedor lento — simula un proveedor con peor rendimiento
// Latencia base: 1000-3000ms + degradación acumulada
// Es el cuello de botella principal de /packages en ApiService
app.MapGet("/availability/slow", async () => {
    await Task.Delay(Random.Shared.Next(1000, 3000) + degradation);
    return Results.Ok(new {
        provider = "slowprovider",
        available = true,
        price = Random.Shared.Next(200, 1500)
    });
});

// Endpoint de degradación progresiva
// Cada llamada añade 200ms de latencia a todos los endpoints
// Permite simular un proveedor que se degrada poco a poco
// En Grafana verás el p99 subir de forma gradual y constante
app.MapGet("/degrade", () => {
    degradation += 200;
    return Results.Ok(new { degradation_ms = degradation });
});

// Endpoint de recuperación — resetea la degradación a cero
// Simula que el proveedor externo se recupera
app.MapGet("/reset", () => {
    degradation = 0;
    return Results.Ok(new { degradation_ms = degradation });
});

app.Run();
