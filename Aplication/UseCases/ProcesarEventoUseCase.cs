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
            if (string.IsNullOrWhiteSpace(envelope.Origen) || string.IsNullOrWhiteSpace(envelope.Tipo))
                throw new ArgumentException("El origen y el tipo de evento son obligatorios.");

            if (envelope.Timestamp == default)
                throw new ArgumentException("El timestamp de emisión es obligatorio para validar idempotencia.");

            if (envelope.EventId == Guid.Empty)
                envelope.EventId = Guid.NewGuid();
            else if (await _repository.ExistsEventIdAsync(envelope.EventId))
                return false;

            DateTime tsNormalizado = TruncarMilisegundos(envelope.Timestamp.ToUniversalTime());
            string ubicacion = ExtraerIdentificadorUbicacion(envelope.Payload);

            var ultimoTimestamp = await _repository.GetLastTimestampAsync(envelope.Origen, envelope.Tipo, ubicacion);
            if (ultimoTimestamp.HasValue && tsNormalizado <= TruncarMilisegundos(ultimoTimestamp.Value.ToUniversalTime()))
            {
                return false;
            }

            NivelSeveridad severidad = DeterminarSeveridadAutomatica(envelope.Tipo, envelope.Payload);
            bool esPublico = _config.EsEventoPublico(envelope.Tipo);
            string mensaje = ConstruirMensajeLegible(envelope);
            DateTime ahoraUtc = DateTime.UtcNow;

            var nuevoLog = new LogEntry
            {
                Id = Guid.NewGuid(),
                EventId = envelope.EventId,
                Origen = envelope.Origen.Trim(),
                TipoEvento = envelope.Tipo.Trim(),
                Severidad = severidad.ToString(),
                Mensaje = mensaje,
                UbicacionId = ubicacion,
                TimestampEmision = tsNormalizado,
                FechaRegistroUtc = ahoraUtc,
                ExpiraEnUtc = ahoraUtc.AddDays(_config.DiasRetencionLogs),
                EsPublico = esPublico,
                PayloadJson = envelope.Payload.ValueKind != JsonValueKind.Undefined
                    ? envelope.Payload.GetRawText()
                    : "{}"
            };

            await _repository.SaveAsync(nuevoLog);

            VueloPantalla? vueloActualizado = await ActualizarTableroVuelosSiCorrespondeAsync(envelope, nuevoLog);
            await _notifier.NotificarEventoAsync(nuevoLog, vueloActualizado);
            return true;
        }

        public async Task<LogEntry> EjecutarDirectoAsync(DirectLogRequestDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Origen) || string.IsNullOrWhiteSpace(dto.Mensaje))
                throw new ArgumentException("El componente de origen y el mensaje son obligatorios.");

            NivelSeveridad severidad = NormalizarSeveridad(dto.Severidad);
            string tipo = string.IsNullOrWhiteSpace(dto.TipoEvento) ? "LogDirecto" : dto.TipoEvento.Trim();
            bool esPublico = dto.MostrarEnPantallaPublica || _config.EsEventoPublico(tipo);
            DateTime ahoraUtc = TruncarMilisegundos(DateTime.UtcNow);
            string ubicacion = string.IsNullOrWhiteSpace(dto.UbicacionId) ? "General" : dto.UbicacionId.Trim();

            var nuevoLog = new LogEntry
            {
                Id = Guid.NewGuid(),
                EventId = Guid.NewGuid(),
                Origen = dto.Origen.Trim(),
                TipoEvento = tipo,
                Severidad = severidad.ToString(),
                Mensaje = dto.Mensaje.Trim(),
                UbicacionId = ubicacion,
                TimestampEmision = ahoraUtc,
                FechaRegistroUtc = ahoraUtc,
                ExpiraEnUtc = ahoraUtc.AddDays(_config.DiasRetencionLogs),
                EsPublico = esPublico,
                PayloadJson = JsonSerializer.Serialize(dto)
            };

            await _repository.SaveAsync(nuevoLog);

            VueloPantalla? vueloActualizado = null;
            bool tieneDatosVuelo = !string.IsNullOrWhiteSpace(dto.EstadoVuelo) ||
                                   !string.IsNullOrWhiteSpace(dto.Destino) ||
                                   !string.IsNullOrWhiteSpace(dto.Puerta) ||
                                   !string.IsNullOrWhiteSpace(dto.Horario) ||
                                   tipo.Contains("Vuelo", StringComparison.OrdinalIgnoreCase);

            if (tieneDatosVuelo && !ubicacion.Equals("General", StringComparison.OrdinalIgnoreCase))
            {
                var existente = await _repository.GetVueloPantallaByIdAsync(ubicacion);

                vueloActualizado = new VueloPantalla
                {
                    VueloId = ubicacion,
                    AerolineaOOrigen = !string.IsNullOrWhiteSpace(existente?.AerolineaOOrigen) ? existente.AerolineaOOrigen : dto.Origen.Trim(),
                    Destino = !string.IsNullOrWhiteSpace(dto.Destino) ? dto.Destino.Trim() : (existente?.Destino ?? "Sin especificar"),
                    Horario = !string.IsNullOrWhiteSpace(dto.Horario) ? dto.Horario.Trim() : (existente?.Horario ?? "--:--"),
                    Puerta = !string.IsNullOrWhiteSpace(dto.Puerta) ? dto.Puerta.Trim() : (existente?.Puerta ?? "-"),
                    Pista = existente?.Pista ?? "-",
                    Estado = !string.IsNullOrWhiteSpace(dto.EstadoVuelo) ? dto.EstadoVuelo.Trim() : (existente?.Estado ?? "En Horario"),
                    TipoOperacion = !string.IsNullOrWhiteSpace(dto.TipoOperacion) ? dto.TipoOperacion.Trim() : (existente?.TipoOperacion ?? "Salida"),
                    SeveridadVisual = nuevoLog.Severidad,
                    UltimaActualizacionUtc = ahoraUtc
                };
                await _repository.UpsertVueloPantallaAsync(vueloActualizado);
            }

            await _notifier.NotificarEventoAsync(nuevoLog, vueloActualizado);
            return nuevoLog;
        }

        private async Task<VueloPantalla?> ActualizarTableroVuelosSiCorrespondeAsync(BusEventEnvelopeDto envelope, LogEntry log)
        {
            bool esGestionVuelos = envelope.Origen.Equals("GestionVuelos", StringComparison.OrdinalIgnoreCase);
            bool tipoMencionaVuelo = envelope.Tipo.Contains("Vuelo", StringComparison.OrdinalIgnoreCase);

            // Solo eventos de GestionVuelos o cuyo tipo mencione "Vuelo" modifican el tablero
            if (!esGestionVuelos && !tipoMencionaVuelo)
                return null;

            string? vueloId = ExtraerCampoPayload(envelope.Payload, "vueloId");
            string idFinal = !string.IsNullOrWhiteSpace(vueloId) ? vueloId : log.UbicacionId;

            if (string.IsNullOrWhiteSpace(idFinal) ||
                idFinal.Equals("Sistema", StringComparison.OrdinalIgnoreCase) ||
                idFinal.StartsWith("Pista ", StringComparison.OrdinalIgnoreCase))
                return null;

            var existente = await _repository.GetVueloPantallaByIdAsync(idFinal);

            string? estadoExplicito = ExtraerCampoPayload(envelope.Payload, "estadoNuevo")
                                   ?? ExtraerCampoPayload(envelope.Payload, "estado");

            string nuevoEstado = !string.IsNullOrWhiteSpace(estadoExplicito)
                ? estadoExplicito
                : (existente?.Estado ?? (envelope.Tipo.Equals("VueloCreado", StringComparison.OrdinalIgnoreCase) ? "Programado" : envelope.Tipo));

            var vuelo = new VueloPantalla
            {
                VueloId = idFinal,
                TipoOperacion = ExtraerCampoPayload(envelope.Payload, "tipoOperacion") ?? existente?.TipoOperacion ?? "Salida",
                AerolineaOOrigen = ExtraerCampoPayload(envelope.Payload, "origen") ?? existente?.AerolineaOOrigen ?? "EZE",
                Destino = ExtraerCampoPayload(envelope.Payload, "destino") ?? existente?.Destino ?? "Sin especificar",
                Horario = ExtraerCampoPayload(envelope.Payload, "horario") ?? existente?.Horario ?? "--:--",
                Puerta = ExtraerCampoPayload(envelope.Payload, "puerta") ?? existente?.Puerta ?? "-",
                Pista = ExtraerCampoPayload(envelope.Payload, "pista") ?? existente?.Pista ?? "-",
                Estado = nuevoEstado,
                SeveridadVisual = !string.IsNullOrWhiteSpace(estadoExplicito) || existente == null ? log.Severidad : existente.SeveridadVisual,
                UltimaActualizacionUtc = DateTime.UtcNow
            };

            await _repository.UpsertVueloPantallaAsync(vuelo);
            return vuelo;
        }

        private static DateTime TruncarMilisegundos(DateTime dt)
        {
            return new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, dt.Millisecond, DateTimeKind.Utc);
        }

        private NivelSeveridad DeterminarSeveridadAutomatica(string tipoEvento, JsonElement payload)
        {
            string t = tipoEvento.ToLowerInvariant();
            string estadoNuevo = (ExtraerCampoPayload(payload, "estadoNuevo") ?? "").ToLowerInvariant();
            string combinado = $"{t} {estadoNuevo}";

            if (combinado.Contains("error") || combinado.Contains("denegada") || combinado.Contains("bloqueada") || combinado.Contains("timeout") || combinado.Contains("cancelado"))
                return NivelSeveridad.Error;

            if (combinado.Contains("alerta") || combinado.Contains("ocupada") || combinado.Contains("demora") || combinado.Contains("viento"))
                return NivelSeveridad.Alerta;

            if (combinado.Contains("otorgada") || combinado.Contains("liberada") || combinado.Contains("creado") || combinado.Contains("arribado") || combinado.Contains("aprobado") || combinado.Contains("despegando"))
                return NivelSeveridad.Aprobado;

            return NivelSeveridad.Informacion;
        }

        private NivelSeveridad NormalizarSeveridad(string severidadStr)
        {
            if (string.IsNullOrWhiteSpace(severidadStr)) return NivelSeveridad.Informacion;

            return severidadStr.Trim().ToLowerInvariant() switch
            {
                "error" => NivelSeveridad.Error,
                "alerta" or "warning" => NivelSeveridad.Alerta,
                "aprobado" or "ok" or "success" => NivelSeveridad.Aprobado,
                _ => NivelSeveridad.Informacion
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
                        else if (prop.Value.ValueKind == JsonValueKind.Number)
                        {
                            return prop.Value.GetRawText().Trim();
                        }
                    }
                }
            }
            return null;
        }

        private string ExtraerIdentificadorUbicacion(JsonElement payload)
        {
            return ExtraerCampoPayload(payload, "vueloId")
                ?? (ExtraerCampoPayload(payload, "pista") is string p ? $"Pista {p}" : null)
                ?? ExtraerCampoPayload(payload, "ubicacionId")
                ?? "Sistema";
        }

        private string ConstruirMensajeLegible(BusEventEnvelopeDto env)
        {
            string? msg = ExtraerCampoPayload(env.Payload, "mensaje");
            if (!string.IsNullOrWhiteSpace(msg)) return msg;

            string? estadoNuevo = ExtraerCampoPayload(env.Payload, "estadoNuevo");
            string? vueloId = ExtraerCampoPayload(env.Payload, "vueloId");
            if (!string.IsNullOrWhiteSpace(vueloId) && !string.IsNullOrWhiteSpace(estadoNuevo))
                return $"Vuelo {vueloId} cambió a estado: {estadoNuevo}";

            string ubicacion = ExtraerIdentificadorUbicacion(env.Payload);
            return $"[{env.Origen}] Evento '{env.Tipo}' registrado sobre {ubicacion}.";
        }
    }
}