using Aplication.DTOs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Domain.Entities;

namespace Aplication.Abstractions
{
    public interface ILogRepository
    {
        Task SaveAsync(LogEntry log);
        Task<LogEntry?> GetByIdAsync(Guid id);
        Task<bool> ExistsEventIdAsync(Guid eventId);
        Task<IEnumerable<LogEntry>> GetFilteredAsync(LogFilterDto filter);
        Task<IEnumerable<LogEntry>> GetRecentPublicEventsAsync(int limit = 20);
        Task<DateTime?> GetLastTimestampAsync(string origen, string tipoEvento, string ubicacionId);
        Task UpsertVueloPantallaAsync(VueloPantalla vuelo);
        Task<IEnumerable<VueloPantalla>> GetVuelosPantallaAsync();
        Task<DashboardResumenDto> GetResumenDashboardAsync();
    }
}
