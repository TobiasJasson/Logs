using System.Text.Json;

namespace Aplication.DTOs
{
    public class BusEventEnvelopeDto
    {
        public Guid EventId { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public string Origen { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public JsonElement Payload { get; set; }
    }
}
