using Aplication.Abstractions;
using Aplication.DTOs;
using Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
        private readonly IMongoCollection<LogEntry>? _collection;
        private readonly ConcurrentBag<LogEntry> _fallbackMemory = new();
        private readonly ILogger<MongoLogRepository> _logger;
        private readonly bool _useMongo;

        public MongoLogRepository(IConfiguration config, ILogger<MongoLogRepository> logger)
        {
            _logger = logger;
            var connString = config["MongoDb:ConnectionString"];
            var dbName = config["MongoDb:DatabaseName"] ?? "AeropuertoLogsDb";

            try
            {
                if (!string.IsNullOrWhiteSpace(connString))
                {
                    var client = new MongoClient(connString);
                    var database = client.GetDatabase(dbName);
                    _collection = database.GetCollection<LogEntry>("LogsCentralizados");

                    // Crear índice TTL para borrar logs automáticamente cuando se cumpla ExpiraEnUtc
                    var indexKeys = Builders<LogEntry>.IndexKeys.Ascending(x => x.ExpiraEnUtc);
                    var indexOptions = new CreateIndexOptions { ExpireAfter = TimeSpan.Zero };
                    _collection.Indexes.CreateOne(new CreateIndexModel<LogEntry>(indexKeys, indexOptions));
                    _useMongo = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"No se pudo conectar a MongoDB ({ex.Message}). Usando memoria local como fallback.");
                _useMongo = false;
            }
        }

        public async Task SaveAsync(LogEntry log)
        {
            _fallbackMemory.Add(log);
            if (_useMongo && _collection != null)
            {
                try { await _collection.InsertOneAsync(log); }
                catch (Exception ex) { _logger.LogError($"Error guardando en Mongo: {ex.Message}"); }
            }
        }

        public async Task<IEnumerable<LogEntry>> GetFilteredAsync(LogFilterDto filter)
        {
            IEnumerable<LogEntry> query = _fallbackMemory;

            if (_useMongo && _collection != null)
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

                    return await _collection.Find(mongoFilter)
                        .SortByDescending(x => x.TimestampEmision)
                        .Limit(200)
                        .ToListAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Consulta en Mongo falló, leyendo de memoria: {ex.Message}");
                }
            }

            if (filter.FechaDesde.HasValue) query = query.Where(x => x.TimestampEmision >= filter.FechaDesde.Value);
            if (filter.FechaHasta.HasValue) query = query.Where(x => x.TimestampEmision <= filter.FechaHasta.Value);
            if (!string.IsNullOrWhiteSpace(filter.Origen)) query = query.Where(x => x.Origen.Equals(filter.Origen, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(filter.TipoEvento)) query = query.Where(x => x.TipoEvento.Equals(filter.TipoEvento, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(filter.Severidad)) query = query.Where(x => x.Severidad.Equals(filter.Severidad, StringComparison.OrdinalIgnoreCase));

            return query.OrderByDescending(x => x.TimestampEmision).Take(200).ToList();
        }

        public async Task<IEnumerable<LogEntry>> GetRecentPublicEventsAsync(int limit = 20)
        {
            if (_useMongo && _collection != null)
            {
                try
                {
                    return await _collection.Find(x => x.EsPublico)
                        .SortByDescending(x => x.TimestampEmision)
                        .Limit(limit)
                        .ToListAsync();
                }
                catch { /* Fallback a memoria */ }
            }

            return _fallbackMemory.Where(x => x.EsPublico)
                .OrderByDescending(x => x.TimestampEmision)
                .Take(limit)
                .ToList();
        }

        public async Task<DateTime?> GetLastTimestampAsync(string origen, string tipoEvento)
        {
            if (_useMongo && _collection != null)
            {
                try
                {
                    var ultimo = await _collection.Find(x => x.Origen == origen && x.TipoEvento == tipoEvento)
                        .SortByDescending(x => x.TimestampEmision)
                        .FirstOrDefaultAsync();
                    return ultimo?.TimestampEmision;
                }
                catch { /* Fallback */ }
            }

            var local = _fallbackMemory
                .Where(x => x.Origen.Equals(origen, StringComparison.OrdinalIgnoreCase) &&
                            x.TipoEvento.Equals(tipoEvento, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.TimestampEmision)
                .FirstOrDefault();

            return local?.TimestampEmision;
        }
    }
}
