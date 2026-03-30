using Prometheus;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;

// ============================================================
// GatewayService — punto de entrada único
// Responsabilidades: autenticación JWT, rate limiting, enrutamiento
// ============================================================

var builder = WebApplication.CreateBuilder(args);

// Clave secreta para firmar y validar tokens JWT
// En producción esto va en variables de entorno o un vault
var jwtSecret = "observability-lab-secret-key-2026-muy-larga-para-ser-segura";
var jwtKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

// Configuración de autenticación JWT
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = "observability-lab",
            ValidAudience = "observability-lab-clients",
            IssuerSigningKey = jwtKey
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        context.HttpContext.Response.Headers["Retry-After"] = "60";
        await context.HttpContext.Response.WriteAsync(
            "{\"error\":\"Too many requests\",\"retry_after_seconds\":60}",
            cancellationToken);
    };

    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (context.Request.Path.StartsWithSegments("/api/bookings") &&
                context.Request.Method == "POST")
            {
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"bookings:{ip}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    });
            }
            return RateLimitPartition.GetFixedWindowLimiter(
                $"global:{ip}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 5
                });
        }),
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(
                $"burst:{ip}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromSeconds(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                });
        })
    );
});

var app = builder.Build();

app.UseRouting();
app.UseHttpMetrics();
app.UseRateLimiter();

// Autenticación y autorización deben ir antes del proxy
app.UseAuthentication();
app.UseAuthorization();

app.MapMetrics();

// Endpoint público — genera un JWT token
// En producción validaría usuario y contraseña contra una BD
app.MapPost("/auth/login", (LoginRequest request) =>
{
    // Usuarios de ejemplo — en producción estarían en BD con passwords hasheados
    var validUsers = new Dictionary<string, (string password, string role)>
    {
        { "admin", ("admin123", "admin") },
        { "user", ("user123", "user") }
    };

    if (!validUsers.TryGetValue(request.Username, out var userData) ||
        userData.password != request.Password)
    {
        return Results.Unauthorized();
    }

    // Creamos el token JWT con claims del usuario
    var claims = new[]
    {
        new Claim(ClaimTypes.Name, request.Username),
        new Claim(ClaimTypes.Role, userData.role),
        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        new Claim(JwtRegisteredClaimNames.Iat,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
    };

    var token = new JwtSecurityToken(
        issuer: "observability-lab",
        audience: "observability-lab-clients",
        claims: claims,
        expires: DateTime.UtcNow.AddHours(1),
        signingCredentials: new SigningCredentials(jwtKey, SecurityAlgorithms.HmacSha256)
    );

    var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

    return Results.Ok(new
    {
        token = tokenString,
        expires_in = 3600,
        token_type = "Bearer",
        user = request.Username,
        role = userData.role
    });
}).AllowAnonymous();

// Endpoint público — verifica si un token es válido
app.MapGet("/auth/verify", (HttpContext context) =>
{
    var user = context.User;
    if (!user.Identity?.IsAuthenticated ?? true)
        return Results.Unauthorized();

    return Results.Ok(new
    {
        valid = true,
        user = user.Identity?.Name,
        role = user.FindFirst(ClaimTypes.Role)?.Value
    });
}).RequireAuthorization();

// El proxy requiere autenticación para todas las rutas
app.MapReverseProxy(proxyPipeline =>
{
    proxyPipeline.Use(async (context, next) =>
    {
        // Verificamos que el usuario está autenticado
        if (!context.User.Identity?.IsAuthenticated ?? true)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync(
                "{\"error\":\"Unauthorized\",\"message\":\"Token JWT requerido\"}");
            return;
        }

        // Propagamos el usuario al servicio downstream via header
        var username = context.User.Identity?.Name;
        context.Request.Headers["X-User"] = username;
        context.Request.Headers["X-User-Role"] = context.User.FindFirst(ClaimTypes.Role)?.Value;

        await next();
    });
});

app.Run();

public record LoginRequest(string Username, string Password);
