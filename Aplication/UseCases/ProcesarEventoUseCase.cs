using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Aplication.Abstractions;
using Aplication.Config;
using Aplication.DTOs;
using Domain.Entities;
using Domain.Enums;

namespace Aplication.UseCases
{
    public class ProcesarEventoUseCase
    {
        private readonly ILogRepository _repository;
        private readonly IPantallaNotifier _notifier;
        private readonly LogsBusinessConfig _config;

        public ProcesarEventoUseCase(
            ILogRepository repository,
            IPantallaNotifier notifier,
            LogsBusinessConfig config)
        {
            _repository = repository;
            _notifier = notifier;
            _config = config;
        }

        // 1. Flujo para eventos entrantes desde RabbitMQ (Envelope Estándar)
        public async Task<bool> EjecutarDesdeBusAsync(BusEventEnvelopeDto envelope)
        {
            if (string.IsNullOrWhiteSpace(envelope.Origen) || string.IsNullOrWhiteSpace(envelope.Tipo))
                throw new ArgumentException("El origen y el tipo de evento son obligatorios.");

            // Requerimiento No Funcional: Idempotencia por Timestamp, Origen y Tipo
            var ultimoTimestamp = await _repository.GetLastTimestampAsync(envelope.Origen, envelope.Tipo);
            if (ultimoTimestamp.HasValue && envelope.Timestamp <= ultimoTimestamp.Value)
            {
                return false; // Se descarta por duplicado o llegada fuera de orden
            }

            var severidad = DeterminarSeveridadAutomatica(envelope.Tipo);
            bool esPublico = _config.EventosPublicos.Contains(envelope.Tipo);
            string payloadRaw = envelope.Payload.ValueKind != JsonValueKind.Undefined
                ? envelope.Payload.GetRawText()
                : "{}";

            var log = new LogEntry
            {
                EventId = envelope.EventId == Guid.Empty ? Guid.NewGuid() : envelope.EventId,
                Origen = envelope.Origen,
                TipoEvento = envelope.Tipo,
                Severidad = severidad.ToString(),
                Mensaje = ConstruirMensajeLegible(envelope.Origen, envelope.Tipo, envelope.Payload),
                UbicacionId = ExtraerIdentificadorUbicacion(envelope.Payload),
                TimestampEmision = envelope.Timestamp,
                ExpiraEnUtc = DateTime.UtcNow.AddDays(_config.DiasRetencionLogs),
                EsPublico = esPublico,
                PayloadJson = payloadRaw
            };

            await _repository.SaveAsync(log);
            await _notifier.NotificarEventoAsync(log); // Envía a Pantallas y PopUps en vivo
            return true;
        }

        // 2. Flujo para invocación directa vía API / Método compartido
        public async Task<LogEntry> EjecutarDirectoAsync(DirectLogRequestDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Origen) || string.IsNullOrWhiteSpace(dto.Mensaje))
                throw new ArgumentException("El componente de origen y el mensaje son obligatorios.");

            var log = new LogEntry
            {
                EventId = Guid.NewGuid(),
                Origen = dto.Origen,
                TipoEvento = string.IsNullOrWhiteSpace(dto.TipoEvento) ? "LogDirecto" : dto.TipoEvento,
                Severidad = NormalizarSeveridad(dto.Severidad),
                Mensaje = dto.Mensaje,
                UbicacionId = string.IsNullOrWhiteSpace(dto.UbicacionId) ? "General" : dto.UbicacionId,
                TimestampEmision = DateTime.UtcNow,
                ExpiraEnUtc = DateTime.UtcNow.AddDays(_config.DiasRetencionLogs),
                EsPublico = dto.MostrarEnPantallaPublica || _config.EventosPublicos.Contains(dto.TipoEvento),
                PayloadJson = JsonSerializer.Serialize(dto)
            };

            await _repository.SaveAsync(log);
            await _notifier.NotificarEventoAsync(log);
            return log;
        }

        private Severidad DeterminarSeveridadAutomatica(string tipoEvento)
        {
            var t = tipoEvento.ToLowerInvariant();
            if (t.Contains("error") || t.Contains("denegada") || t.Contains("bloqueada") || t.Contains("timeout"))
                return Severidad.Error;
            if (t.Contains("alerta") || t.Contains("ocupada") || t.Contains("demora") || t.Contains("viento"))
                return Severidad.Alerta;
            if (t.Contains("otorgada") || t.Contains("liberada") || t.Contains("creado") || t.Contains("arribado") || t.Contains("aprobado"))
                return Severidad.Aprobado;

            return Severidad.Informacion;
        }

        private string NormalizarSeveridad(string sev)
        {
            return sev?.Trim().ToLowerInvariant() switch
            {
                "error" => Severidad.Error.ToString(),
                "alerta" => Severidad.Alerta.ToString(),
                "aprobado" => Severidad.Aprobado.ToString(),
                _ => Severidad.Informacion.ToString()
            };
        }

        private string ExtraerIdentificadorUbicacion(JsonElement payload)
        {
            if (payload.ValueKind == JsonValueKind.Object)
            {
                if (payload.TryGetProperty("vueloId", out var v)) return v.ToString();
                if (payload.TryGetProperty("pista", out var p)) return $"Pista {p}";
                if (payload.TryGetProperty("ubicacionId", out var u)) return u.ToString();
            }
            return "Sistema";
        }

        private string ConstruirMensajeLegible(string origen, string tipo, JsonElement payload)
        {
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("mensaje", out var msg))
                return msg.ToString();

            var id = ExtraerIdentificadorUbicacion(payload);
            return $"[{origen}] Evento '{tipo}' registrado sobre {id}.";
        }
    }
}
