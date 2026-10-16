using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Aplication.Config
{
    public class LogsBusinessConfig
    {
        public int DiasRetencionLogs { get; set; } = 30;

        public HashSet<string> EventosPublicos { get; set; } = new()
        {
            "EstadoVueloCambiado",
            "VueloCreado",
            "PistaBloqueada",
            "PistaLiberada",
            "AutorizacionOtorgada",
            "AutorizacionDenegada",
            "ReporteClimatico"
        };
    }
}
