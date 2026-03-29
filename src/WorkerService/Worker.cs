using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

// ============================================================
// Worker — consumidor de mensajes de RabbitMQ
// Escucha la cola "booking-requests" y procesa cada mensaje
// en segundo plano sin bloquear la API principal
// ============================================================
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly HttpClient _httpClient;
    private IConnection? _connection;
    private IChannel? _channel;

    public Worker(ILogger<Worker> logger, IHttpClientFactory factory)
    {
        _logger = logger;
        _httpClient = factory.CreateClient();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Esperamos a que RabbitMQ esté listo antes de conectar
        await WaitForRabbitMQ(stoppingToken);

        var factory = new ConnectionFactory
        {
            HostName = "rabbitmq",
            UserName = "admin",
            Password = "admin"
        };

        _connection = await factory.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();

        // Declaramos la cola — si no existe la crea, si existe la reutiliza
        // durable: true — la cola sobrevive a reinicios de RabbitMQ
        await _channel.QueueDeclareAsync(
            queue: "booking-requests",
            durable: true,
            exclusive: false,
            autoDelete: false
        );

        // prefetchCount: 1 — el worker procesa un mensaje a la vez
        // Evita que se acumulen mensajes en un worker lento
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);

        _logger.LogInformation("Worker conectado a RabbitMQ. Esperando mensajes...");

        var consumer = new AsyncEventingBasicConsumer(_channel);

        consumer.ReceivedAsync += async (model, ea) =>
        {
            var body = ea.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);
            var booking = JsonSerializer.Deserialize<BookingRequest>(message);

            _logger.LogInformation("Procesando booking {BookingId} para {Destination}",
                booking?.BookingId, booking?.Destination);

            try
            {
                // Consultamos disponibilidad al ProviderService
                var fast = await _httpClient.GetStringAsync(
                    "http://providerservice:8080/availability");
                var slow = await _httpClient.GetStringAsync(
                    "http://providerservice:8080/availability/slow");

                _logger.LogInformation(
                    "Booking {BookingId} completado. Proveedor rapido: {Fast}",
                    booking?.BookingId, fast);

                // ACK — confirmamos que el mensaje fue procesado correctamente
                // RabbitMQ lo elimina de la cola
                await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando booking {BookingId}", booking?.BookingId);

                // NACK — rechazamos el mensaje, RabbitMQ lo reencola
                // requeue: true — vuelve al principio de la cola para reintentarlo
                await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
            }
        };

        await _channel.BasicConsumeAsync(
            queue: "booking-requests",
            autoAck: false, // Confirmación manual — más seguro que autoAck
            consumer: consumer
        );

        // Mantenemos el worker vivo hasta que se cancele
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    // Reintenta conectar a RabbitMQ hasta que esté disponible
    // RabbitMQ tarda unos segundos en arrancar dentro de Docker
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

// Modelo del mensaje que viaja por la cola
public record BookingRequest(string BookingId, string Destination, int Passengers);
