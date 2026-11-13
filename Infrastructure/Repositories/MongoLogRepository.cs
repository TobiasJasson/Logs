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
using System.Threading.Tasks;

namespace Infrastructure.Repositories
{
    public class MongoLogRepository : ILogRepository
    {
        private static readonly object _bsonLock = new();
        private static bool _bsonConfigured = false;

        private readonly IMongoCollection<LogEntry>? _logsCollection;
        private readonly IMongoCollection<VueloPantalla>? _vuelosCollection;
        private readonly ConcurrentQueue<LogEntry> _fallbackMemory = new();
        private readonly ConcurrentDictionary<string, VueloPantalla> _fallbackVuelos = new();
        private readonly ILogger<MongoLogRepository> _logger;
        private readonly bool _useMongo;
        private const int MaxFallbackItems = 1000;

        public MongoLogRepository(IConfiguration config, ILogger<MongoLogRepository> logger)
        {
            _logger = logger;
            ConfigurarBsonSerializers();

            var connString = config["MongoDb:ConnectionString"];
            var dbName = config["MongoDb:DatabaseName"] ?? "AeropuertoLogsDb";

            try
            {
                if (!string.IsNullOrWhiteSpace(connString))
                {
                    var settings = MongoClientSettings.FromConnectionString(connString);
                    settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
                    var client = new MongoClient(settings);
                    var database = client.GetDatabase(dbName);

                    _logsCollection = database.GetCollection<LogEntry>("LogsCentralizados");
                    _vuelosCollection = database.GetCollection<VueloPantalla>("TableroVuelos");

                    // 1. Índice TTL por ExpiraEnUtc
                    var ttlIndex = new CreateIndexModel<LogEntry>(
                        Builders<LogEntry>.IndexKeys.Ascending(x => x.ExpiraEnUtc),
                        new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "idx_ttl_expiracion" });

                    // 2. Índice único por EventId
                    var eventIdIndex = new CreateIndexModel<LogEntry>(
                        Builders<LogEntry>.IndexKeys.Ascending(x => x.EventId),
                        new CreateIndexOptions { Unique = true, Name = "idx_unique_eventId" });

                    // 3. Índice compuesto para consulta rápida de Idempotencia
                    var idempotenciaIndex = new CreateIndexModel<LogEntry>(
                        Builders<LogEntry>.IndexKeys
                            .Ascending(x => x.Origen)
                            .Ascending(x => x.TipoEvento)
                            .Ascending(x => x.UbicacionId)
                            .Descending(x => x.TimestampEmision),
                        new CreateIndexOptions { Name = "idx_idempotencia" });

                    _logsCollection.Indexes.CreateMany(new[] { ttlIndex, eventIdIndex, idempotenciaIndex });
                    _useMongo = true;
                    _logger.LogInformation("MongoDB conectado e índices configurados en {Database}", dbName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MongoDB no disponible ({Message}). Operando con memoria local acotada.", ex.Message);
                _useMongo = false;
            }
        }

        private static void ConfigurarBsonSerializers()
        {
            lock (_bsonLock)
            {
                if (_bsonConfigured) return;
                try
                {
                    BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
                    BsonClassMap.RegisterClassMap<VueloPantalla>(cm =>
                    {
                        cm.AutoMap();
                        cm.MapIdProperty(c => c.VueloId);
                    });
                }
                catch { /* Ya registrado */ }
                _bsonConfigured = true;
            }
        }

        public async Task SaveAsync(LogEntry log)
        {
            if (_useMongo && _logsCollection != null)
            {
                try
                {
                    await _logsCollection.InsertOneAsync(log);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error guardando en MongoDB para EventId {EventId}. Guardando en fallback.", log.EventId);
                }
            }

            _fallbackMemory.Enqueue(log);
            while (_fallbackMemory.Count > MaxFallbackItems)
            {
                _fallbackMemory.TryDequeue(out _);
            }
        }

        public async Task<LogEntry?> GetByIdAsync(Guid id)
        {
            if (_useMongo && _logsCollection != null)
            {
                try { return await _logsCollection.Find(x => x.Id == id).FirstOrDefaultAsync(); }
                catch { /* Fallback */ }
            }
            return _fallbackMemory.FirstOrDefault(x => x.Id == id);
        }

        public async Task<bool> ExistsEventIdAsync(Guid eventId)
        {
            if (_useMongo && _logsCollection != null)
            {
                try { return await _logsCollection.Find(x => x.EventId == eventId).AnyAsync(); }
                catch { /* Fallback */ }
            }
            return _fallbackMemory.Any(x => x.EventId == eventId);
        }

        public async Task<IEnumerable<LogEntry>> GetFilteredAsync(LogFilterDto filter)
        {
            int page = Math.Max(1, filter.Page);
            int pageSize = Math.Clamp(filter.PageSize, 1, 200);
            int skip = (page - 1) * pageSize;

            if (_useMongo && _logsCollection != null)
            {
                try
                {
                    var builder = Builders<LogEntry>.Filter;
                    var mongoFilter = builder.Empty;

                    if (filter.FechaDesde.HasValue)
                        mongoFilter &= builder.Gte(x => x.TimestampEmision, filter.FechaDesde.Value);
                    if (filter.FechaHasta.HasValue)
                        mongoFilter &= builder.Lte(x => x.TimestampEmision, filter.FechaHasta.Value);
                    if (!string.IsNullOrWhiteSpace(filter.Origen))
                        mongoFilter &= builder.Eq(x => x.Origen, filter.Origen);
                    if (!string.IsNullOrWhiteSpace(filter.TipoEvento))
                        mongoFilter &= builder.Eq(x => x.TipoEvento, filter.TipoEvento);
                    if (!string.IsNullOrWhiteSpace(filter.Severidad))
                        mongoFilter &= builder.Eq(x => x.Severidad, filter.Severidad);

                    return await _logsCollection.Find(mongoFilter)
                        .SortByDescending(x => x.TimestampEmision)
                        .Skip(skip)
                        .Limit(pageSize)
                        .ToListAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Consulta en MongoDB falló, leyendo de memoria.");
                }
            }

            IEnumerable<LogEntry> query = _fallbackMemory;
            if (filter.FechaDesde.HasValue) query = query.Where(x => x.TimestampEmision >= filter.FechaDesde.Value);
            if (filter.FechaHasta.HasValue) query = query.Where(x => x.TimestampEmision <= filter.FechaHasta.Value);
            if (!string.IsNullOrWhiteSpace(filter.Origen)) query = query.Where(x => x.Origen.Equals(filter.Origen, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(filter.TipoEvento)) query = query.Where(x => x.TipoEvento.Equals(filter.TipoEvento, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(filter.Severidad)) query = query.Where(x => x.Severidad.Equals(filter.Severidad, StringComparison.OrdinalIgnoreCase));

            return query.OrderByDescending(x => x.TimestampEmision).Skip(skip).Take(pageSize).ToList();
        }

        public async Task<IEnumerable<LogEntry>> GetRecentPublicEventsAsync(int limit = 20)
        {
            if (_useMongo && _logsCollection != null)
            {
                try
                {
                    return await _logsCollection.Find(x => x.EsPublico)
                        .SortByDescending(x => x.TimestampEmision)
                        .Limit(limit)
                        .ToListAsync();
                }
                catch { /* Fallback */ }
            }

            return _fallbackMemory.Where(x => x.EsPublico)
                .OrderByDescending(x => x.TimestampEmision)
                .Take(limit)
                .ToList();
        }

        public async Task<DateTime?> GetLastTimestampAsync(string origen, string tipoEvento, string ubicacionId)
        {
            if (_useMongo && _logsCollection != null)
            {
                try
                {
                    var ultimo = await _logsCollection
                        .Find(x => x.Origen == origen && x.TipoEvento == tipoEvento && x.UbicacionId == ubicacionId)
                        .SortByDescending(x => x.TimestampEmision)
                        .FirstOrDefaultAsync();
                    return ultimo?.TimestampEmision;
                }
                catch { /* Fallback */ }
            }

            var local = _fallbackMemory
                .Where(x => x.Origen.Equals(origen, StringComparison.OrdinalIgnoreCase) &&
                            x.TipoEvento.Equals(tipoEvento, StringComparison.OrdinalIgnoreCase) &&
                            x.UbicacionId.Equals(ubicacionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.TimestampEmision)
                .FirstOrDefault();

            return local?.TimestampEmision;
        }

        public async Task UpsertVueloPantallaAsync(VueloPantalla vuelo)
        {
            _fallbackVuelos[vuelo.VueloId] = vuelo;
            if (_useMongo && _vuelosCollection != null)
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
                    _logger.LogWarning(ex, "No se pudo actualizar VueloPantalla {VueloId} en MongoDB", vuelo.VueloId);
                }
            }
        }

        public async Task<IEnumerable<VueloPantalla>> GetVuelosPantallaAsync()
        {
            if (_useMongo && _vuelosCollection != null)
            {
                try
                {
                    return await _vuelosCollection.Find(_ => true)
                        .SortByDescending(x => x.UltimaActualizacionUtc)
                        .Limit(50)
                        .ToListAsync();
                }
                catch { /* Fallback */ }
            }
            return _fallbackVuelos.Values.OrderByDescending(x => x.UltimaActualizacionUtc).ToList();
        }

        public async Task<DashboardResumenDto> GetResumenDashboardAsync()
        {
            var todos = (await GetFilteredAsync(new LogFilterDto { Page = 1, PageSize = 200 })).ToList();
            var vuelos = (await GetVuelosPantallaAsync()).ToList();

            return new DashboardResumenDto
            {
                TotalLogsRegistrados = todos.Count,
                AutorizacionesOtorgadas = todos.Count(x => x.TipoEvento.Contains("Otorgada", StringComparison.OrdinalIgnoreCase) || x.Severidad == "Aprobado"),
                AutorizacionesDenegadas = todos.Count(x => x.TipoEvento.Contains("Denegada", StringComparison.OrdinalIgnoreCase)),
                AlertasActivas = todos.Count(x => x.Severidad == "Alerta"),
                ErroresRegistrados = todos.Count(x => x.Severidad == "Error"),
                VuelosEnCurso = vuelos.Count(v => !v.Estado.Equals("Arribado", StringComparison.OrdinalIgnoreCase)),
                LogsPorComponente = todos.GroupBy(x => x.Origen).ToDictionary(g => g.Key, g => g.Count())
            };
        }
    }
}