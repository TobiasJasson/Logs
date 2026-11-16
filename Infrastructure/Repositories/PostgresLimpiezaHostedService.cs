using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Infrastructure.Repositories
{
    public class PostgresLimpiezaHostedService : BackgroundService
    {
        private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(10);

        private readonly PostgresLogRepository _repository;
        private readonly ILogger<PostgresLimpiezaHostedService> _logger;

        public PostgresLimpiezaHostedService(PostgresLogRepository repository, ILogger<PostgresLimpiezaHostedService> logger)
        {
            _repository = repository;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(Intervalo);
            do
            {
                try
                {
                    int eliminados = await _repository.EliminarLogsVencidosAsync();
                    if (eliminados > 0)
                        _logger.LogInformation("Limpieza de retención: {Cantidad} logs vencidos eliminados de PostgreSQL.", eliminados);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo la limpieza de logs vencidos en PostgreSQL. Se reintenta en {Minutos} minutos.", Intervalo.TotalMinutes);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }
}