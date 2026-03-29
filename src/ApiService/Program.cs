using Prometheus;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddHttpClient();

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
    
    var fast = await client.GetStringAsync("http://providerservice:8080/availability");
    var slow = await client.GetStringAsync("http://providerservice:8080/availability/slow");
    
    var fastObj = JsonSerializer.Deserialize<object>(fast);
    var slowObj = JsonSerializer.Deserialize<object>(slow);
    
    return Results.Json(new { 
        message = "busqueda completada",
        fast_provider = fastObj,
        slow_provider = slowObj
    });
});

app.Run();
