using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;
using Npgsql;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _connectionString;
    private IConnection? _connection;
    private IChannel? _channel;
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 10000;

    public Worker(ILogger<Worker> logger, IHttpClientFactory factory, IConfiguration config)
    {
        _logger = logger;
        _httpClient = factory.CreateClient();
        _connectionString = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Connection string 'Postgres' not found");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await InitializeDatabase(stoppingToken);
        await WaitForRabbitMQ(stoppingToken);

        var factory = new ConnectionFactory { HostName = "rabbitmq", UserName = "admin", Password = "admin" };
        _connection = await factory.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();

        // Cola DLQ — destino final de mensajes que fallaron demasiadas veces
        await _channel.QueueDeclareAsync("booking-requests.dlq", durable: true, exclusive: false, autoDelete: false);

        // Cola retry — los mensajes esperan aquí antes de volver a la cola principal
        var retryArgs = new Dictionary<string, object?>
        {
            { "x-dead-letter-exchange", "" },
            { "x-dead-letter-routing-key", "booking-requests" },
            { "x-message-ttl", RetryDelayMs }
        };
        await _channel.QueueDeclareAsync("booking-requests.retry", durable: true, exclusive: false, autoDelete: false, arguments: retryArgs);

        // Cola principal
        await _channel.QueueDeclareAsync("booking-requests", durable: true, exclusive: false, autoDelete: false);

        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);
        _logger.LogInformation("Worker conectado. Patrón retry + DLQ configurado");

        var consumer = new AsyncEventingBasicConsumer(_channel);

        consumer.ReceivedAsync += async (model, ea) =>
        {
            var body = ea.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);

            // Deserializamos el envelope que incluye el contador de reintentos
            var envelope = JsonSerializer.Deserialize<MessageEnvelope>(message);
            if (envelope == null)
            {
                await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                return;
            }

            var booking = envelope.Booking;
            var retryCount = envelope.RetryCount;

            _logger.LogInformation("Procesando booking {BookingId} (intento {Retry}/{Max})",
                booking.BookingId, retryCount + 1, MaxRetries);

            // Si superó el máximo → DLQ
            if (retryCount >= MaxRetries)
            {
                _logger.LogError("Booking {BookingId} → DLQ después de {Max} intentos",
                    booking.BookingId, MaxRetries);
                await UpdateBooking(booking.BookingId, "dead_lettered",
                    $"Falló {MaxRetries} veces consecutivas");

                // Publicamos en la DLQ para inspección
                await _channel.BasicPublishAsync(
                    exchange: "",
                    routingKey: "booking-requests.dlq",
                    body: body
                );

                await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                return;
            }

            await SaveBooking(booking, "processing", null);

            try
            {
                // Proveedor rápido
                var fastResponse = await _httpClient.GetAsync("http://providerservice:8080/availability");
                if ((int)fastResponse.StatusCode >= 500)
                    throw new HttpRequestException($"fastprovider error: {fastResponse.StatusCode}");
                var fastResult = await fastResponse.Content.ReadAsStringAsync();

                // Proveedor lento — sin circuit breaker para ver la DLQ
                var slowResponse = await _httpClient.GetAsync("http://providerservice:8080/availability/slow");
                if ((int)slowResponse.StatusCode >= 500)
                    throw new HttpRequestException($"slowprovider error: {slowResponse.StatusCode}");
                var slowResult = await slowResponse.Content.ReadAsStringAsync();

                var result = JsonSerializer.Serialize(new
                {
                    fast_provider = JsonSerializer.Deserialize<object>(fastResult),
                    slow_provider = JsonSerializer.Deserialize<object>(slowResult)
                });

                await UpdateBooking(booking.BookingId, "completed", result);
                _logger.LogInformation("Booking {BookingId} completado", booking.BookingId);
                await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Booking {BookingId} falló (intento {Retry}/{Max}): {Error}",
                    booking.BookingId, retryCount + 1, MaxRetries, ex.Message);

                await UpdateBooking(booking.BookingId, "retrying",
                    $"Intento {retryCount + 1}: {ex.Message}");

                // Incrementamos el contador y mandamos a la cola retry
                var retryEnvelope = new MessageEnvelope(booking, retryCount + 1);
                var retryBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(retryEnvelope));

                await _channel.BasicPublishAsync(
                    exchange: "",
                    routingKey: "booking-requests.retry",
                    body: retryBody
                );

                _logger.LogWarning("Booking {BookingId} → retry en {Delay}s (intento {Retry}/{Max})",
                    booking.BookingId, RetryDelayMs / 1000, retryCount + 1, MaxRetries);

                await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            }
        };

        await _channel.BasicConsumeAsync(queue: "booking-requests", autoAck: false, consumer: consumer);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task InitializeDatabase(CancellationToken stoppingToken)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(stoppingToken);
                await using var cmd = new NpgsqlCommand(@"
                    CREATE TABLE IF NOT EXISTS bookings (
                        id TEXT PRIMARY KEY, destination TEXT NOT NULL,
                        passengers INT NOT NULL, status TEXT NOT NULL,
                        result TEXT, created_at TIMESTAMPTZ DEFAULT NOW(),
                        updated_at TIMESTAMPTZ DEFAULT NOW())", conn);
                await cmd.ExecuteNonQueryAsync(stoppingToken);
                _logger.LogInformation("Base de datos inicializada");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Postgres no disponible ({Attempt}/10): {Error}", i + 1, ex.Message);
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task SaveBooking(BookingRequest booking, string status, string? result)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO bookings (id, destination, passengers, status, result)
            VALUES (@id, @destination, @passengers, @status, @result)
            ON CONFLICT (id) DO NOTHING", conn);
        cmd.Parameters.AddWithValue("id", booking.BookingId);
        cmd.Parameters.AddWithValue("destination", booking.Destination);
        cmd.Parameters.AddWithValue("passengers", booking.Passengers);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("result", result ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task UpdateBooking(string bookingId, string status, string? result)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE bookings SET status=@status, result=@result, updated_at=NOW() WHERE id=@id", conn);
        cmd.Parameters.AddWithValue("id", bookingId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("result", result ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task WaitForRabbitMQ(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory { HostName = "rabbitmq", UserName = "admin", Password = "admin" };
        for (int i = 0; i < 10; i++)
        {
            try { using var conn = await factory.CreateConnectionAsync(); _logger.LogInformation("RabbitMQ disponible"); return; }
            catch { _logger.LogWarning("RabbitMQ no disponible, reintentando en 5s..."); await Task.Delay(5000, stoppingToken); }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel != null) await _channel.CloseAsync();
        if (_connection != null) await _connection.CloseAsync();
        await base.StopAsync(cancellationToken);
    }
}

public record BookingRequest(string BookingId, string Destination, int Passengers);

// Envelope que envuelve el mensaje con metadata de reintentos
public record MessageEnvelope(BookingRequest Booking, int RetryCount = 0);
