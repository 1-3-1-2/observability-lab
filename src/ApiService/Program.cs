using Prometheus;
using System.Text.Json;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// ============================================================
// ApiService — servicio principal que actúa como gateway
// Recibe peticiones externas y las distribuye a otros servicios
// ============================================================

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();

// HttpClient registrado como factory para reutilizar conexiones
// Evita el problema de socket exhaustion que ocurre con 'new HttpClient()'
builder.Services.AddHttpClient();

// Configuración de trazas distribuidas con OpenTelemetry
// Las trazas permiten ver el recorrido completo de una request
// a través de múltiples servicios
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            // Identificamos este servicio en Jaeger
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("apiservice"))
            // Auto-instrumentación de ASP.NET Core — crea un span por cada request HTTP entrante
            .AddAspNetCoreInstrumentation()
            // Auto-instrumentación de HttpClient — crea un span por cada llamada HTTP saliente
            // Esto es lo que permite ver las llamadas a providerservice en Jaeger
            .AddHttpClientInstrumentation()
            // Exportamos las trazas al colector OTLP de Jaeger
            .AddOtlpExporter(opts =>
            {
                opts.Endpoint = new Uri("http://jaeger:4317");
            });
    });

var app = builder.Build();

app.UseRouting();

// UseHttpMetrics registra automáticamente métricas de latencia y requests
// para cada endpoint — estas son las que vemos en Grafana
app.UseHttpMetrics();

// Expone el endpoint /metrics que Prometheus scrapeará cada 15 segundos
app.MapMetrics();
app.MapControllers();

// Lista en memoria para simular un memory leak
// En producción esto ocurre cuando se acumulan objetos que nunca se liberan
var leak = new List<byte[]>();

// Endpoint rápido — simula una operación simple sin latencia artificial
app.MapGet("/fast", () => Results.Ok(new { message = "respuesta rapida", ms = 10 }));

// Endpoint lento — simula una operación con latencia variable
// Útil para ver cómo el p99 difiere de la media en Grafana
app.MapGet("/slow", async () => {
    await Task.Delay(Random.Shared.Next(200, 800));
    return Results.Ok(new { message = "respuesta lenta" });
});

// Endpoint que falla aleatoriamente el 50% de las veces
// Permite ver la tasa de errores en el dashboard de Grafana
app.MapGet("/error", () => {
    if (Random.Shared.Next(0, 2) == 0)
        return Results.Problem("error aleatorio");
    return Results.Ok(new { message = "esta vez no fallo" });
});

// Endpoint principal — busca disponibilidad en dos proveedores en PARALELO
// Usando Task.WhenAll en lugar de await secuencial:
// - Secuencial: tiempo_total = tiempo_fast + tiempo_slow (~3s)
// - Paralelo:   tiempo_total = max(tiempo_fast, tiempo_slow) (~2s)
// Esta es una decisión de arquitectura con impacto directo en la latencia
app.MapGet("/packages", async (IHttpClientFactory factory) => {
    var client = factory.CreateClient();

    // Lanzamos las dos llamadas sin await — se ejecutan en paralelo
    var fastTask = client.GetStringAsync("http://providerservice:8080/availability");
    var slowTask = client.GetStringAsync("http://providerservice:8080/availability/slow");

    // Esperamos a que las DOS terminen antes de continuar
    await Task.WhenAll(fastTask, slowTask);

    var fastObj = JsonSerializer.Deserialize<object>(fastTask.Result);
    var slowObj = JsonSerializer.Deserialize<object>(slowTask.Result);

    return Results.Json(new {
        message = "busqueda completada en paralelo",
        fast_provider = fastObj,
        slow_provider = slowObj
    });
});

// Endpoint de simulación de memory leak
// Cada llamada reserva 1MB de memoria que nunca se libera
// En Grafana verás process_working_set_bytes subir linealmente
app.MapGet("/leak", () => {
    leak.Add(new byte[1024 * 1024]); // 1MB por llamada
    var mb = leak.Count;
    return Results.Ok(new { leaked_mb = mb, message = $"Memoria acumulada: {mb}MB" });
});

// Endpoint de recuperación — libera toda la memoria acumulada
// Verás la línea de memoria bajar bruscamente en Grafana
app.MapGet("/leak/reset", () => {
    leak.Clear();
    GC.Collect(); // Forzamos la recolección de basura inmediatamente
    return Results.Ok(new { message = "memoria liberada" });
});

app.Run();
