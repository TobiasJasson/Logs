using Aplication.Abstractions;
using Aplication.DTOs;
using Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Infrastructure.Repositories
{
    public class PostgresLogRepository : ILogRepository
    {
        private readonly NpgsqlDataSource? _dataSource;
        private readonly ILogger<PostgresLogRepository> _logger;
        private readonly string _schema;

        private const int MaxInMemoryLogs = 1000;
        private static readonly ConcurrentQueue<LogEntry> _inMemoryLogs = new();
        private static readonly ConcurrentDictionary<string, VueloPantalla> _inMemoryVuelos = new(StringComparer.OrdinalIgnoreCase);

        private const string ColumnasLog =
            "id, event_id, origen, tipo_evento, severidad, mensaje, ubicacion_id, " +
            "timestamp_emision, fecha_registro_utc, expira_en_utc, es_publico, payload::text";

        private const string ColumnasVuelo =
            "vuelo_id, tipo_operacion, aerolinea_o_origen, destino, horario, puerta, pista, " +
            "estado, severidad_visual, ultima_actualizacion_utc";

        public PostgresLogRepository(IConfiguration config, ILogger<PostgresLogRepository> logger)
        {
            _logger = logger;

            _schema = config["Postgres:Schema"] ?? config["PostgresDb:Schema"] ?? "logs";
            if (!Regex.IsMatch(_schema, "^[a-z_][a-z0-9_]*$"))
                throw new InvalidOperationException($"Nombre de schema inválido: '{_schema}'.");

            var connString = config["Postgres:ConnectionString"] ?? config["PostgresDb:ConnectionString"];
            if (string.IsNullOrWhiteSpace(connString))
            {
                _logger.LogWarning("No hay ConnectionString de PostgreSQL configurada. Operando con memoria local acotada.");
                return;
            }

            try
            {
                _dataSource = NpgsqlDataSource.Create(connString);
                CrearTablasSiNoExisten();
                _logger.LogInformation("PostgreSQL conectado exitosamente (schema {Schema}). Tablas e índices verificados.", _schema);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PostgreSQL no disponible. Operando con memoria local acotada.");
                _dataSource?.Dispose();
                _dataSource = null;
            }
        }

        private void CrearTablasSiNoExisten()
        {
            string s = _schema;
            string sql = $@"
CREATE TABLE IF NOT EXISTS {s}.logs_centralizados (
    id                  uuid        PRIMARY KEY,
    event_id            uuid        NOT NULL,
    origen              text        NOT NULL,
    tipo_evento         text        NOT NULL,
    severidad           text        NOT NULL,
    mensaje             text        NOT NULL,
    ubicacion_id        text        NOT NULL,
    timestamp_emision   timestamptz NOT NULL,
    fecha_registro_utc  timestamptz NOT NULL,
    expira_en_utc       timestamptz NOT NULL,
    es_publico          boolean     NOT NULL,
    payload             jsonb       NOT NULL DEFAULT '{{}}'::jsonb
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_logs_unique_event_id ON {s}.logs_centralizados (event_id);
CREATE INDEX IF NOT EXISTS idx_logs_idempotencia ON {s}.logs_centralizados (lower(origen), lower(tipo_evento), lower(ubicacion_id), timestamp_emision DESC);
CREATE INDEX IF NOT EXISTS idx_logs_expiracion ON {s}.logs_centralizados (expira_en_utc);
CREATE INDEX IF NOT EXISTS idx_logs_timestamp ON {s}.logs_centralizados (timestamp_emision DESC);
CREATE INDEX IF NOT EXISTS idx_logs_publicos ON {s}.logs_centralizados (timestamp_emision DESC) WHERE es_publico;

CREATE TABLE IF NOT EXISTS {s}.tablero_vuelos (
    vuelo_id                  text        PRIMARY KEY,
    tipo_operacion            text        NOT NULL,
    aerolinea_o_origen        text        NOT NULL,
    destino                   text        NOT NULL,
    horario                   text        NOT NULL,
    puerta                    text        NOT NULL,
    pista                     text        NOT NULL,
    estado                    text        NOT NULL,
    severidad_visual          text        NOT NULL,
    ultima_actualizacion_utc  timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_vuelos_actualizacion ON {s}.tablero_vuelos (ultima_actualizacion_utc DESC);";

            using var conn = _dataSource!.OpenConnection();
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.ExecuteNonQuery();
        }

        public async Task SaveAsync(LogEntry log)
        {
            if (_dataSource != null)
            {
                try
                {
                    await using var conn = await _dataSource.OpenConnectionAsync();
                    await using var cmd = new NpgsqlCommand($@"
INSERT INTO {_schema}.logs_centralizados
    (id, event_id, origen, tipo_evento, severidad, mensaje, ubicacion_id,
     timestamp_emision, fecha_registro_utc, expira_en_utc, es_publico, payload)
VALUES
    (@id, @event_id, @origen, @tipo_evento, @severidad, @mensaje, @ubicacion_id,
     @timestamp_emision, @fecha_registro_utc, @expira_en_utc, @es_publico, @payload)
ON CONFLICT (event_id) DO NOTHING;", conn);

                    cmd.Parameters.AddWithValue("id", log.Id);
                    cmd.Parameters.AddWithValue("event_id", log.EventId);
                    cmd.Parameters.AddWithValue("origen", log.Origen);
                    cmd.Parameters.AddWithValue("tipo_evento", log.TipoEvento);
                    cmd.Parameters.AddWithValue("severidad", log.Severidad);
                    cmd.Parameters.AddWithValue("mensaje", log.Mensaje);
                    cmd.Parameters.AddWithValue("ubicacion_id", log.UbicacionId);
                    cmd.Parameters.AddWithValue("timestamp_emision", Utc(log.TimestampEmision));
                    cmd.Parameters.AddWithValue("fecha_registro_utc", Utc(log.FechaRegistroUtc));
                    cmd.Parameters.AddWithValue("expira_en_utc", Utc(log.ExpiraEnUtc));
                    cmd.Parameters.AddWithValue("es_publico", log.EsPublico);
                    cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
                    {
                        Value = string.IsNullOrWhiteSpace(log.PayloadJson) ? "{}" : log.PayloadJson
                    });

                    int filas = await cmd.ExecuteNonQueryAsync();
                    if (filas == 0)
                        _logger.LogInformation("Evento duplicado detectado por índice único en PostgreSQL (EventId: {EventId}).", log.EventId);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error al insertar en PostgreSQL. Guardando en memoria local.");
                }
            }

            EncolarEnMemoria(log);
        }

        public async Task<LogEntry?> GetByIdAsync(Guid id)
        {
            if (_dataSource != null)
            {
                try
                {
                    var lista = await ConsultarLogsAsync(
                        $"SELECT {ColumnasLog} FROM {_schema}.logs_centralizados WHERE id = @id",
                        cmd => cmd.Parameters.AddWithValue("id", id));
                    return lista.FirstOrDefault();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo lectura por ID en PostgreSQL.");
                }
            }
            return _inMemoryLogs.FirstOrDefault(x => x.Id == id);
        }

        public async Task<bool> ExistsEventIdAsync(Guid eventId)
        {
            if (eventId == Guid.Empty) return false;

            if (_dataSource != null)
            {
                try
                {
                    await using var conn = await _dataSource.OpenConnectionAsync();
                    await using var cmd = new NpgsqlCommand(
                        $"SELECT EXISTS (SELECT 1 FROM {_schema}.logs_centralizados WHERE event_id = @event_id)", conn);
                    cmd.Parameters.AddWithValue("event_id", eventId);
                    return (bool)(await cmd.ExecuteScalarAsync() ?? false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo verificación de EventId en PostgreSQL.");
                }
            }
            return _inMemoryLogs.Any(x => x.EventId == eventId);
        }

        public async Task<IEnumerable<LogEntry>> GetFilteredAsync(LogFilterDto filtro)
        {
            int page = filtro.Page < 1 ? 1 : filtro.Page;
            int pageSize = filtro.PageSize < 1 ? 50 : (filtro.PageSize > 200 ? 200 : filtro.PageSize);
            int skip = (page - 1) * pageSize;

            DateTime? desde = filtro.FechaDesde;
            DateTime? hasta = filtro.FechaHasta;
            if (desde.HasValue && hasta.HasValue && desde.Value > hasta.Value)
                (desde, hasta) = (hasta, desde);

            if (_dataSource != null)
            {
                try
                {
                    var condiciones = new List<string>();
                    var parametros = new List<NpgsqlParameter>();

                    if (desde.HasValue)
                    {
                        condiciones.Add("timestamp_emision >= @desde");
                        parametros.Add(new NpgsqlParameter("desde", Utc(desde.Value)));
                    }
                    if (hasta.HasValue)
                    {
                        condiciones.Add("timestamp_emision <= @hasta");
                        parametros.Add(new NpgsqlParameter("hasta", Utc(hasta.Value)));
                    }
                    if (!string.IsNullOrWhiteSpace(filtro.Origen))
                    {
                        condiciones.Add("lower(origen) = lower(@origen)");
                        parametros.Add(new NpgsqlParameter("origen", filtro.Origen.Trim()));
                    }
                    if (!string.IsNullOrWhiteSpace(filtro.TipoEvento))
                    {
                        condiciones.Add("lower(tipo_evento) = lower(@tipo_evento)");
                        parametros.Add(new NpgsqlParameter("tipo_evento", filtro.TipoEvento.Trim()));
                    }
                    if (!string.IsNullOrWhiteSpace(filtro.Severidad))
                    {
                        condiciones.Add("lower(severidad) = lower(@severidad)");
                        parametros.Add(new NpgsqlParameter("severidad", filtro.Severidad.Trim()));
                    }

                    string where = condiciones.Count > 0 ? "WHERE " + string.Join(" AND ", condiciones) : "";
                    string sql = $@"SELECT {ColumnasLog} FROM {_schema}.logs_centralizados {where}
ORDER BY timestamp_emision DESC
OFFSET @skip LIMIT @take";

                    return await ConsultarLogsAsync(sql, cmd =>
                    {
                        foreach (var p in parametros) cmd.Parameters.Add(p);
                        cmd.Parameters.AddWithValue("skip", skip);
                        cmd.Parameters.AddWithValue("take", pageSize);
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error consultando PostgreSQL. Leyendo desde memoria local.");
                }
            }

            IEnumerable<LogEntry> query = _inMemoryLogs;
            if (desde.HasValue)
                query = query.Where(x => x.TimestampEmision >= desde.Value.ToUniversalTime());
            if (hasta.HasValue)
                query = query.Where(x => x.TimestampEmision <= hasta.Value.ToUniversalTime());
            if (!string.IsNullOrWhiteSpace(filtro.Origen))
                query = query.Where(x => x.Origen.Equals(filtro.Origen.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(filtro.TipoEvento))
                query = query.Where(x => x.TipoEvento.Equals(filtro.TipoEvento.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(filtro.Severidad))
                query = query.Where(x => x.Severidad.Equals(filtro.Severidad.Trim(), StringComparison.OrdinalIgnoreCase));

            return query.OrderByDescending(x => x.TimestampEmision).Skip(skip).Take(pageSize).ToList();
        }

        public async Task<IEnumerable<LogEntry>> GetRecentPublicEventsAsync(int count = 20)
        {
            if (_dataSource != null)
            {
                try
                {
                    return await ConsultarLogsAsync(
                        $@"SELECT {ColumnasLog} FROM {_schema}.logs_centralizados
WHERE es_publico ORDER BY timestamp_emision DESC LIMIT @count",
                        cmd => cmd.Parameters.AddWithValue("count", count));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo consulta pública en PostgreSQL.");
                }
            }

            return _inMemoryLogs
                .Where(x => x.EsPublico)
                .OrderByDescending(x => x.TimestampEmision)
                .Take(count)
                .ToList();
        }

        public async Task<DateTime?> GetLastTimestampAsync(string origen, string tipoEvento, string ubicacionId)
        {
            if (_dataSource != null)
            {
                try
                {
                    await using var conn = await _dataSource.OpenConnectionAsync();
                    await using var cmd = new NpgsqlCommand($@"
SELECT max(timestamp_emision) FROM {_schema}.logs_centralizados
WHERE lower(origen) = lower(@origen)
  AND lower(tipo_evento) = lower(@tipo_evento)
  AND lower(ubicacion_id) = lower(@ubicacion_id)", conn);
                    cmd.Parameters.AddWithValue("origen", origen.Trim());
                    cmd.Parameters.AddWithValue("tipo_evento", tipoEvento.Trim());
                    cmd.Parameters.AddWithValue("ubicacion_id", ubicacionId.Trim());

                    var resultado = await cmd.ExecuteScalarAsync();
                    return resultado is DateTime dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : null;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo consulta de idempotencia en PostgreSQL.");
                }
            }

            return _inMemoryLogs
                .Where(x => x.Origen.Equals(origen.Trim(), StringComparison.OrdinalIgnoreCase) &&
                            x.TipoEvento.Equals(tipoEvento.Trim(), StringComparison.OrdinalIgnoreCase) &&
                            x.UbicacionId.Equals(ubicacionId.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.TimestampEmision)
                .Select(x => (DateTime?)x.TimestampEmision)
                .FirstOrDefault();
        }

        public async Task<VueloPantalla?> GetVueloPantallaByIdAsync(string vueloId)
        {
            if (string.IsNullOrWhiteSpace(vueloId)) return null;
            string idNorm = vueloId.Trim().ToUpperInvariant();

            if (_dataSource != null)
            {
                try
                {
                    var lista = await ConsultarVuelosAsync(
                        $"SELECT {ColumnasVuelo} FROM {_schema}.tablero_vuelos WHERE upper(vuelo_id) = @vuelo_id",
                        cmd => cmd.Parameters.AddWithValue("vuelo_id", idNorm));
                    return lista.FirstOrDefault();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo lectura de vuelo {VueloId} en PostgreSQL.", idNorm);
                }
            }

            _inMemoryVuelos.TryGetValue(idNorm, out var vuelo);
            return vuelo;
        }

        public async Task UpsertVueloPantallaAsync(VueloPantalla vuelo)
        {
            vuelo.VueloId = vuelo.VueloId.Trim().ToUpperInvariant();
            _inMemoryVuelos[vuelo.VueloId] = vuelo;

            if (_dataSource != null)
            {
                try
                {
                    await using var conn = await _dataSource.OpenConnectionAsync();
                    await using var cmd = new NpgsqlCommand($@"
INSERT INTO {_schema}.tablero_vuelos ({ColumnasVuelo})
VALUES (@vuelo_id, @tipo_operacion, @aerolinea_o_origen, @destino, @horario, @puerta, @pista,
        @estado, @severidad_visual, @ultima_actualizacion_utc)
ON CONFLICT (vuelo_id) DO UPDATE SET
    tipo_operacion = EXCLUDED.tipo_operacion,
    aerolinea_o_origen = EXCLUDED.aerolinea_o_origen,
    destino = EXCLUDED.destino,
    horario = EXCLUDED.horario,
    puerta = EXCLUDED.puerta,
    pista = EXCLUDED.pista,
    estado = EXCLUDED.estado,
    severidad_visual = EXCLUDED.severidad_visual,
    ultima_actualizacion_utc = EXCLUDED.ultima_actualizacion_utc;", conn);

                    cmd.Parameters.AddWithValue("vuelo_id", vuelo.VueloId);
                    cmd.Parameters.AddWithValue("tipo_operacion", vuelo.TipoOperacion ?? "Salida");
                    cmd.Parameters.AddWithValue("aerolinea_o_origen", vuelo.AerolineaOOrigen ?? "-");
                    cmd.Parameters.AddWithValue("destino", vuelo.Destino ?? "Sin especificar");
                    cmd.Parameters.AddWithValue("horario", vuelo.Horario ?? "--:--");
                    cmd.Parameters.AddWithValue("puerta", vuelo.Puerta ?? "-");
                    cmd.Parameters.AddWithValue("pista", vuelo.Pista ?? "-");
                    cmd.Parameters.AddWithValue("estado", vuelo.Estado ?? "Programado");
                    cmd.Parameters.AddWithValue("severidad_visual", vuelo.SeveridadVisual ?? "Informacion");
                    cmd.Parameters.AddWithValue("ultima_actualizacion_utc", Utc(vuelo.UltimaActualizacionUtc));

                    await cmd.ExecuteNonQueryAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo actualización de tablero de vuelos en PostgreSQL.");
                }
            }
        }

        public async Task<IEnumerable<VueloPantalla>> GetVuelosPantallaAsync()
        {
            if (_dataSource != null)
            {
                try
                {
                    return await ConsultarVuelosAsync(
                        $"SELECT {ColumnasVuelo} FROM {_schema}.tablero_vuelos ORDER BY ultima_actualizacion_utc DESC LIMIT 50",
                        _ => { });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo lectura de tablero de vuelos en PostgreSQL.");
                }
            }

            return _inMemoryVuelos.Values
                .OrderByDescending(x => x.UltimaActualizacionUtc)
                .Take(50)
                .ToList();
        }

        public async Task<DashboardResumenDto> GetResumenDashboardAsync()
        {
            var vuelos = (await GetVuelosPantallaAsync()).ToList();
            int vuelosEnCurso = vuelos.Count(v =>
                !v.Estado.Equals("Arribado", StringComparison.OrdinalIgnoreCase) &&
                !v.Estado.Equals("Cancelado", StringComparison.OrdinalIgnoreCase));

            if (_dataSource != null)
            {
                try
                {
                    await using var conn = await _dataSource.OpenConnectionAsync();

                    var resumen = new DashboardResumenDto { VuelosEnCurso = vuelosEnCurso };

                    await using (var cmd = new NpgsqlCommand($@"
SELECT count(*),
       count(*) FILTER (WHERE tipo_evento ILIKE '%Otorgada%'),
       count(*) FILTER (WHERE tipo_evento ILIKE '%Denegada%'),
       count(*) FILTER (WHERE lower(severidad) = 'alerta'),
       count(*) FILTER (WHERE lower(severidad) = 'error')
FROM {_schema}.logs_centralizados", conn))
                    await using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            resumen.TotalLogsRegistrados = reader.GetInt64(0);
                            resumen.AutorizacionesOtorgadas = (int)reader.GetInt64(1);
                            resumen.AutorizacionesDenegadas = (int)reader.GetInt64(2);
                            resumen.AlertasActivas = (int)reader.GetInt64(3);
                            resumen.ErroresRegistrados = (int)reader.GetInt64(4);
                        }
                    }

                    var porComponente = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    await using (var cmd = new NpgsqlCommand($@"
SELECT min(trim(origen)), count(*)
FROM {_schema}.logs_centralizados
WHERE trim(origen) <> ''
GROUP BY lower(trim(origen))", conn))
                    await using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                            porComponente[reader.GetString(0)] = (int)reader.GetInt64(1);
                    }

                    resumen.LogsPorComponente = porComponente;
                    return resumen;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo cálculo de resumen en PostgreSQL. Calculando desde memoria local.");
                }
            }

            var lista = _inMemoryLogs.ToList();
            return new DashboardResumenDto
            {
                TotalLogsRegistrados = lista.Count,
                AutorizacionesOtorgadas = lista.Count(l => l.TipoEvento.Contains("Otorgada", StringComparison.OrdinalIgnoreCase)),
                AutorizacionesDenegadas = lista.Count(l => l.TipoEvento.Contains("Denegada", StringComparison.OrdinalIgnoreCase)),
                AlertasActivas = lista.Count(l => l.Severidad.Equals("Alerta", StringComparison.OrdinalIgnoreCase)),
                ErroresRegistrados = lista.Count(l => l.Severidad.Equals("Error", StringComparison.OrdinalIgnoreCase)),
                VuelosEnCurso = vuelosEnCurso,
                LogsPorComponente = lista
                    .Where(l => !string.IsNullOrWhiteSpace(l.Origen))
                    .GroupBy(l => l.Origen.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.First().Origen.Trim(), g => g.Count(), StringComparer.OrdinalIgnoreCase)
            };
        }

        public async Task<int> EliminarLogsVencidosAsync()
        {
            if (_dataSource == null) return 0;

            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                $"DELETE FROM {_schema}.logs_centralizados WHERE expira_en_utc < now()", conn);
            return await cmd.ExecuteNonQueryAsync();
        }

        private async Task<List<LogEntry>> ConsultarLogsAsync(string sql, Action<NpgsqlCommand> configurar)
        {
            await using var conn = await _dataSource!.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            configurar(cmd);

            var resultado = new List<LogEntry>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                resultado.Add(new LogEntry
                {
                    Id = reader.GetGuid(0),
                    EventId = reader.GetGuid(1),
                    Origen = reader.GetString(2),
                    TipoEvento = reader.GetString(3),
                    Severidad = reader.GetString(4),
                    Mensaje = reader.GetString(5),
                    UbicacionId = reader.GetString(6),
                    TimestampEmision = DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc),
                    FechaRegistroUtc = DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc),
                    ExpiraEnUtc = DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc),
                    EsPublico = reader.GetBoolean(10),
                    PayloadJson = reader.GetString(11)
                });
            }
            return resultado;
        }

        private async Task<List<VueloPantalla>> ConsultarVuelosAsync(string sql, Action<NpgsqlCommand> configurar)
        {
            await using var conn = await _dataSource!.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            configurar(cmd);

            var resultado = new List<VueloPantalla>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                resultado.Add(new VueloPantalla
                {
                    VueloId = reader.GetString(0),
                    TipoOperacion = reader.GetString(1),
                    AerolineaOOrigen = reader.GetString(2),
                    Destino = reader.GetString(3),
                    Horario = reader.GetString(4),
                    Puerta = reader.GetString(5),
                    Pista = reader.GetString(6),
                    Estado = reader.GetString(7),
                    SeveridadVisual = reader.GetString(8),
                    UltimaActualizacionUtc = DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc)
                });
            }
            return resultado;
        }

        private static DateTime Utc(DateTime dt) =>
            dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();

        private static void EncolarEnMemoria(LogEntry log)
        {
            _inMemoryLogs.Enqueue(log);
            while (_inMemoryLogs.Count > MaxInMemoryLogs && _inMemoryLogs.TryDequeue(out _)) { }
        }
    }
}