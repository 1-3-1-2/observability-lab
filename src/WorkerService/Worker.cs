using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;
using Npgsql;
using Polly;
using Polly.CircuitBreaker;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _connectionString;
    private IConnection? _connection;
    private IChannel? _channel;

    private readonly ResiliencePipeline _fastProviderCB;
    private readonly ResiliencePipeline _slowProviderCB;

    private string _fastCBState = "closed";
    private string _slowCBState = "closed";

    public Worker(ILogger<Worker> logger, IHttpClientFactory factory, IConfiguration config)
    {
        _logger = logger;
        _httpClient = factory.CreateClient();
        _connectionString = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Connection string 'Postgres' not found");

        _fastProviderCB = BuildCircuitBreaker("fastprovider",
            onOpen: () => _fastCBState = "open",
            onClose: () => _fastCBState = "closed",
            onHalfOpen: () => _fastCBState = "half-open");

        _slowProviderCB = BuildCircuitBreaker("slowprovider",
            onOpen: () => _slowCBState = "open",
            onClose: () => _slowCBState = "closed",
            onHalfOpen: () => _slowCBState = "half-open");
    }

    private ResiliencePipeline BuildCircuitBreaker(
        string name,
        Action onOpen,
        Action onClose,
        Action onHalfOpen)
    {
        return new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 3,
                BreakDuration = TimeSpan.FromSeconds(30),
                SamplingDuration = TimeSpan.FromSeconds(60),

                // En Polly v8 usamos ShouldHandle con Handle<Exception>
                // y verificamos el status code dentro del ExecuteAsync
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),

                OnOpened = args =>
                {
                    onOpen();
                    _logger.LogWarning("CIRCUIT BREAKER ABIERTO — {Name}", name);
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    onClose();
                    _logger.LogInformation("CIRCUIT BREAKER CERRADO — {Name} recuperado", name);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    onHalfOpen();
                    _logger.LogInformation("CIRCUIT BREAKER HALF-OPEN — probando {Name}", name);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

            _logger.LogInformation("Procesando booking {BookingId}", booking.BookingId);
            await SaveBooking(booking, "processing", null);

            string fastResult = "{\"provider\":\"amadeus\",\"available\":false,\"reason\":\"circuit_breaker_open\"}";
            try
            {
                await _fastProviderCB.ExecuteAsync(async ct =>
                {
                    var response = await _httpClient.GetAsync(
                        "http://providerservice:8080/availability", ct);

                    // Lanzamos excepción si es 5xx para que Polly lo cuente como fallo
                    if ((int)response.StatusCode >= 500)
                        throw new HttpRequestException($"Server error: {response.StatusCode}");

                    fastResult = await response.Content.ReadAsStringAsync(ct);
                }, stoppingToken);
            }
            catch (BrokenCircuitException)
            {
                _logger.LogWarning("Booking {BookingId}: fastprovider cortado por CB", booking.BookingId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Booking {BookingId}: fastprovider error: {Error}", booking.BookingId, ex.Message);
            }

            string slowResult = "{\"provider\":\"slowprovider\",\"available\":false,\"reason\":\"circuit_breaker_open\"}";
            try
            {
                await _slowProviderCB.ExecuteAsync(async ct =>
                {
                    var response = await _httpClient.GetAsync(
                        "http://providerservice:8080/availability/slow", ct);

                    // Lanzamos excepción si es 5xx para que Polly lo cuente como fallo
                    if ((int)response.StatusCode >= 500)
                        throw new HttpRequestException($"Server error: {response.StatusCode}");

                    slowResult = await response.Content.ReadAsStringAsync(ct);
                }, stoppingToken);
            }
            catch (BrokenCircuitException)
            {
                _logger.LogWarning("Booking {BookingId}: slowprovider cortado por CB", booking.BookingId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Booking {BookingId}: slowprovider error: {Error}", booking.BookingId, ex.Message);
            }

            var result = JsonSerializer.Serialize(new
            {
                fast_cb_state = _fastCBState,
                slow_cb_state = _slowCBState,
                fast_provider = JsonSerializer.Deserialize<object>(fastResult),
                slow_provider = JsonSerializer.Deserialize<object>(slowResult)
            });

            await UpdateBooking(booking.BookingId, "completed", result);

            _logger.LogInformation("Booking {BookingId} completado. Fast CB: {FastState}, Slow CB: {SlowState}",
                booking.BookingId, _fastCBState, _slowCBState);

            await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
        };

        await _channel.BasicConsumeAsync(
            queue: "booking-requests",
            autoAck: false,
            consumer: consumer
        );

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
                        id          TEXT PRIMARY KEY,
                        destination TEXT NOT NULL,
                        passengers  INT  NOT NULL,
                        status      TEXT NOT NULL,
                        result      TEXT,
                        created_at  TIMESTAMPTZ DEFAULT NOW(),
                        updated_at  TIMESTAMPTZ DEFAULT NOW()
                    )", conn);
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
