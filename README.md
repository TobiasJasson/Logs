# Grupo 8 — Microservicio de Logs Centralizados, Notificaciones y Pantallas Públicas

Sistema desarrollado para el Trabajo Práctico Integrador de **Modelos Computacionales de Gestión Administrativa / Metodologías y Desarrollos Web (UAI)**.

---

## 1. Responsabilidad del Módulo y Qué Hace la API

El microservicio del **Grupo 8** es el núcleo de observabilidad, auditoría e información pública en tiempo real del ecosistema aeroportuario. Sus responsabilidades principales son:

1. **Ingesta Asíncrona Centralizada (Service Bus RabbitMQ):** Escucha todos los eventos publicados en el exchange `aeropuerto_events` (routing key `#`) por los Grupos 1 al 6 (Torre de Control, Gestión de Vuelos, Pistas y Aeronaves, Check-in, Equipaje y Meteorología).
2. **Ingesta Síncrona Directa (API REST / SDK):** Expone el endpoint `POST /api/logs` para que cualquier microservicio reporte fallas críticas de comunicación (como un timeout de 5 segundos luego de 3 reintentos) o dispare notificaciones visuales inmediatas.
3. **Garantía de Idempotencia y Orden:** Filtra y descarta eventos duplicados (por `EventId`) o eventos que lleguen desordenados/atrasados en el tiempo para una misma entidad `(Origen, TipoEvento, UbicacionId)`, truncando la marca de tiempo a precisión de milisegundos.
4. **Persistencia con Retención Automática (MongoDB + Fallback en Memoria):** Almacena todas las bitácoras en MongoDB (`AeropuertoLogsDb`) con índices optimizados y expiración automática (TTL configurable de 30 días). Si MongoDB no está disponible en el entorno local, conmuta automáticamente a un almacenamiento en memoria acotado (*thread-safe*) para nunca interrumpir la operatoria.
5. **Exposición de Consultas y Métricas para Administración (Grupo 9):** Provee endpoints paginados, filtrables y un endpoint de resumen ejecutivo (`/api/logs/resumen`) con totales por severidad, vuelos activos y desglose por grupo emisor.
6. **Pantallas Públicas de Salidas/Llegadas y PopUps en Tiempo Real (SSE):** Alimenta mediante **Server-Sent Events (SSE)** dos canales independientes en tiempo real:
   * **Canal Público (`/api/pantallas/stream`):** Actualiza al instante la grilla de Salidas y Llegadas por vuelo y los avisos públicos sin exponer errores internos ni datos sensibles.
   * **Canal Interno (`/api/logs/stream-interno`):** Dispara alertas visuales (PopUps de *Información*, *Aprobado*, *Alerta* y *Error Crítico*) y actualiza la bitácora técnica en vivo.

---

## 2. Arquitectura y Patrones de Diseño Utilizados

La solución está construida en **.NET 8.0 (C#)** aplicando **Clean Architecture (Arquitectura Limpia / Cebolla)** dividida en 4 proyectos independientes, respetando la **Regla de Oro**: las dependencias apuntan estrictamente hacia el interior (`API -> Infrastructure -> Aplication -> Domain`).

### 2.1. Estructura de las 4 Capas

```text
Logs/
├── Domain/                             # Capa de Dominio (0 dependencias externas)
│   ├── Entities/
│   │   ├── LogEntry.cs                 # Entidad raíz con validación e invariantes
│   │   └── VueloPantalla.cs            # Proyección consolidada de estado de vuelo
│   └── Enums/
│       └── Serveridad.cs               # NivelSeveridad (Informacion, Aprobado, Alerta, Error)
├── Aplication/                         # Capa de Aplicación (Solo referencia a Domain)
│   ├── Abstractions/                   # Puertos (Interfaces)
│   │   ├── ILogRepository.cs
│   │   └── IPantallaNotifier.cs
│   ├── Config/
│   │   └── LogsBusinessConfig.cs       # Retención y lista blanca de EventosPublicos
│   ├── DTOs/                           # Objetos de Transferencia de Datos planos
│   │   ├── BusEventEnvelopeDto.cs
│   │   ├── DashboardResumenDto.cs
│   │   ├── DirectLogRequestDto.cs
│   │   └── LogFilterDto.cs
│   └── UseCases/                       # Casos de Uso (Reglas de aplicación)
│       ├── ConsultarLogsUseCase.cs
│       └── ProcesarEventoUseCase.cs
├── Infrastructure/                     # Capa de Infraestructura (Adaptadores concretos)
│   ├── Messaging/
│   │   └── RabbitMqConsumerHostedService.cs
│   ├── Notifications/
│   │   └── SsePantallaNotifier.cs
│   └── Repositories/
│       └── MongoLogRepository.cs
├── Logs/ (API)                         # Capa de Presentación HTTP (Minimal APIs)
│   ├── wwwroot/                        # Frontend Autónomo (Separación de Responsabilidades)
│   │   ├── index.html                  # Estructura semántica
│   │   ├── style.css                   # Estilos y animaciones de PopUps
│   │   └── app.js                      # Lógica Fetch + EventSource (SSE)
│   ├── appsettings.json
│   ├── Dockerfile
│   └── Program.cs                      # Composición de DI y 10 Endpoints Minimal API
└── docker-compose.yml                  # Orquestación de contenedores (API + MongoDB 7.0)
2.2. Patrones de Diseño y Principios SOLID Aplicados
Inversión de Dependencias (DIP - SOLID): Los casos de uso en Aplication dependen exclusivamente de las abstracciones ILogRepository e IPantallaNotifier. La inyección concreta (MongoLogRepository y SsePantallaNotifier) se resuelve en un único punto de composición dentro de Program.cs.

Principio de Responsabilidad Única (SRP - SOLID): Separación estricta entre la ingesta y validación de idempotencia (ProcesarEventoUseCase) y la lectura/agregación de métricas (ConsultarLogsUseCase). En el Frontend, separación total en index.html, style.css y app.js.

Principio Abierto/Cerrado (OCP - SOLID): La clasificación de qué eventos son públicos se parametriza mediante configuración (BusinessConfig:EventosPublicos en appsettings.json) sin necesidad de recompilar el código para agregar nuevos eventos públicos.

Patrón Repository con Fallback Transparente: MongoLogRepository encapsula el acceso a MongoDB.Driver (configurando serialización estándar de GUIDs y creación automática de 3 índices: TTL, EventId único e idempotencia compuesta) y ofrece degradación elegante (graceful degradation) a una cola concurrente acotada en RAM si el motor Mongo no responde.

Patrón Publisher/Subscriber (Pub/Sub) & Productor-Consumidor (System.Threading.Channels):

Externo: RabbitMqConsumerHostedService (BackgroundService) consume de forma asíncrona desde RabbitMQ con control de flujo (BasicQos prefetch) y confirmación manual (BasicAck / BasicNack).

Interno (SSE Thread-Safe): SsePantallaNotifier asigna un Channel<string> independiente por cada cliente HTTP conectado, evitando condiciones de carrera sobre el HttpContext.Response.Body y enviando heartbeats cada 15 segundos para evitar cierres de conexión en el API Gateway (Nginx/YARP).

Patrón Data Transfer Object (DTO): Aísla las entidades de dominio de los contratos JSON de entrada/salida y oculta datos internos en el stream público.

Minimal APIs: Eliminación completa de controladores pesados (Controllers) en favor de rutas declarativas de alto rendimiento mapeadas directamente en Program.cs.

3. Cómo Ejecutar el Proyecto (Local y Docker)
Opción A: Con Docker Compose (Recomendado para Integración)
Desde la raíz de la solución (donde se encuentra docker-compose.yml):
docker-compose up --build -d
API y Pantallas Públicas: http://localhost:5008

Swagger UI: http://localhost:5008/swagger

MongoDB: Puerto 27017 (contenedor grupo8_mongodb)

Opción B: Ejecución Local (Visual Studio 2022 / .NET CLI)
dotnet run --project Logs/API.csproj
Pantallas Públicas y Simulador: http://localhost:5097 (o https://localhost:7078)

Swagger UI: http://localhost:5097/swagger

4. Referencia Completa de Endpoints, Ejemplos de Uso y Respuestas Esperadas
4.1. GET /health — Estado de Salud del Microservicio
Verifica la disponibilidad del servicio para Docker y el API Gateway.

Respuesta Exitosa (200 OK):
{
  "status": "Healthy",
  "service": "Grupo8-LogsNotificaciones",
  "timestamp": "2026-11-12T23:00:00.000Z"
}
4.2. POST /api/logs — Registro Directo de Log / Disparo de PopUp y Actualización de Vuelo
Utilizado por todos los grupos cuando desean registrar un evento directamente vía REST (por ejemplo, tras agotar 3 reintentos con timeout de 5s) o actualizar el tablero público de vuelos.

Ejemplo de Petición (Request Body):
{
  "origen": "GestionVuelos",
  "ubicacionId": "AR1234",
  "severidad": "Aprobado",
  "tipoEvento": "EstadoVueloCambiado",
  "mensaje": "Vuelo AR1234 embarcando en Puerta B4",
  "mostrarEnPantallaPublica": true,
  "destino": "Madrid (MAD)",
  "horario": "18:45",
  "puerta": "B4",
  "estadoVuelo": "Embarcando",
  "tipoOperacion": "Salida"
}
Respuesta Esperada (201 Created):
{
  "id": "8b7d4c12-9a11-4f32-8c90-1a2b3c4d5e6f",
  "eventId": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
  "origen": "GestionVuelos",
  "ubicacionId": "AR1234",
  "tipoEvento": "EstadoVueloCambiado",
  "severidad": "Aprobado",
  "mensaje": "Vuelo AR1234 embarcando en Puerta B4",
  "timestampEmision": "2026-11-12T23:05:10.125Z",
  "timestampRecepcion": "2026-11-12T23:05:10.125Z",
  "payloadRaw": "{\"origen\":\"GestionVuelos\",\"ubicacionId\":\"AR1234\",...}",
  "esPublico": true
}
Respuesta ante Datos Inválidos (400 Bad Request):
{
  "error": "El origen es obligatorio."
}
4.3. POST /api/logs/envelope — Ingesta de Envelope Estándar del Bus (Pruebas de Idempotencia)
Recibe exactamente el mismo contrato JSON que viaja por RabbitMQ. Permite verificar la regla de idempotencia vía HTTP sin requerir un broker activo.

Ejemplo de Petición (Request Body):
{
  "eventId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "tipo": "EstadoVueloCambiado",
  "origen": "TorreControl",
  "timestamp": "2026-11-12T23:10:00Z",
  "payload": {
    "vueloId": "AR1234",
    "destino": "Madrid (MAD)",
    "horario": "18:45",
    "puerta": "B4",
    "pista": "09L",
    "tipoOperacion": "Salida",
    "estadoNuevo": "Despegando",
    "mensaje": "Autorización de despegue otorgada en Pista 09L"
  }
}
Respuesta Esperada (1° envío — 200 OK):
{
  "procesado": true,
  "eventId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
Respuesta Esperada (2° envío repetido o con timestamp anterior — 200 OK):
{
  "procesado": false,
  "motivo": "Descartado por idempotencia (evento duplicado o antiguo)."
}
4.4. GET /api/logs — Consulta Filtrada y Paginada (Para Grupo 9 - Panel Admin)
Permite consultar la bitácora combinando filtros opcionales por Query String: fechaDesde, fechaHasta, origen, tipo, severidad, page (default 1) y pageSize (default 50).

4.5. GET /api/logs/{id} — Consulta de un Log por su GUID
Obtiene el detalle completo de un registro puntual (200 OK o 404 Not Found).

4.6. GET /api/logs/resumen — Métricas Ejecutivas para el Dashboard (Grupo 9)
Respuesta Esperada (200 OK):
{
  "totalEventos": 42,
  "autorizacionesOtorgadas": 15,
  "autorizacionesDenegadas": 2,
  "alertasActivas": 4,
  "erroresCriticos": 1,
  "vuelosActivosEnPantalla": 6,
  "eventosPorOrigen": {
    "GestionVuelos": 18,
    "TorreControl": 14,
    "Meteorologia": 6,
    "Equipaje": 4
  },
  "eventosPorSeveridad": {
    "Informacion": 20,
    "Aprobado": 17,
    "Alerta": 4,
    "Error": 1
  }
}
4.7. GET /api/pantallas/vuelos — Grilla Consolidada de Salidas y Llegadas
Respuesta Esperada (200 OK):
[
  {
    "vueloId": "AR1234",
    "origen": "EZE",
    "destino": "Madrid (MAD)",
    "horario": "18:45",
    "puerta": "B4",
    "pista": "09L",
    "tipoOperacion": "Salida",
    "estado": "Despegando",
    "severidadVisual": "Aprobado",
    "ultimaActualizacion": "2026-11-12T23:10:00Z"
  }
]
4.8. GET /api/pantallas/eventos — Últimos Avisos Públicos
Retorna los últimos 30 eventos marcados como públicos (EsPublico == true).

4.9. Canales de Streaming en Tiempo Real (Server-Sent Events - SSE)
GET /api/pantallas/stream (Público): Emite eventos text/event-stream destinados a las pantallas de pasajeros.

GET /api/logs/stream-interno (Interno): Emite el flujo completo de logs y errores para el monitor de administración y disparo de PopUps.

5. Escalabilidad Horizontal
El diseño permite levantar múltiples réplicas del contenedor grupo8-logs-api detrás del API Gateway:

Ingesta desde RabbitMQ: Todas las instancias consumen de la cola compartida grupo8_logs_queue bajo el patrón Competing Consumers, garantizando idempotencia mediante el índice único sobre EventId en MongoDB.

Difusión SSE Multi-Nodo: Cada instancia mantiene sus propios canales en memoria para sus clientes SSE conectados y recibe una copia de los eventos públicos mediante colas exclusivas vinculadas al exchange aeropuerto_events.