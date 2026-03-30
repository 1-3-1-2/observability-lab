using Prometheus;
using System.Text;
using System.Text.Json;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using Npgsql;

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
        app.Logger.LogWarning("Intento {Attempt}/10 fallido: {Error}. Reintentando en 5s...", attempt, ex.Message);
        await Task.Delay(5000);
    }
}

app.MapGet("/fast", () => Results.Ok(new { message = "respuesta rapida", ms = 10 }));
app.MapGet("/slow", async () => { await Task.Delay(Random.Shared.Next(200, 800)); return Results.Ok(new { message = "respuesta lenta" }); });
app.MapGet("/error", () => Random.Shared.Next(0, 2) == 0 ? Results.Problem("error aleatorio") : Results.Ok(new { message = "esta vez no fallo" }));

app.MapGet("/packages", async (IHttpClientFactory factory) => {
    var client = factory.CreateClient();
    var fastTask = client.GetStringAsync("http://providerservice:8080/availability");
    var slowTask = client.GetStringAsync("http://providerservice:8080/availability/slow");
    await Task.WhenAll(fastTask, slowTask);
    return Results.Json(new {
        message = "busqueda completada en paralelo",
        fast_provider = JsonSerializer.Deserialize<object>(fastTask.Result),
        slow_provider = JsonSerializer.Deserialize<object>(slowTask.Result)
    });
});

app.MapPost("/bookings", async (BookingRequest request) =>
{
    if (rabbitChannel == null) return Results.Problem("RabbitMQ no disponible");

    var bookingId = Guid.NewGuid().ToString()[..8];
    var booking = request with { BookingId = bookingId };

    // Publicamos usando el MessageEnvelope con RetryCount = 0
    var envelope = new MessageEnvelope(booking, 0);
    var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));

    await rabbitChannel.BasicPublishAsync(exchange: "", routingKey: "booking-requests", body: body);
    app.Logger.LogInformation("Booking {BookingId} publicado en cola", bookingId);

    return Results.Accepted($"/bookings/{bookingId}", new {
        booking_id = bookingId,
        status = "processing",
        message = "Reserva recibida, procesando en segundo plano"
    });
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
