using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Entities
{
    public class LogEntry
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid EventId { get; set; }
        public string Origen { get; set; } = string.Empty;      // Ej: "TorreControl", "GestionVuelos"
        public string TipoEvento { get; set; } = string.Empty;  // Ej: "AutorizacionOtorgada", "ErrorTimeout"
        public string Severidad { get; set; } = string.Empty;   // "Informacion", "Alerta", "Error", "Aprobado"
        public string Mensaje { get; set; } = string.Empty;     // Cuerpo descriptivo para la bitácora y el PopUp
        public string UbicacionId { get; set; } = string.Empty; // ID de vuelo, pista o método donde ocurrió
        public DateTime TimestampEmision { get; set; }          // Usado para idempotencia
        public DateTime FechaRegistroUtc { get; set; } = DateTime.UtcNow;
        public DateTime ExpiraEnUtc { get; set; }               // Para tiempo de retención de negocio
        public bool EsPublico { get; set; }                     // Indica si impacta en Pantallas Públicas
        public string PayloadJson { get; set; } = "{}";         // Datos crudos adicionales del evento
    }
}
