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

        public ProcesarEventoUseCase(ILogRepository repository, IPantallaNotifier notifier, LogsBusinessConfig config)
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

            if (envelope.Timestamp == default)
                throw new ArgumentException("El timestamp de emisión es obligatorio para validar idempotencia.");

            // Truncamos a milisegundos para coincidir con la precisión de BSON Date en MongoDB
            var timestampTruncado = TruncarMilisegundos(envelope.Timestamp.ToUniversalTime());

            // 1. Control por EventId duplicado
            if (envelope.EventId != Guid.Empty && await _repository.ExistsEventIdAsync(envelope.EventId))
                return false;

            var ubicacionId = ExtraerIdentificadorUbicacion(envelope.Payload);

            // 2. Idempotencia del enunciado: Origen + Tipo + UbicacionId (para no pisar 2 vuelos distintos al mismo tiempo)
            var ultimoTimestamp = await _repository.GetLastTimestampAsync(envelope.Origen, envelope.Tipo, ubicacionId);
            if (ultimoTimestamp.HasValue && timestampTruncado <= TruncarMilisegundos(ultimoTimestamp.Value.ToUniversalTime()))
            {
                return false;
            }

            var severidad = DeterminarSeveridadAutomatica(envelope.Tipo);
            bool esPublico = _config.EsEventoPublico(envelope.Tipo);
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
                UbicacionId = ubicacionId,
                TimestampEmision = timestampTruncado,
                ExpiraEnUtc = DateTime.UtcNow.AddDays(_config.DiasRetencionLogs),
                EsPublico = esPublico,
                PayloadJson = payloadRaw
            };

            await _repository.SaveAsync(log);

            VueloPantalla? vueloActualizado = await ActualizarTableroVuelosSiCorrespondeAsync(envelope.Tipo, ubicacionId, severidad.ToString(), envelope.Payload);
            await _notifier.NotificarEventoAsync(log, vueloActualizado);
            return true;
        }

        // 2. Flujo para invocación directa vía API / Método compartido
        public async Task<LogEntry> EjecutarDirectoAsync(DirectLogRequestDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Origen) || string.IsNullOrWhiteSpace(dto.Mensaje))
                throw new ArgumentException("El componente de origen y el mensaje son obligatorios.");

            var ahoraTruncado = TruncarMilisegundos(DateTime.UtcNow);
            var severidadNorm = NormalizarSeveridad(dto.Severidad);
            var ubicacion = string.IsNullOrWhiteSpace(dto.UbicacionId) ? "General" : dto.UbicacionId;

            var log = new LogEntry
            {
                EventId = Guid.NewGuid(),
                Origen = dto.Origen,
                TipoEvento = string.IsNullOrWhiteSpace(dto.TipoEvento) ? "LogDirecto" : dto.TipoEvento,
                Severidad = severidadNorm,
                Mensaje = dto.Mensaje,
                UbicacionId = ubicacion,
                TimestampEmision = ahoraTruncado,
                ExpiraEnUtc = ahoraTruncado.AddDays(_config.DiasRetencionLogs),
                EsPublico = dto.MostrarEnPantallaPublica || _config.EsEventoPublico(dto.TipoEvento),
                PayloadJson = JsonSerializer.Serialize(dto)
            };

            await _repository.SaveAsync(log);

            VueloPantalla? vueloActualizado = null;
            if (!string.IsNullOrWhiteSpace(dto.EstadoVuelo) || dto.TipoEvento.Contains("Vuelo", StringComparison.OrdinalIgnoreCase))
            {
                vueloActualizado = new VueloPantalla
                {
                    VueloId = ubicacion,
                    TipoOperacion = dto.TipoOperacion ?? "Salida",
                    AerolineaOOrigen = dto.Origen,
                    Destino = dto.Destino ?? "Nacional / Internacional",
                    Horario = dto.Horario ?? ahoraTruncado.ToLocalTime().ToString("HH:mm"),
                    Puerta = dto.Puerta ?? "A1",
                    Estado = dto.EstadoVuelo ?? severidadNorm,
                    SeveridadVisual = severidadNorm,
                    UltimaActualizacionUtc = ahoraTruncado
                };
                await _repository.UpsertVueloPantallaAsync(vueloActualizado);
            }

            await _notifier.NotificarEventoAsync(log, vueloActualizado);
            return log;
        }

        private async Task<VueloPantalla?> ActualizarTableroVuelosSiCorrespondeAsync(
            string tipoEvento, string ubicacionId, string severidad, JsonElement payload)
        {
            if (payload.ValueKind != JsonValueKind.Object) return null;

            bool tieneVuelo = payload.TryGetProperty("vueloId", out var vProp);
            if (!tieneVuelo && !tipoEvento.Contains("Vuelo", StringComparison.OrdinalIgnoreCase))
                return null;

            string vueloId = tieneVuelo ? vProp.ToString() : ubicacionId;
            if (string.IsNullOrWhiteSpace(vueloId) || vueloId == "Sistema") return null;

            string destino = payload.TryGetProperty("destino", out var d) ? d.ToString() : "Aeropuerto Destino";
            string origenVuelo = payload.TryGetProperty("origen", out var o) ? o.ToString() : "EZE";
            string horario = payload.TryGetProperty("horario", out var h) ? h.ToString() : DateTime.Now.ToString("HH:mm");
            string puerta = payload.TryGetProperty("puerta", out var p) ? p.ToString() : "B2";
            string pista = payload.TryGetProperty("pista", out var pi) ? pi.ToString() : "09L";
            string tipoOp = payload.TryGetProperty("tipoOperacion", out var top) ? top.ToString() : "Salida";
            string estado = payload.TryGetProperty("estadoNuevo", out var est) ? est.ToString()
                          : (payload.TryGetProperty("estado", out var est2) ? est2.ToString() : tipoEvento);

            var vuelo = new VueloPantalla
            {
                VueloId = vueloId,
                TipoOperacion = tipoOp,
                AerolineaOOrigen = origenVuelo,
                Destino = destino,
                Horario = horario,
                Puerta = puerta,
                Pista = pista,
                Estado = estado,
                SeveridadVisual = severidad,
                UltimaActualizacionUtc = DateTime.UtcNow
            };

            await _repository.UpsertVueloPantallaAsync(vuelo);
            return vuelo;
        }

        private static DateTime TruncarMilisegundos(DateTime dt)
        {
            return new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, dt.Millisecond, DateTimeKind.Utc);
        }

        private NivelSeveridad DeterminarSeveridadAutomatica(string tipoEvento)
        {
            var t = tipoEvento.ToLowerInvariant();
            if (t.Contains("error") || t.Contains("denegada") || t.Contains("bloqueada") || t.Contains("timeout"))
                return NivelSeveridad.Error;
            if (t.Contains("alerta") || t.Contains("ocupada") || t.Contains("demora") || t.Contains("viento"))
                return NivelSeveridad.Alerta;
            if (t.Contains("otorgada") || t.Contains("liberada") || t.Contains("creado") || t.Contains("arribado") || t.Contains("aprobado"))
                return NivelSeveridad.Aprobado;

            return NivelSeveridad.Informacion;
        }

        private string NormalizarSeveridad(string sev)
        {
            return sev?.Trim().ToLowerInvariant() switch
            {
                "error" => NivelSeveridad.Error.ToString(),
                "alerta" => NivelSeveridad.Alerta.ToString(),
                "aprobado" => NivelSeveridad.Aprobado.ToString(),
                _ => NivelSeveridad.Informacion.ToString()
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