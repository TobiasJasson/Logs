using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Aplication.DTOs
{
    public class DirectLogRequestDto
    {
        public string Origen { get; set; } = string.Empty;      // Nombre del grupo/componente
        public string UbicacionId { get; set; } = string.Empty; // ID de Vuelo, Pista o Label del método
        public string Severidad { get; set; } = "Informacion";  // "Alerta", "Error", "Informacion", "Aprobado"
        public string TipoEvento { get; set; } = "LogManual";
        public string Mensaje { get; set; } = string.Empty;
        public bool MostrarEnPantallaPublica { get; set; } = false;
    }
}
