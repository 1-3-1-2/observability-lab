// ============================================================
// WorkerService — proceso en segundo plano que consume mensajes
// de RabbitMQ y procesa reservas de forma asíncrona
// ============================================================

var builder = Host.CreateApplicationBuilder(args);

// Registramos HttpClient para llamar a ProviderService
builder.Services.AddHttpClient();

// Registramos el Worker como servicio en background
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
