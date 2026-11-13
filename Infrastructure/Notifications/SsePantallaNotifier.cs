using Aplication.Abstractions;
using Domain.Entities;
using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Infrastructure.Notifications
{
    public class SseSubscriber
    {
        public Channel<string> Canal { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        public bool SoloPublicos { get; init; }
    }

    public class SsePantallaNotifier : IPantallaNotifier
    {
        private readonly ConcurrentDictionary<Guid, SseSubscriber> _subscriptores = new();
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public (Guid Id, ChannelReader<string> Reader) SuscribirCliente(bool soloPublicos)
        {
            var id = Guid.NewGuid();
            var sub = new SseSubscriber { SoloPublicos = soloPublicos };
            _subscriptores.TryAdd(id, sub);
            return (id, sub.Canal.Reader);
        }

        public void DesuscribirCliente(Guid id)
        {
            if (_subscriptores.TryRemove(id, out var sub))
            {
                sub.Canal.Writer.TryComplete();
            }
        }

        public Task NotificarEventoAsync(LogEntry log, VueloPantalla? vueloActualizado = null)
        {
            // Payload limpio para pantallas públicas (sin PayloadJson interno)
            var eventoPublico = new
            {
                id = log.Id,
                origen = log.Origen,
                tipoEvento = log.TipoEvento,
                severidad = log.Severidad,
                mensaje = log.Mensaje,
                ubicacionId = log.UbicacionId,
                timestampEmision = log.TimestampEmision,
                esPublico = log.EsPublico,
                vuelo = vueloActualizado
            };

            string jsonPublico = JsonSerializer.Serialize(eventoPublico, _jsonOptions);
            string jsonInterno = JsonSerializer.Serialize(new { log, vuelo = vueloActualizado }, _jsonOptions);

            foreach (var kvp in _subscriptores)
            {
                var sub = kvp.Value;
                if (sub.SoloPublicos)
                {
                    if (log.EsPublico)
                    {
                        sub.Canal.Writer.TryWrite(jsonPublico);
                    }
                }
                else
                {
                    sub.Canal.Writer.TryWrite(jsonInterno);
                }
            }

            return Task.CompletedTask;
        }
    }
}