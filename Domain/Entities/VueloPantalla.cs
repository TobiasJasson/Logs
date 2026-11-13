using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Entities
{
    public class VueloPantalla
    {
        public string VueloId { get; set; } = string.Empty;       // Identificador único (Clave en Mongo)
        public string TipoOperacion { get; set; } = "Salida";     // "Salida" o "Llegada"
        public string AerolineaOOrigen { get; set; } = "EZE";
        public string Destino { get; set; } = " Sin especificar";
        public string Horario { get; set; } = "--:--";
        public string Puerta { get; set; } = "-";
        public string Pista { get; set; } = "-";
        public string Estado { get; set; } = "Programado";        // Programado, Embarcando, Despegando, En Vuelo, Arribado, Demorado
        public string SeveridadVisual { get; set; } = "Informacion";
        public DateTime UltimaActualizacionUtc { get; set; } = DateTime.UtcNow;
    }
}
