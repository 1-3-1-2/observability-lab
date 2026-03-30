using Prometheus;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddHttpClient();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("providerservice"))
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(opts => { opts.Endpoint = new Uri("http://jaeger:4317"); });
    });

var app = builder.Build();
app.UseRouting();
app.UseHttpMetrics();
app.MapMetrics();
app.MapControllers();

var degradation = 0;
var slowProviderFailing = false;

// Conexión a Redis para invalidación activa
IDatabase? redisDb = null;
try
{
    var redis = await ConnectionMultiplexer.ConnectAsync("redis:6379");
    redisDb = redis.GetDatabase();
    app.Logger.LogInformation("ProviderService conectado a Redis");
}
catch (Exception ex)
{
    app.Logger.LogWarning("No se pudo conectar a Redis: {Error}", ex.Message);
}

app.MapGet("/availability", async () => {
    await Task.Delay(Random.Shared.Next(50, 300) + degradation);
    return Results.Ok(new {
        provider = "amadeus",
        available = true,
        price = Random.Shared.Next(200, 1500)
    });
});

app.MapGet("/availability/slow", async () => {
    if (slowProviderFailing)
        return Results.Problem(detail: "Provider temporarily unavailable", statusCode: 503);
    await Task.Delay(Random.Shared.Next(1000, 3000) + degradation);
    return Results.Ok(new {
        provider = "slowprovider",
        available = true,
        price = Random.Shared.Next(200, 1500)
    });
});

// Simula una actualización de precios para un destino
// Invalida activamente las claves de caché afectadas en Redis
app.MapPost("/prices/update", async (PriceUpdate update) =>
{
    app.Logger.LogInformation("Actualizando precios para destino {Destination}", update.Destination);

    if (redisDb != null)
    {
        // Borramos la clave de caché para ese destino específico
        var cacheKey = $"packages:{update.Destination}";
        var deleted = await redisDb.KeyDeleteAsync(cacheKey);

        app.Logger.LogInformation(
            "Caché invalidado para {Destination} — clave {Key} {Result}",
            update.Destination, cacheKey, deleted ? "eliminada" : "no existía");

        return Results.Ok(new {
            destination = update.Destination,
            cache_invalidated = deleted,
            new_price = update.NewPrice,
            message = deleted
                ? $"Caché invalidado. Próxima consulta irá a los proveedores"
                : $"No había caché para ese destino"
        });
    }

    return Results.Ok(new { message = "Redis no disponible, no se invalidó caché" });
});

// Invalida todo el caché de paquetes
app.MapPost("/prices/update/all", async () =>
{
    if (redisDb != null)
    {
        // Buscamos todas las claves que empiecen por "packages:"
        var server = redisDb.Multiplexer.GetServer("redis", 6379);
        var keys = server.Keys(pattern: "packages:*").ToArray();

        await redisDb.KeyDeleteAsync(keys);

        app.Logger.LogInformation("Caché global invalidado — {Count} claves eliminadas", keys.Length);

        return Results.Ok(new {
            keys_deleted = keys.Length,
            message = $"Caché global invalidado — {keys.Length} destinos afectados"
        });
    }

    return Results.Ok(new { message = "Redis no disponible" });
});

app.MapPost("/availability/slow/fail", () => {
    slowProviderFailing = true;
    return Results.Ok(new { message = "slowprovider ahora falla con 503" });
});

app.MapPost("/availability/slow/recover", () => {
    slowProviderFailing = false;
    return Results.Ok(new { message = "slowprovider recuperado" });
});

app.MapGet("/degrade", () => {
    degradation += 200;
    return Results.Ok(new { degradation_ms = degradation });
});

app.MapGet("/reset", () => {
    degradation = 0;
    slowProviderFailing = false;
    return Results.Ok(new { degradation_ms = degradation });
});

app.Run();

public record PriceUpdate(string Destination, int NewPrice);
