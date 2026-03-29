using Prometheus;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();

var app = builder.Build();

app.UseRouting();
app.UseHttpMetrics();
app.MapMetrics();
app.MapControllers();

app.MapGet("/fast", () => Results.Ok(new { message = "respuesta rápida", ms = 10 }));

app.MapGet("/slow", async () =>
{
    await Task.Delay(Random.Shared.Next(200, 800));
    return Results.Ok(new { message = "respuesta lenta" });
});

app.MapGet("/error", () =>
{
    if (Random.Shared.Next(0, 2) == 0)
        return Results.Problem("error aleatorio");
    return Results.Ok(new { message = "esta vez no falló" });
});

app.Run();