using Prometheus;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddHttpClient();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("providerservice"))
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

var degradation = 0;

app.MapGet("/degrade", () => {
    degradation += 200;
    return Results.Ok(new { degradation_ms = degradation });
});

app.MapGet("/availability", async () => {
    await Task.Delay(Random.Shared.Next(50, 300) + degradation);
    return Results.Ok(new { 
        provider = "amadeus",
        available = true,
        price = Random.Shared.Next(200, 1500)
    });
});

app.MapGet("/availability/slow", async () => {
    await Task.Delay(Random.Shared.Next(1000, 3000) + degradation);
    return Results.Ok(new { 
        provider = "slowprovider",
        available = true,
        price = Random.Shared.Next(200, 1500)
    });
});

app.MapGet("/reset", () => {
    degradation = 0;
    return Results.Ok(new { degradation_ms = degradation });
});

app.Run();
