var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHttpClient(string.Empty, client =>
{
    // Timeout global del cliente — corta cualquier llamada que tarde más de 3s
    client.Timeout = TimeSpan.FromSeconds(3);
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
