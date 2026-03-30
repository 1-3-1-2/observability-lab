using Prometheus;
using System.Text;
using System.Text.Json;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using Npgsql;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddHttpClient();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("apiservice"))
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(opts => { opts.Endpoint = new Uri("http://jaeger:4317"); });
    });

var app = builder.Build();
app.UseRouting();
app.UseHttpMetrics();
app.MapMetrics();
app.MapControllers();

var leak = new List<byte[]>();
var pgConnection = "Host=postgres;Database=bookings;Username=admin;Password=admin";

IDatabase? redisDb = null;
try
{
    var redis = await ConnectionMultiplexer.ConnectAsync("redis:6379");
    redisDb = redis.GetDatabase();
    app.Logger.LogInformation("Conectado a Redis");
}
catch (Exception ex)
{
    app.Logger.LogWarning("No se pudo conectar a Redis: {Error}", ex.Message);
}

IConnection? rabbitConnection = null;
IChannel? rabbitChannel = null;

for (int attempt = 1; attempt <= 10; attempt++)
{
    try
    {
        var factory = new ConnectionFactory { HostName = "rabbitmq", UserName = "admin", Password = "admin" };
        rabbitConnection = await factory.CreateConnectionAsync();
        rabbitChannel = await rabbitConnection.CreateChannelAsync();
        await rabbitChannel.QueueDeclareAsync("booking-requests", durable: true, exclusive: false, autoDelete: false);
        app.Logger.LogInformation("Conectado a RabbitMQ en intento {Attempt}", attempt);
        break;
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("RabbitMQ intento {Attempt}/10: {Error}", attempt, ex.Message);
        await Task.Delay(5000);
    }
}

var cacheHits = Metrics.CreateCounter("cache_hits_total", "Cache hits", "endpoint");
var cacheMisses = Metrics.CreateCounter("cache_misses_total", "Cache misses", "endpoint");
var cacheStale = Metrics.CreateCounter("cache_stale_total", "Stale cache servidos", "endpoint");

// TTL real del caché — después de este tiempo los datos se consideran frescos
const int CacheTtlSeconds = 30;
// Tiempo extra de gracia — durante este tiempo los datos se sirven como stale
// mientras se refresca en background
const int StaleGraceSeconds = 60;

app.MapGet("/fast", () => Results.Ok(new { message = "respuesta rapida", ms = 10 }));
app.MapGet("/slow", async () => { await Task.Delay(Random.Shared.Next(200, 800)); return Results.Ok(new { message = "respuesta lenta" }); });
app.MapGet("/error", () => Random.Shared.Next(0, 2) == 0 ? Results.Problem("error aleatorio") : Results.Ok(new { message = "esta vez no fallo" }));

app.MapGet("/packages", async (IHttpClientFactory factory, string? destination) =>
{
    destination ??= "default";
    var cacheKey = $"packages:{destination}";
    var staleKey = $"packages:stale:{destination}";

    if (redisDb != null)
    {
        // Intentamos obtener datos frescos
        var cached = await redisDb.StringGetAsync(cacheKey);
        if (cached.HasValue)
        {
            cacheHits.WithLabels("/packages").Inc();
            app.Logger.LogInformation("Cache HIT (fresco) para {Destination}", destination);
            return Results.Json(new
            {
                source = "cache_fresh",
                destination,
                data = JsonSerializer.Deserialize<object>(cached.ToString())
            });
        }

        // Cache expirado — intentamos obtener datos stale
        var stale = await redisDb.StringGetAsync(staleKey);
        if (stale.HasValue)
        {
            cacheStale.WithLabels("/packages").Inc();
            app.Logger.LogInformation("Cache STALE para {Destination} — refrescando en background", destination);

            // Refrescamos en background sin bloquear al usuario
            _ = Task.Run(async () =>
            {
                try
                {
                    var bgClient = factory.CreateClient();
                    var fastTask = bgClient.GetStringAsync("http://providerservice:8080/availability");
                    var slowTask = bgClient.GetStringAsync("http://providerservice:8080/availability/slow");
                    await Task.WhenAll(fastTask, slowTask);

                    var freshResult = new
                    {
                        fast_provider = JsonSerializer.Deserialize<object>(fastTask.Result),
                        slow_provider = JsonSerializer.Deserialize<object>(slowTask.Result)
                    };

                    var serialized = JsonSerializer.Serialize(freshResult);
                    // Guardamos los datos frescos y actualizamos el stale
                    await redisDb.StringSetAsync(cacheKey, serialized, TimeSpan.FromSeconds(CacheTtlSeconds));
                    await redisDb.StringSetAsync(staleKey, serialized, TimeSpan.FromSeconds(StaleGraceSeconds));

                    app.Logger.LogInformation("Cache refrescado en background para {Destination}", destination);
                }
                catch (Exception ex)
                {
                    app.Logger.LogWarning("Error refrescando cache en background: {Error}", ex.Message);
                }
            });

            // Devolvemos los datos stale inmediatamente — el usuario no espera
            return Results.Json(new
            {
                source = "cache_stale",
                destination,
                data = JsonSerializer.Deserialize<object>(stale.ToString())
            });
        }

        cacheMisses.WithLabels("/packages").Inc();
        app.Logger.LogInformation("Cache MISS para {Destination} — consultando proveedores", destination);
    }

    // Cache miss completo — consultamos proveedores y bloqueamos
    var client = factory.CreateClient();
    var fastProviderTask = client.GetStringAsync("http://providerservice:8080/availability");
    var slowProviderTask = client.GetStringAsync("http://providerservice:8080/availability/slow");
    await Task.WhenAll(fastProviderTask, slowProviderTask);

    var result = new
    {
        fast_provider = JsonSerializer.Deserialize<object>(fastProviderTask.Result),
        slow_provider = JsonSerializer.Deserialize<object>(slowProviderTask.Result)
    };

    if (redisDb != null)
    {
        var serialized = JsonSerializer.Serialize(result);
        // Guardamos tanto la clave fresca como la stale
        await redisDb.StringSetAsync(cacheKey, serialized, TimeSpan.FromSeconds(CacheTtlSeconds));
        await redisDb.StringSetAsync(staleKey, serialized, TimeSpan.FromSeconds(StaleGraceSeconds));
        app.Logger.LogInformation("Cache guardado para {Destination} (fresco: {Ttl}s, stale: {StaleTtl}s)",
            destination, CacheTtlSeconds, StaleGraceSeconds);
    }

    return Results.Json(new { source = "providers", destination, data = result });
});

app.MapPost("/bookings", async (BookingRequest request) =>
{
    if (rabbitChannel == null) return Results.Problem("RabbitMQ no disponible");
    var bookingId = Guid.NewGuid().ToString()[..8];
    var booking = request with { BookingId = bookingId };
    var envelope = new MessageEnvelope(booking, 0);
    var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));
    await rabbitChannel.BasicPublishAsync(exchange: "", routingKey: "booking-requests", body: body);
    app.Logger.LogInformation("Booking {BookingId} publicado en cola", bookingId);
    return Results.Accepted($"/bookings/{bookingId}", new { booking_id = bookingId, status = "processing", message = "Reserva recibida" });
});

app.MapGet("/bookings/{id}", async (string id) => {
    try
    {
        await using var conn = new NpgsqlConnection(pgConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT id, destination, passengers, status, result, created_at FROM bookings WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return Results.NotFound(new { message = $"Booking {id} no encontrado" });
        return Results.Ok(new { booking_id = reader.GetString(0), destination = reader.GetString(1), passengers = reader.GetInt32(2), status = reader.GetString(3), result = reader.IsDBNull(4) ? null : reader.GetString(4), created_at = reader.GetDateTime(5) });
    }
    catch (Exception ex) { return Results.Problem($"Error: {ex.Message}"); }
});

app.MapGet("/bookings", async () => {
    try
    {
        await using var conn = new NpgsqlConnection(pgConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT id, destination, passengers, status, created_at FROM bookings ORDER BY created_at DESC LIMIT 20", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var bookings = new List<object>();
        while (await reader.ReadAsync())
            bookings.Add(new { booking_id = reader.GetString(0), destination = reader.GetString(1), passengers = reader.GetInt32(2), status = reader.GetString(3), created_at = reader.GetDateTime(4) });
        return Results.Ok(bookings);
    }
    catch (Exception ex) { return Results.Problem($"Error: {ex.Message}"); }
});

app.MapGet("/leak", () => { leak.Add(new byte[1024 * 1024]); return Results.Ok(new { leaked_mb = leak.Count }); });
app.MapGet("/leak/reset", () => { leak.Clear(); GC.Collect(); return Results.Ok(new { message = "memoria liberada" }); });

app.Run();

public record BookingRequest(string BookingId, string Destination, int Passengers);
public record MessageEnvelope(BookingRequest Booking, int RetryCount = 0);
