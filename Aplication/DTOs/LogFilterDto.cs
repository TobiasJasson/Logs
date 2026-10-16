using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Aplication.DTOs
{
    public class LogFilterDto
    {
        public DateTime? FechaDesde { get; set; }
        public DateTime? FechaHasta { get; set; }
        public string? Origen { get; set; }
        public string? TipoEvento { get; set; }
        public string? Severidad { get; set; }
    }
}
