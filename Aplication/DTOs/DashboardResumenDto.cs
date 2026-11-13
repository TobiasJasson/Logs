using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Aplication.DTOs
{
    public class DashboardResumenDto
    {
        public long TotalLogsRegistrados { get; set; }
        public int AutorizacionesOtorgadas { get; set; }
        public int AutorizacionesDenegadas { get; set; }
        public int AlertasActivas { get; set; }
        public int ErroresRegistrados { get; set; }
        public int VuelosEnCurso { get; set; }
        public Dictionary<string, int> LogsPorComponente { get; set; } = new();
    }
}
