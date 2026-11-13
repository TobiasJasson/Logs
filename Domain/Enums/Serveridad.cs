using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Enums
{
    public enum NivelSeveridad
    {
        Informacion, // Azul: Eventos generales, clima normal, cambios rutinarios
        Aprobado,    // Verde: AutorizacionOtorgada, PistaLiberada, VueloCreado
        Alerta,      // Amarillo: Demoras, PistaOcupada, Viento fuerte
        Error        // Rojo: AutorizacionDenegada, PistaBloqueada, Timeouts entre servicios
    }
}
