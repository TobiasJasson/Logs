using Aplication.Abstractions;
using Aplication.DTOs;
using Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Aplication.UseCases
{
    public class ConsultarLogsUseCase
    {
        private readonly ILogRepository _repository;

        public ConsultarLogsUseCase(ILogRepository repository)
        {
            _repository = repository;
        }

        public async Task<IEnumerable<LogEntry>> EjecutarAsync(LogFilterDto filtro)
        {
            return await _repository.GetFilteredAsync(filtro);
        }

        public async Task<IEnumerable<LogEntry>> ObtenerEventosPantallaPublicaAsync()
        {
            return await _repository.GetRecentPublicEventsAsync(30);
        }
    }
}
