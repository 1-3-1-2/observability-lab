using Prometheus;
using System.Text.Json;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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

app.Run();
