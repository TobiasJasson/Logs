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
            => await _repository.GetFilteredAsync(filtro);

        public async Task<LogEntry?> ObtenerPorIdAsync(Guid id)
            => await _repository.GetByIdAsync(id);

        public async Task<IEnumerable<LogEntry>> ObtenerEventosPantallaPublicaAsync()
            => await _repository.GetRecentPublicEventsAsync(30);

        public async Task<IEnumerable<VueloPantalla>> ObtenerTableroVuelosAsync()
            => await _repository.GetVuelosPantallaAsync();

        public async Task<DashboardResumenDto> ObtenerResumenDashboardAsync()
            => await _repository.GetResumenDashboardAsync();
    }
}
