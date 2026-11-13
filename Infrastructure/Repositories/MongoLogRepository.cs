using Aplication.Abstractions;
using Aplication.DTOs;
using Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Infrastructure.Repositories
{
    public class MongoLogRepository : ILogRepository
    {
        private readonly IMongoCollection<LogEntry>? _logsCollection;
        private readonly IMongoCollection<VueloPantalla>? _vuelosCollection;
        private readonly ILogger<MongoLogRepository> _logger;

        private const int MaxInMemoryLogs = 1000;
        private static readonly ConcurrentQueue<LogEntry> _inMemoryLogs = new();
        private static readonly ConcurrentDictionary<string, VueloPantalla> _inMemoryVuelos = new(StringComparer.OrdinalIgnoreCase);
        private static bool _serializerRegistered = false;
        private static readonly object _lockObj = new();

        public MongoLogRepository(
            IConfiguration config,
            ILogger<MongoLogRepository> logger)
        {
            _logger = logger;
            RegistrarSerializadoresMongo();

            var connString = config["MongoDb:ConnectionString"];
            var dbName = config["MongoDb:DatabaseName"] ?? "AeropuertoLogsDb";

            if (!string.IsNullOrWhiteSpace(connString))
            {
                try
                {
                    var settings = MongoClientSettings.FromConnectionString(connString);
                    settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
                    var client = new MongoClient(settings);
                    var db = client.GetDatabase(dbName);

                    _logsCollection = db.GetCollection<LogEntry>("LogsCentralizados");
                    _vuelosCollection = db.GetCollection<VueloPantalla>("TableroVuelos");

                    CrearIndicesMongo();
                    _logger.LogInformation("MongoDB conectado exitosamente ({DbName}). Índices verificados.", dbName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MongoDB no disponible en {Conn}. Operando con memoria local acotada.", connString);
                    _logsCollection = null;
                    _vuelosCollection = null;
                }
            }
        }

        private static void RegistrarSerializadoresMongo()
        {
            lock (_lockObj)
            {
                if (_serializerRegistered) return;
                try
                {
                    BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
                }
                catch (BsonSerializationException) { }

                if (!BsonClassMap.IsClassMapRegistered(typeof(VueloPantalla)))
                {
                    BsonClassMap.RegisterClassMap<VueloPantalla>(cm =>
                    {
                        cm.AutoMap();
                        cm.MapIdProperty(v => v.VueloId);
                        cm.SetIgnoreExtraElements(true);
                    });
                }
                _serializerRegistered = true;
            }
        }

        private void CrearIndicesMongo()
        {
            if (_logsCollection == null) return;

            var ttlIndex = new CreateIndexModel<LogEntry>(
                Builders<LogEntry>.IndexKeys.Ascending(x => x.ExpiraEnUtc),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "idx_ttl_expiracion" });

            var eventIdIndex = new CreateIndexModel<LogEntry>(
                Builders<LogEntry>.IndexKeys.Ascending(x => x.EventId),
                new CreateIndexOptions { Unique = true, Name = "idx_unique_eventId" });

            var idempotenciaIndex = new CreateIndexModel<LogEntry>(
                Builders<LogEntry>.IndexKeys
                    .Ascending(x => x.Origen)
                    .Ascending(x => x.TipoEvento)
                    .Ascending(x => x.UbicacionId)
                    .Descending(x => x.TimestampEmision),
                new CreateIndexOptions { Name = "idx_idempotencia" });

            _logsCollection.Indexes.CreateMany(new[] { ttlIndex, eventIdIndex, idempotenciaIndex });
        }

        public async Task SaveAsync(LogEntry log)
        {
            if (_logsCollection != null)
            {
                try
                {
                    await _logsCollection.InsertOneAsync(log);
                    return;
                }
                catch (MongoWriteException mwEx) when (mwEx.WriteError.Category == ServerErrorCategory.DuplicateKey)
                {
                    _logger.LogInformation("Evento duplicado detectado por índice único en MongoDB (EventId: {EventId}).", log.EventId);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error al insertar en MongoDB. Guardando en memoria local.");
                }
            }

            EncolarEnMemoria(log);
        }

        private static void EncolarEnMemoria(LogEntry log)
        {
            _inMemoryLogs.Enqueue(log);
            while (_inMemoryLogs.Count > MaxInMemoryLogs && _inMemoryLogs.TryDequeue(out _)) { }
        }

        public async Task<LogEntry?> GetByIdAsync(Guid id)
        {
            if (_logsCollection != null)
            {
                try
                {
                    return await _logsCollection.Find(x => x.Id == id).FirstOrDefaultAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo lectura por ID en MongoDB.");
                }
            }
            return _inMemoryLogs.FirstOrDefault(x => x.Id == id);
        }

        public async Task<bool> ExistsEventIdAsync(Guid eventId)
        {
            if (eventId == Guid.Empty) return false;

            if (_logsCollection != null)
            {
                try
                {
                    return await _logsCollection.Find(x => x.EventId == eventId).AnyAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo verificación de EventId en MongoDB.");
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
            {
                (desde, hasta) = (hasta, desde);
            }

            if (_logsCollection != null)
            {
                try
                {
                    var builder = Builders<LogEntry>.Filter;
                    var filterDef = builder.Empty;

                    if (desde.HasValue)
                        filterDef &= builder.Gte(x => x.TimestampEmision, desde.Value.ToUniversalTime());

                    if (hasta.HasValue)
                        filterDef &= builder.Lte(x => x.TimestampEmision, hasta.Value.ToUniversalTime());

                    if (!string.IsNullOrWhiteSpace(filtro.Origen))
                    {
                        var regexOrigen = new BsonRegularExpression($"^{Regex.Escape(filtro.Origen.Trim())}$", "i");
                        filterDef &= builder.Regex(x => x.Origen, regexOrigen);
                    }

                    if (!string.IsNullOrWhiteSpace(filtro.TipoEvento))
                    {
                        var regexTipo = new BsonRegularExpression($"^{Regex.Escape(filtro.TipoEvento.Trim())}$", "i");
                        filterDef &= builder.Regex(x => x.TipoEvento, regexTipo);
                    }

                    if (!string.IsNullOrWhiteSpace(filtro.Severidad))
                    {
                        var regexSev = new BsonRegularExpression($"^{Regex.Escape(filtro.Severidad.Trim())}$", "i");
                        filterDef &= builder.Regex(x => x.Severidad, regexSev);
                    }

                    return await _logsCollection.Find(filterDef)
                        .SortByDescending(x => x.TimestampEmision)
                        .Skip(skip)
                        .Limit(pageSize)
                        .ToListAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error consultando MongoDB. Leyendo desde memoria local.");
                }
            }

            var query = _inMemoryLogs.AsEnumerable();

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

            return query
                .OrderByDescending(x => x.TimestampEmision)
                .Skip(skip)
                .Take(pageSize)
                .ToList();
        }

        public async Task<IEnumerable<LogEntry>> GetRecentPublicEventsAsync(int count = 20)
        {
            if (_logsCollection != null)
            {
                try
                {
                    return await _logsCollection.Find(x => x.EsPublico)
                        .SortByDescending(x => x.TimestampEmision)
                        .Limit(count)
                        .ToListAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo consulta pública en MongoDB.");
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
            if (_logsCollection != null)
            {
                try
                {
                    var builder = Builders<LogEntry>.Filter;
                    var filter = builder.Regex(x => x.Origen, new BsonRegularExpression($"^{Regex.Escape(origen.Trim())}$", "i")) &
                                 builder.Regex(x => x.TipoEvento, new BsonRegularExpression($"^{Regex.Escape(tipoEvento.Trim())}$", "i")) &
                                 builder.Regex(x => x.UbicacionId, new BsonRegularExpression($"^{Regex.Escape(ubicacionId.Trim())}$", "i"));

                    var ultimo = await _logsCollection
                        .Find(filter)
                        .SortByDescending(x => x.TimestampEmision)
                        .FirstOrDefaultAsync();
                    return ultimo?.TimestampEmision;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo consulta de idempotencia en MongoDB.");
                }
            }

            var memUltimo = _inMemoryLogs
                .Where(x => x.Origen.Equals(origen.Trim(), StringComparison.OrdinalIgnoreCase) &&
                            x.TipoEvento.Equals(tipoEvento.Trim(), StringComparison.OrdinalIgnoreCase) &&
                            x.UbicacionId.Equals(ubicacionId.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.TimestampEmision)
                .FirstOrDefault();

            return memUltimo?.TimestampEmision;
        }

        public async Task<VueloPantalla?> GetVueloPantallaByIdAsync(string vueloId)
        {
            if (string.IsNullOrWhiteSpace(vueloId)) return null;
            string idNorm = vueloId.Trim().ToUpperInvariant();

            if (_vuelosCollection != null)
            {
                try
                {
                    var regexId = new BsonRegularExpression($"^{Regex.Escape(idNorm)}$", "i");
                    return await _vuelosCollection.Find(Builders<VueloPantalla>.Filter.Regex(x => x.VueloId, regexId)).FirstOrDefaultAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo lectura de vuelo {VueloId} en MongoDB.", idNorm);
                }
            }

            _inMemoryVuelos.TryGetValue(idNorm, out var vuelo);
            return vuelo;
        }

        public async Task UpsertVueloPantallaAsync(VueloPantalla vuelo)
        {
            vuelo.VueloId = vuelo.VueloId.Trim().ToUpperInvariant();
            _inMemoryVuelos[vuelo.VueloId] = vuelo;

            if (_vuelosCollection != null)
            {
                try
                {
                    await _vuelosCollection.ReplaceOneAsync(
                        x => x.VueloId == vuelo.VueloId,
                        vuelo,
                        new ReplaceOptions { IsUpsert = true });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo actualización de tablero de vuelos en MongoDB.");
                }
            }
        }

        public async Task<IEnumerable<VueloPantalla>> GetVuelosPantallaAsync()
        {
            if (_vuelosCollection != null)
            {
                try
                {
                    return await _vuelosCollection.Find(_ => true)
                        .SortByDescending(x => x.UltimaActualizacionUtc)
                        .Limit(50)
                        .ToListAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo lectura de tablero de vuelos en MongoDB.");
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

            if (_logsCollection != null)
            {
                try
                {
                    var builder = Builders<LogEntry>.Filter;
                    long total = await _logsCollection.CountDocumentsAsync(builder.Empty);
                    long otorgadas = await _logsCollection.CountDocumentsAsync(
                        builder.Regex(x => x.TipoEvento, new BsonRegularExpression("Otorgada", "i")));
                    long denegadas = await _logsCollection.CountDocumentsAsync(
                        builder.Regex(x => x.TipoEvento, new BsonRegularExpression("Denegada", "i")));
                    long alertas = await _logsCollection.CountDocumentsAsync(
                        builder.Regex(x => x.Severidad, new BsonRegularExpression("^Alerta$", "i")));
                    long errores = await _logsCollection.CountDocumentsAsync(
                        builder.Regex(x => x.Severidad, new BsonRegularExpression("^Error$", "i")));

                    var porOrigenRaw = await _logsCollection.Aggregate()
                      .Group(x => x.Origen, g => new { Origen = g.Key, Cantidad = g.Count() })
                      .ToListAsync();

                    var porComponente = porOrigenRaw
                        .Where(x => !string.IsNullOrWhiteSpace(x.Origen))
                        .GroupBy(x => x.Origen.Trim(), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.First().Origen.Trim(), g => g.Sum(x => x.Cantidad), StringComparer.OrdinalIgnoreCase);

                    return new DashboardResumenDto
                    {
                        TotalLogsRegistrados = (int)total,
                        AutorizacionesOtorgadas = (int)otorgadas,
                        AutorizacionesDenegadas = (int)denegadas,
                        AlertasActivas = (int)alertas,
                        ErroresRegistrados = (int)errores,
                        VuelosEnCurso = vuelosEnCurso,
                        LogsPorComponente = porComponente
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo cálculo de resumen en MongoDB. Calculando desde memoria local.");
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
    }
}