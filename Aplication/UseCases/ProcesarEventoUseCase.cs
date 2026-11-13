using Aplication.Abstractions;
using Aplication.Config;
using Aplication.DTOs;
using Domain.Entities;
using Domain.Enums;
using System;
using System.Text.Json;
using System.Threading.Tasks;

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

        public async Task<bool> EjecutarDesdeBusAsync(BusEventEnvelopeDto envelope)
        {
            if (envelope is null)
                throw new ArgumentException("El cuerpo del evento no puede ser nulo.");

            if (string.IsNullOrWhiteSpace(envelope.Origen) || string.IsNullOrWhiteSpace(envelope.Tipo))
                throw new ArgumentException("El origen y el tipo de evento son obligatorios.");

            if (envelope.Timestamp == default)
                throw new ArgumentException("El timestamp de emisión es obligatorio para validar idempotencia.");

            string origenLimpio = envelope.Origen.Trim();
            string tipoLimpio = envelope.Tipo.Trim();
            var timestampUtc = TruncarMilisegundos(envelope.Timestamp);

            Guid eventIdFinal = envelope.EventId == Guid.Empty ? Guid.NewGuid() : envelope.EventId;
            envelope.EventId = eventIdFinal;

            if (await _repository.ExistsEventIdAsync(eventIdFinal))
                return false;

            string ubicacion = ExtraerIdentificadorUbicacion(envelope.Payload);

            var ultimoTimestamp = await _repository.GetLastTimestampAsync(origenLimpio, tipoLimpio, ubicacion);
            if (ultimoTimestamp.HasValue && timestampUtc <= TruncarMilisegundos(ultimoTimestamp.Value))
            {
                return false;
            }

            string? estadoPayload = ExtraerCampoPayload(envelope.Payload, "estadoNuevo")
                                 ?? ExtraerCampoPayload(envelope.Payload, "estado");

            string severidad = DeterminarSeveridadAutomatica(tipoLimpio, estadoPayload);
            bool esPublico = _config.EsEventoPublico(tipoLimpio);

            string payloadRaw = envelope.Payload.ValueKind != JsonValueKind.Undefined
                ? envelope.Payload.GetRawText()
                : "{}";

            var logEntry = new LogEntry
            {
                Id = Guid.NewGuid(),
                EventId = eventIdFinal,
                Origen = origenLimpio,
                TipoEvento = tipoLimpio,
                Severidad = severidad,
                Mensaje = ConstruirMensajeLegible(origenLimpio, tipoLimpio, envelope.Payload),
                UbicacionId = ubicacion,
                TimestampEmision = timestampUtc,
                FechaRegistroUtc = DateTime.UtcNow,
                ExpiraEnUtc = DateTime.UtcNow.AddDays(_config.DiasRetencionLogs > 0 ? _config.DiasRetencionLogs : 30),
                EsPublico = esPublico,
                PayloadJson = payloadRaw
            };

            await _repository.SaveAsync(logEntry);

            VueloPantalla? vueloActualizado = await ActualizarTableroVuelosSiCorrespondeAsync(envelope, logEntry);

            await _notifier.NotificarEventoAsync(logEntry, vueloActualizado);
            return true;
        }

        public async Task<LogEntry> EjecutarDirectoAsync(DirectLogRequestDto dto)
        {
            if (dto is null)
                throw new ArgumentException("El cuerpo de la solicitud no puede ser nulo.");

            if (string.IsNullOrWhiteSpace(dto.Origen) || string.IsNullOrWhiteSpace(dto.Mensaje))
                throw new ArgumentException("El componente de origen y el mensaje son obligatorios.");

            string origenLimpio = dto.Origen.Trim();
            string mensajeLimpio = dto.Mensaje.Trim();
            string severidadNorm = NormalizarSeveridad(dto.Severidad);
            string tipoEvento = string.IsNullOrWhiteSpace(dto.TipoEvento) ? "LogDirecto" : dto.TipoEvento.Trim();
            string ubicacion = string.IsNullOrWhiteSpace(dto.UbicacionId) ? "General" : dto.UbicacionId.Trim();

            bool esPublico = dto.MostrarEnPantallaPublica || _config.EsEventoPublico(tipoEvento);

            var ahoraUtc = TruncarMilisegundos(DateTime.UtcNow);
            var logEntry = new LogEntry
            {
                Id = Guid.NewGuid(),
                EventId = Guid.NewGuid(),
                Origen = origenLimpio,
                UbicacionId = ubicacion,
                Severidad = severidadNorm,
                TipoEvento = tipoEvento,
                Mensaje = mensajeLimpio,
                TimestampEmision = ahoraUtc,
                FechaRegistroUtc = DateTime.UtcNow,
                ExpiraEnUtc = DateTime.UtcNow.AddDays(_config.DiasRetencionLogs > 0 ? _config.DiasRetencionLogs : 30),
                EsPublico = esPublico,
                PayloadJson = JsonSerializer.Serialize(dto)
            };

            await _repository.SaveAsync(logEntry);

            VueloPantalla? vuelo = null;
            bool tieneDatosVuelo = !string.IsNullOrWhiteSpace(dto.EstadoVuelo) ||
                                   !string.IsNullOrWhiteSpace(dto.Destino) ||
                                   !string.IsNullOrWhiteSpace(dto.Puerta) ||
                                   !string.IsNullOrWhiteSpace(dto.Horario) ||
                                   tipoEvento.Contains("Vuelo", StringComparison.OrdinalIgnoreCase);

            if (tieneDatosVuelo &&
                !ubicacion.Equals("General", StringComparison.OrdinalIgnoreCase) &&
                !ubicacion.Equals("Sistema", StringComparison.OrdinalIgnoreCase) &&
                !ubicacion.StartsWith("Pista ", StringComparison.OrdinalIgnoreCase))
            {
                string vueloIdNorm = ubicacion.ToUpperInvariant();
                var existente = await _repository.GetVueloPantallaByIdAsync(vueloIdNorm)
                             ?? await _repository.GetVueloPantallaByIdAsync(ubicacion);

                vuelo = new VueloPantalla
                {
                    VueloId = vueloIdNorm,
                    TipoOperacion = !string.IsNullOrWhiteSpace(dto.TipoOperacion) ? dto.TipoOperacion.Trim() : (existente?.TipoOperacion ?? "Salida"),
                    AerolineaOOrigen = existente?.AerolineaOOrigen ?? origenLimpio,
                    Destino = !string.IsNullOrWhiteSpace(dto.Destino) ? dto.Destino.Trim() : (existente?.Destino ?? "Sin especificar"),
                    Horario = !string.IsNullOrWhiteSpace(dto.Horario) ? dto.Horario.Trim() : (existente?.Horario ?? "--:--"),
                    Puerta = !string.IsNullOrWhiteSpace(dto.Puerta) ? dto.Puerta.Trim() : (existente?.Puerta ?? "-"),
                    Pista = existente?.Pista ?? "-",
                    Estado = !string.IsNullOrWhiteSpace(dto.EstadoVuelo) ? dto.EstadoVuelo.Trim() : (existente?.Estado ?? "En Horario"),
                    SeveridadVisual = severidadNorm,
                    UltimaActualizacionUtc = DateTime.UtcNow
                };
                await _repository.UpsertVueloPantallaAsync(vuelo);
            }

            await _notifier.NotificarEventoAsync(logEntry, vuelo);
            return logEntry;
        }

        private async Task<VueloPantalla?> ActualizarTableroVuelosSiCorrespondeAsync(BusEventEnvelopeDto envelope, LogEntry log)
        {
            bool esGestionVuelos = envelope.Origen.Trim().Equals("GestionVuelos", StringComparison.OrdinalIgnoreCase);
            bool tipoMencionaVuelo = envelope.Tipo.Contains("Vuelo", StringComparison.OrdinalIgnoreCase);

            // Solo eventos de GestionVuelos o cuyo tipo mencione "Vuelo" modifican el tablero
            if (!esGestionVuelos && !tipoMencionaVuelo)
                return null;

            string? vueloId = ExtraerCampoPayload(envelope.Payload, "vueloId");
            string idCandidato = !string.IsNullOrWhiteSpace(vueloId) ? vueloId : log.UbicacionId;

            if (string.IsNullOrWhiteSpace(idCandidato) ||
                idCandidato.Equals("Sistema", StringComparison.OrdinalIgnoreCase) ||
                idCandidato.Equals("General", StringComparison.OrdinalIgnoreCase) ||
                idCandidato.StartsWith("Pista ", StringComparison.OrdinalIgnoreCase))
                return null;

            string idFinal = idCandidato.Trim().ToUpperInvariant();

            var existente = await _repository.GetVueloPantallaByIdAsync(idFinal)
                         ?? await _repository.GetVueloPantallaByIdAsync(idCandidato.Trim());

            string? estadoExplicito = ExtraerCampoPayload(envelope.Payload, "estadoNuevo")
                                   ?? ExtraerCampoPayload(envelope.Payload, "estado");

            string nuevoEstado = !string.IsNullOrWhiteSpace(estadoExplicito)
                ? estadoExplicito
                : (existente?.Estado ?? (envelope.Tipo.Trim().Equals("VueloCreado", StringComparison.OrdinalIgnoreCase) ? "Programado" : envelope.Tipo.Trim()));

            var vuelo = new VueloPantalla
            {
                VueloId = idFinal,
                TipoOperacion = ExtraerCampoPayload(envelope.Payload, "tipoOperacion") ?? existente?.TipoOperacion ?? "Salida",
                AerolineaOOrigen = (ExtraerCampoPayload(envelope.Payload, "origen") ?? existente?.AerolineaOOrigen ?? "EZE").ToUpperInvariant(),
                Destino = ExtraerCampoPayload(envelope.Payload, "destino") ?? existente?.Destino ?? "Sin especificar",
                Horario = ExtraerCampoPayload(envelope.Payload, "horario") ?? existente?.Horario ?? "--:--",
                Puerta = (ExtraerCampoPayload(envelope.Payload, "puerta") ?? existente?.Puerta ?? "-").ToUpperInvariant(),
                Pista = (ExtraerCampoPayload(envelope.Payload, "pista") ?? existente?.Pista ?? "-").ToUpperInvariant(),
                Estado = nuevoEstado,
                SeveridadVisual = !string.IsNullOrWhiteSpace(estadoExplicito) || existente == null ? log.Severidad : existente.SeveridadVisual,
                UltimaActualizacionUtc = DateTime.UtcNow
            };

            await _repository.UpsertVueloPantallaAsync(vuelo);
            return vuelo;
        }

        private static DateTime TruncarMilisegundos(DateTime dt)
        {
            var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, utc.Millisecond, DateTimeKind.Utc);
        }

        private static string DeterminarSeveridadAutomatica(string tipo, string? estadoNuevo = null)
        {
            string t = $"{tipo} {estadoNuevo}".ToLowerInvariant();
            if (t.Contains("error") || t.Contains("denegada") || t.Contains("bloqueada") || t.Contains("timeout") || t.Contains("cancelado"))
                return NivelSeveridad.Error.ToString();
            if (t.Contains("alerta") || t.Contains("ocupada") || t.Contains("demora") || t.Contains("viento"))
                return NivelSeveridad.Alerta.ToString();
            if (t.Contains("otorgada") || t.Contains("liberada") || t.Contains("creado") || t.Contains("arribado") || t.Contains("aprobado") || t.Contains("despegando"))
                return NivelSeveridad.Aprobado.ToString();

            return NivelSeveridad.Informacion.ToString();
        }

        private static string NormalizarSeveridad(string? sev)
        {
            if (string.IsNullOrWhiteSpace(sev)) return NivelSeveridad.Informacion.ToString();
            return sev.Trim().ToLowerInvariant() switch
            {
                "error" => NivelSeveridad.Error.ToString(),
                "alerta" or "warning" => NivelSeveridad.Alerta.ToString(),
                "aprobado" or "ok" or "success" => NivelSeveridad.Aprobado.ToString(),
                _ => NivelSeveridad.Informacion.ToString()
            };
        }

        private static string? ExtraerCampoPayload(JsonElement payload, string propName)
        {
            if (payload.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in payload.EnumerateObject())
                {
                    if (prop.Name.Equals(propName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            var val = prop.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Number ||
                                 prop.Value.ValueKind == JsonValueKind.True ||
                                 prop.Value.ValueKind == JsonValueKind.False)
                        {
                            return prop.Value.GetRawText().Trim();
                        }
                    }
                }
            }
            return null;
        }

        private static string ExtraerIdentificadorUbicacion(JsonElement payload)
        {
            var vuelo = ExtraerCampoPayload(payload, "vueloId");
            if (!string.IsNullOrWhiteSpace(vuelo)) return vuelo.Trim().ToUpperInvariant();

            var pista = ExtraerCampoPayload(payload, "pista");
            if (!string.IsNullOrWhiteSpace(pista)) return $"Pista {pista.Trim().ToUpperInvariant()}";

            var ubicacion = ExtraerCampoPayload(payload, "ubicacionId");
            if (!string.IsNullOrWhiteSpace(ubicacion)) return ubicacion.Trim();

            return "Sistema";
        }

        private static string ConstruirMensajeLegible(string origen, string tipo, JsonElement payload)
        {
            var msgExplicito = ExtraerCampoPayload(payload, "mensaje");
            if (!string.IsNullOrWhiteSpace(msgExplicito)) return msgExplicito;

            var vueloId = ExtraerCampoPayload(payload, "vueloId");
            var estadoNuevo = ExtraerCampoPayload(payload, "estadoNuevo");
            if (!string.IsNullOrWhiteSpace(vueloId) && !string.IsNullOrWhiteSpace(estadoNuevo))
                return $"Vuelo {vueloId.ToUpperInvariant()} cambió a estado: {estadoNuevo}.";

            string id = ExtraerIdentificadorUbicacion(payload);
            return $"[{origen}] Evento '{tipo}' registrado sobre {id}.";
        }
    }
}