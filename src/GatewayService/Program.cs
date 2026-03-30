using Prometheus;

// ============================================================
// GatewayService — punto de entrada único para todos los servicios
// Enruta las peticiones a los servicios correctos usando YARP
// ============================================================

var builder = WebApplication.CreateBuilder(args);

// YARP lee la configuración de rutas desde appsettings.json
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddControllers();

var app = builder.Build();

app.UseRouting();

// Métricas del gateway — cuántas peticiones pasan por cada ruta
app.UseHttpMetrics();
app.MapMetrics();

// YARP maneja todo el enrutamiento
app.MapReverseProxy();

app.Run();
