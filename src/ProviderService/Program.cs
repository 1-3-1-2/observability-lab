using Prometheus;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseRouting();
app.UseHttpMetrics();
app.MapMetrics();
app.MapControllers();

app.MapGet("/availability", async () => {
    await Task.Delay(Random.Shared.Next(50, 300));
    return Results.Ok(new { 
        provider = "amadeus",
        available = true,
        price = Random.Shared.Next(200, 1500)
    });
});

app.MapGet("/availability/slow", async () => {
    await Task.Delay(Random.Shared.Next(1000, 3000));
    return Results.Ok(new { 
        provider = "slowprovider",
        available = true,
        price = Random.Shared.Next(200, 1500)
    });
});

app.Run();
