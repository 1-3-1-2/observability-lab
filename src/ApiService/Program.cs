using Prometheus;
using System.Text;
using System.Text.Json;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMQ.Client;

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

var leak = new List<byte[]>();

// Intentamos conectar a RabbitMQ con reintentos
// RabbitMQ puede tardar hasta 30s en estar listo dentro de Docker
IConnection? rabbitConnection = null;
IChannel? rabbitChannel = null;

for (int attempt = 1; attempt <= 10; attempt++)
{
    try
    {
        var factory = new ConnectionFactory
        {
            HostName = "rabbitmq",
            UserName = "admin",
            Password = "admin"
        };
        rabbitConnection = await factory.CreateConnectionAsync();
        rabbitChannel = await rabbitConnection.CreateChannelAsync();

        await rabbitChannel.QueueDeclareAsync(
            queue: "booking-requests",
            durable: true,
            exclusive: false,
            autoDelete: false
        );

        app.Logger.LogInformation("Conectado a RabbitMQ en intento {Attempt}", attempt);
        break;
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("Intento {Attempt}/10 fallido: {Error}. Reintentando en 5s...",
            attempt, ex.Message);
        await Task.Delay(5000);
    }
}

app.MapGet("/fast", () => Results.Ok(new { message = "respuesta rapida", ms = 10 }));

app.MapGet("/slow", async () => {
    await Task.Delay(Random.Shared.Next(200, 800));
    return Results.Ok(new { message = "respuesta lenta" });
});

app.MapGet("/error", () => {
    if (Random.Shared.Next(0, 2) == 0)
        return Results.Problem("error aleatorio");
    return Results.Ok(new { message = "esta vez no fallo" });
});

app.MapGet("/packages", async (IHttpClientFactory factory) => {
    var client = factory.CreateClient();
    var fastTask = client.GetStringAsync("http://providerservice:8080/availability");
    var slowTask = client.GetStringAsync("http://providerservice:8080/availability/slow");
    await Task.WhenAll(fastTask, slowTask);
    var fastObj = JsonSerializer.Deserialize<object>(fastTask.Result);
    var slowObj = JsonSerializer.Deserialize<object>(slowTask.Result);
    return Results.Json(new {
        message = "busqueda completada en paralelo",
        fast_provider = fastObj,
        slow_provider = slowObj
    });
});

app.MapPost("/bookings", async (BookingRequest request) =>
{
    if (rabbitChannel == null)
        return Results.Problem("RabbitMQ no disponible");

    var bookingId = Guid.NewGuid().ToString()[..8];
    var booking = request with { BookingId = bookingId };

    var message = JsonSerializer.Serialize(booking);
    var body = Encoding.UTF8.GetBytes(message);

    await rabbitChannel.BasicPublishAsync(
        exchange: string.Empty,
        routingKey: "booking-requests",
        body: body
    );

    app.Logger.LogInformation("Booking {BookingId} publicado en cola", bookingId);

    return Results.Accepted($"/bookings/{bookingId}", new {
        booking_id = bookingId,
        status = "processing",
        message = "Reserva recibida, procesando en segundo plano"
    });
});

app.MapGet("/leak", () => {
    leak.Add(new byte[1024 * 1024]);
    var mb = leak.Count;
    return Results.Ok(new { leaked_mb = mb, message = $"Memoria acumulada: {mb}MB" });
});

app.MapGet("/leak/reset", () => {
    leak.Clear();
    GC.Collect();
    return Results.Ok(new { message = "memoria liberada" });
});

app.Run();

public record BookingRequest(string BookingId, string Destination, int Passengers);
