using Aplication.DTOs;
using Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Aplication.Abstractions
{
    public interface ILogRepository
    {
        Task SaveAsync(LogEntry log);
        Task<LogEntry?> GetByIdAsync(Guid id);
        Task<bool> ExistsEventIdAsync(Guid eventId);
        Task<IEnumerable<LogEntry>> GetFilteredAsync(LogFilterDto filter);
        Task<IEnumerable<LogEntry>> GetRecentPublicEventsAsync(int count = 20);
        Task<DateTime?> GetLastTimestampAsync(string origen, string tipoEvento, string ubicacionId);
        Task<VueloPantalla?> GetVueloPantallaByIdAsync(string vueloId);
        Task UpsertVueloPantallaAsync(VueloPantalla vuelo);
        Task<IEnumerable<VueloPantalla>> GetVuelosPantallaAsync();
        Task<DashboardResumenDto> GetResumenDashboardAsync();
    }
}