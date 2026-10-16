using Aplication.Abstractions;
using Domain.Entities;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Infrastructure.Notifications
{
    public class SsePantallaNotifier : IPantallaNotifier
    {
        private readonly ConcurrentDictionary<Guid, StreamWriter> _subscriptores = new();

        public Guid SuscribirCliente(StreamWriter writer)
        {
            var id = Guid.NewGuid();
            _subscriptores.TryAdd(id, writer);
            return id;
        }

        public void DesuscribirCliente(Guid id)
        {
            _subscriptores.TryRemove(id, out _);
        }

        public async Task NotificarEventoAsync(LogEntry log)
        {
            var json = JsonSerializer.Serialize(log, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            foreach (var sub in _subscriptores)
            {
                try
                {
                    await sub.Value.WriteAsync($"data: {json}\n\n");
                    await sub.Value.FlushAsync();
                }
                catch
                {
                    _subscriptores.TryRemove(sub.Key, out _);
                }
            }
        }
    }
}
