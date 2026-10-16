using Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Aplication.Abstractions
{
    public interface IPantallaNotifier
    {
        Task NotificarEventoAsync(LogEntry log);
    }
}
