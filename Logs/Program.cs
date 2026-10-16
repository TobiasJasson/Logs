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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using System;
using System.IO;
using System.Text;
using System.Threading;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// Configuración de JWT (Emitido por Grupo 7 - Seguridad)
var jwtKey = builder.Configuration["Jwt:Key"] ?? "ClaveSecretaSuperSeguraAeropuertoUAI2026!";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = true,
            ValidAudience = "logs-api",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });
builder.Services.AddAuthorization();

// INVERSIÓN DE DEPENDENCIAS (DIP)
builder.Services.AddSingleton<LogsBusinessConfig>();
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
app.UseStaticFiles(); // Sirve wwwroot/index.html para las Pantallas Públicas y PopUps
app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();

// =========================================================================
// ENDPOINTS MINIMAL API (Contratos REST)
// =========================================================================

// 1. Consulta de Logs filtrados (Consumido por Grupo 9 - Panel de Administración)
app.MapGet("/api/logs", async (
    [FromQuery] DateTime? fechaDesde,
    [FromQuery] DateTime? fechaHasta,
    [FromQuery] string? origen,
    [FromQuery] string? tipo,
    [FromQuery] string? severidad,
    ConsultarLogsUseCase useCase) =>
{
    var filtro = new LogFilterDto
    {
        FechaDesde = fechaDesde,
        FechaHasta = fechaHasta,
        Origen = origen,
        TipoEvento = tipo,
        Severidad = severidad
    };
    var logs = await useCase.EjecutarAsync(filtro);
    return Results.Ok(logs);
});

// 2. Endpoint para invocación directa / SDK de otros grupos (Guarda en BD y dispara PopUp)
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

// 3. Simulador del Envelope estándar de RabbitMQ vía REST (Para facilitar pruebas a otros grupos)
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

// 4. Alimentación inicial de Pantallas Públicas (Salidas / Llegadas y eventos públicos)
app.MapGet("/api/pantallas/eventos", async (ConsultarLogsUseCase useCase) =>
{
    return Results.Ok(await useCase.ObtenerEventosPantallaPublicaAsync());
});

// 5. Canal Server-Sent Events (SSE) para tiempo real en Pantallas Públicas y PopUps
app.MapGet("/api/pantallas/stream", async (HttpContext context, SsePantallaNotifier notifier, CancellationToken ct) =>
{
    context.Response.Headers.Append("Content-Type", "text/event-stream");
    context.Response.Headers.Append("Cache-Control", "no-cache");
    context.Response.Headers.Append("Connection", "keep-alive");

    using var writer = new StreamWriter(context.Response.Body);
    var clientId = notifier.SuscribirCliente(writer);

    try
    {
        await writer.WriteAsync(": conectado\n\n");
        await writer.FlushAsync();
        await Task.Delay(Timeout.Infinite, ct);
    }
    catch (TaskCanceledException) { }
    finally
    {
        notifier.DesuscribirCliente(clientId);
    }
});

app.Run();