using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;
using Npgsql;

// ============================================================
// Worker — consumidor de mensajes de RabbitMQ
// Procesa reservas y guarda el resultado en PostgreSQL
// ============================================================
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _connectionString;
    private IConnection? _connection;
    private IChannel? _channel;

    public Worker(ILogger<Worker> logger, IHttpClientFactory factory, IConfiguration config)
    {
        _logger = logger;
        _httpClient = factory.CreateClient();
        _connectionString = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Connection string 'Postgres' not found");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Esperamos a que PostgreSQL y RabbitMQ estén listos
        await InitializeDatabase(stoppingToken);
        await WaitForRabbitMQ(stoppingToken);

        var factory = new ConnectionFactory
        {
            HostName = "rabbitmq",
            UserName = "admin",
            Password = "admin"
        };

        _connection = await factory.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();

        await _channel.QueueDeclareAsync(
            queue: "booking-requests",
            durable: true,
            exclusive: false,
            autoDelete: false
        );

        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);

        _logger.LogInformation("Worker conectado. Esperando mensajes...");

        var consumer = new AsyncEventingBasicConsumer(_channel);

        consumer.ReceivedAsync += async (model, ea) =>
        {
            var body = ea.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);
            var booking = JsonSerializer.Deserialize<BookingRequest>(message);

            if (booking == null)
            {
                await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                return;
            }

            _logger.LogInformation("Procesando booking {BookingId} para {Destination}",
                booking.BookingId, booking.Destination);

            try
            {
                // Guardamos la reserva en BD con estado "processing"
                await SaveBooking(booking, "processing", null);

                // Consultamos disponibilidad en paralelo
                var fastTask = _httpClient.GetStringAsync("http://providerservice:8080/availability");
                var slowTask = _httpClient.GetStringAsync("http://providerservice:8080/availability/slow");
                await Task.WhenAll(fastTask, slowTask);

                var result = JsonSerializer.Serialize(new
                {
                    fast_provider = JsonSerializer.Deserialize<object>(fastTask.Result),
                    slow_provider = JsonSerializer.Deserialize<object>(slowTask.Result)
                });

                // Actualizamos la reserva con estado "completed" y el resultado
                await UpdateBooking(booking.BookingId, "completed", result);

                _logger.LogInformation("Booking {BookingId} completado y guardado en BD",
                    booking.BookingId);

                await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando booking {BookingId}", booking.BookingId);
                await UpdateBooking(booking.BookingId, "failed", ex.Message);
                await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
            }
        };

        await _channel.BasicConsumeAsync(
            queue: "booking-requests",
            autoAck: false,
            consumer: consumer
        );

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    // Crea la tabla de reservas si no existe
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
                        id          TEXT PRIMARY KEY,
                        destination TEXT NOT NULL,
                        passengers  INT  NOT NULL,
                        status      TEXT NOT NULL,
                        result      TEXT,
                        created_at  TIMESTAMPTZ DEFAULT NOW(),
                        updated_at  TIMESTAMPTZ DEFAULT NOW()
                    )", conn);

                await cmd.ExecuteNonQueryAsync(stoppingToken);
                _logger.LogInformation("Base de datos inicializada correctamente");
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
            UPDATE bookings
            SET status = @status, result = @result, updated_at = NOW()
            WHERE id = @id", conn);

        cmd.Parameters.AddWithValue("id", bookingId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("result", result ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task WaitForRabbitMQ(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = "rabbitmq",
            UserName = "admin",
            Password = "admin"
        };

        for (int i = 0; i < 10; i++)
        {
            try
            {
                using var conn = await factory.CreateConnectionAsync();
                _logger.LogInformation("RabbitMQ disponible");
                return;
            }
            catch
            {
                _logger.LogWarning("RabbitMQ no disponible, reintentando en 5s...");
                await Task.Delay(5000, stoppingToken);
            }
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
