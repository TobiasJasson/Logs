using Aplication.Abstractions;
using Aplication.Config;
using Aplication.DTOs;
using Aplication.UseCases;
using Infrastructure.Messaging;
using Infrastructure.Notifications;
using Infrastructure.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// Configuración de JWT (Emitido por Grupo 7 - Seguridad)
var jwtKey = builder.Configuration["Jwt:Key"] ?? "ClaveSecretaSuperSeguraAeropuertoUAI2026!";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "logs-api";
bool requireJwt = builder.Configuration.GetValue<bool>("Jwt:RequireJwtInLogs");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });
builder.Services.AddAuthorization();

// Configuración de negocio desde appsettings.json
var businessConfig = new LogsBusinessConfig();
builder.Configuration.GetSection("BusinessConfig").Bind(businessConfig);
builder.Services.AddSingleton(businessConfig);

// INVERSIÓN DE DEPENDENCIAS (DIP)
builder.Services.AddSingleton<ILogRepository, MongoLogRepository>();
builder.Services.AddSingleton<SsePantallaNotifier>();
builder.Services.AddSingleton<IPantallaNotifier>(sp => sp.GetRequiredService<SsePantallaNotifier>());

// Casos de Uso
builder.Services.AddScoped<ProcesarEventoUseCase>();
builder.Services.AddScoped<ConsultarLogsUseCase>();

// Consumidor RabbitMQ en segundo plano
builder.Services.AddHostedService<RabbitMqConsumerHostedService>();

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();

// =========================================================================
// ENDPOINTS MINIMAL API
// =========================================================================

// 0. Healthcheck para Docker y API Gateway (Grupo 6)
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "Grupo8-LogsNotificaciones", timestamp = DateTime.UtcNow }));

// 1. Consulta de Logs filtrados y paginados (Consumido por Grupo 9 - Panel de Administración)
var logsGetEndpoint = app.MapGet("/api/logs", async (
    [FromQuery] DateTime? fechaDesde,
    [FromQuery] DateTime? fechaHasta,
    [FromQuery] string? origen,
    [FromQuery] string? tipo,
    [FromQuery] string? severidad,
    [FromQuery] int? page,
    [FromQuery] int? pageSize,
    ConsultarLogsUseCase useCase) =>
{
    var filtro = new LogFilterDto
    {
        FechaDesde = fechaDesde,
        FechaHasta = fechaHasta,
        Origen = origen,
        TipoEvento = tipo,
        Severidad = severidad,
        Page = page ?? 1,
        PageSize = pageSize ?? 50
    };
    var logs = await useCase.EjecutarAsync(filtro);
    return Results.Ok(logs);
});

if (requireJwt) logsGetEndpoint.RequireAuthorization();

// 2. Consulta de un Log puntual por ID (usado por Location de 201 Created)
var logByIdEndpoint = app.MapGet("/api/logs/{id:guid}", async (Guid id, ConsultarLogsUseCase useCase) =>
{
    var log = await useCase.ObtenerPorIdAsync(id);
    return log is not null ? Results.Ok(log) : Results.NotFound(new { error = "Log no encontrado." });
});
if (requireJwt) logByIdEndpoint.RequireAuthorization();

// 3. Resumen de métricas para el Dashboard del Panel de Administración (Grupo 9)
var resumenEndpoint = app.MapGet("/api/logs/resumen", async (ConsultarLogsUseCase useCase) =>
{
    return Results.Ok(await useCase.ObtenerResumenDashboardAsync());
});

if (requireJwt) resumenEndpoint.RequireAuthorization();

// 4. Ingesta directa / SDK de otros grupos (Guarda en BD y dispara PopUp)
app.MapPost("/api/logs", async ([FromBody] DirectLogRequestDto dto, ProcesarEventoUseCase useCase) =>
{
    try
    {
        var creado = await useCase.EjecutarDirectoAsync(dto);
        return Results.Created($"/api/logs/{creado.Id}", creado);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// 5. Recepción de Envelope estándar vía REST (Para pruebas de idempotencia sin RabbitMQ)
app.MapPost("/api/logs/envelope", async ([FromBody] BusEventEnvelopeDto envelope, ProcesarEventoUseCase useCase) =>
{
    try
    {
        bool procesado = await useCase.EjecutarDesdeBusAsync(envelope);
        if (!procesado)
            return Results.Ok(new { procesado = false, motivo = "Descartado por idempotencia (evento duplicado o antiguo)." });

        return Results.Ok(new { procesado = true, eventId = envelope.EventId });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// 6. Tablero consolidado de Vuelos (Salidas / Llegadas) para Pantallas Públicas
app.MapGet("/api/pantallas/vuelos", async (ConsultarLogsUseCase useCase) =>
{
    return Results.Ok(await useCase.ObtenerTableroVuelosAsync());
});

// 7. Últimos eventos públicos para Pantallas Públicas
app.MapGet("/api/pantallas/eventos", async (ConsultarLogsUseCase useCase) =>
{
    return Results.Ok(await useCase.ObtenerEventosPantallaPublicaAsync());
});

// 8. Canal SSE Público (Solo envía eventos donde EsPublico == true, sin datos sensibles)
app.MapGet("/api/pantallas/stream", async (HttpContext context, SsePantallaNotifier notifier, CancellationToken ct) =>
{
    await ManejarStreamSseAsync(context, notifier, soloPublicos: true, ct);
});

// 9. Canal SSE Interno (Envía todos los logs en vivo para monitoreo/bitácora interna)
app.MapGet("/api/logs/stream-interno", async (HttpContext context, SsePantallaNotifier notifier, CancellationToken ct) =>
{
    await ManejarStreamSseAsync(context, notifier, soloPublicos: false, ct);
});

static async Task ManejarStreamSseAsync(HttpContext context, SsePantallaNotifier notifier, bool soloPublicos, CancellationToken ct)
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.Headers["X-Accel-Buffering"] = "no"; // Evita buffering en Nginx / API Gateway

    var (clientId, reader) = notifier.SuscribirCliente(soloPublicos);
    await using var writer = new StreamWriter(context.Response.Body, leaveOpen: true);

    try
    {
        await writer.WriteAsync(": conectado\n\n");
        await writer.FlushAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            var readTask = reader.WaitToReadAsync(ct).AsTask();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15), ct);

            var completed = await Task.WhenAny(readTask, timeoutTask);
            if (completed == timeoutTask)
            {
                // Heartbeat cada 15s para mantener viva la conexión a través del Gateway
                await writer.WriteAsync(": heartbeat\n\n");
                await writer.FlushAsync(ct);
                continue;
            }

            if (await readTask)
            {
                while (reader.TryRead(out var json))
                {
                    await writer.WriteAsync($"data: {json}\n\n");
                }
                await writer.FlushAsync(ct);
            }
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        notifier.DesuscribirCliente(clientId);
    }
}

app.Run();