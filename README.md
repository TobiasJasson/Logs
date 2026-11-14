# Servicio de Logs Centralizados y Notificaciones

**Aeropuerto Internacional UAI - Grupo 8 (Logs / Notificaciones y Pantallas)**

## Índice

1. [Descripción general](#1-descripción-general)
2. [Arquitectura](#2-arquitectura)
3. [Funcionamiento interno](#3-funcionamiento-interno)
4. [Requisitos](#4-requisitos)
5. [Configuración](#5-configuración)
6. [Instalación y ejecución](#6-instalación-y-ejecución)
7. [Modelo de datos](#7-modelo-de-datos)
8. [Reglas de negocio](#8-reglas-de-negocio)
9. [Guía de integración para los demás grupos](#9-guía-de-integración-para-los-demás-grupos)
10. [Referencia de la API](#10-referencia-de-la-api)
11. [Códigos de estado y manejo de errores](#11-códigos-de-estado-y-manejo-de-errores)
12. [Pruebas con Postman o Insomnia](#12-pruebas-con-postman-o-insomnia)
13. [Pantallas Públicas y simulador](#13-pantallas-públicas-y-simulador)
14. [Seguridad](#14-seguridad)
15. [Resolución de problemas](#15-resolución-de-problemas)
16. [Limitaciones conocidas](#16-limitaciones-conocidas)
17. [Referencia de componentes](#17-referencia-de-componentes)

---

## 1. Descripción general

Este repositorio contiene el servicio desarrollado por el **Grupo 8** del proyecto *Aeropuerto Internacional UAI*. Su función es actuar como **bitácora centralizada del sistema**, como **canal de notificaciones en tiempo real** y como **fuente de datos de las Pantallas Públicas** de salidas y llegadas.

Los demás grupos (Torre de Control, Gestión de Vuelos, Pistas y Aeronaves, Equipaje, Meteorología, Seguridad, Panel de Administración, entre otros) informan a este servicio los eventos relevantes de su operación. El servicio se encarga de:

1. **Registrar** cada evento en una base de datos centralizada (MongoDB) con una política de retención automática.
2. **Clasificar** cada evento según su severidad: `Informacion`, `Aprobado`, `Alerta` o `Error`.
3. **Descartar duplicados** y eventos llegados fuera de orden (idempotencia).
4. **Mantener un tablero de vuelos** con el estado actual de cada vuelo (una fila por vuelo), a partir de los eventos recibidos.
5. **Notificar en tiempo real** mediante Server-Sent Events (SSE) por dos canales separados: uno **público** para las Pantallas Públicas (solo eventos públicos, sin datos internos) y uno **interno** para la bitácora completa y los PopUps de monitoreo.
6. **Exponer una API REST de consulta** para que el Panel de Administración (Grupo 9) pueda listar y filtrar los registros y obtener un resumen de métricas para su dashboard.

### Vías de ingreso de eventos

| Vía | Descripción | Uso recomendado |
|---|---|---|
| **RabbitMQ** | Los grupos publican un mensaje con el *envelope* estándar en el exchange `aeropuerto_events`. | Integración asíncrona oficial entre grupos. |
| **API REST directa** (`POST /api/logs`) | El grupo envía un log ya estructurado (origen, severidad, mensaje, etc.). | Registro puntual de errores técnicos, avisos y diagnósticos. |
| **API REST envelope** (`POST /api/logs/envelope`) | Recibe el mismo envelope que el bus, pero por HTTP. | Pruebas de integración sin broker y casos en que RabbitMQ no esté disponible. |

### Vías de salida de información

| Vía | Descripción | Consumidor |
|---|---|---|
| **API REST de consulta** (`GET /api/logs`, `GET /api/logs/{id}`, `GET /api/logs/resumen`) | Listado filtrado y paginado, consulta puntual y métricas agregadas. | Grupo 9 (Panel de Administración). |
| **API REST de pantallas** (`GET /api/pantallas/vuelos`, `GET /api/pantallas/eventos`) | Estado actual del tablero de vuelos y últimos avisos públicos. | Pantallas Públicas (carga inicial). |
| **SSE público** (`GET /api/pantallas/stream`) | Eventos públicos en tiempo real, con la fila de vuelo actualizada cuando corresponde. | Pantallas Públicas. |
| **SSE interno** (`GET /api/logs/stream-interno`) | Todos los logs en tiempo real, con el contenido completo del registro. | Bitácora interna y PopUps de monitoreo. |

### Responsabilidades del servicio

El servicio **sí** realiza las siguientes tareas: recibir, validar, clasificar, deduplicar, persistir, expirar y difundir eventos; mantener el tablero de vuelos; permitir la consulta filtrada y paginada de los registros; y calcular un resumen de métricas.

El servicio **no** realiza las siguientes tareas: emitir tokens de autenticación (corresponde al Grupo 7), decidir reglas operativas del aeropuerto (corresponde a cada grupo emisor), administrar el ciclo de vida de los vuelos (corresponde al Grupo 2) ni reintentar el envío de eventos en nombre de otros grupos.

---

## 2. Arquitectura

La solución está desarrollada en **.NET 8** con **Arquitectura Limpia** (Clean Architecture) y aplicación del principio de inversión de dependencias. Las capas internas no conocen a las externas: los casos de uso dependen de interfaces y la infraestructura las implementa.

### Estructura del repositorio

```
Logs/
|-- Logs.slnx                              Solución
|-- Domain/                                Núcleo del dominio (sin dependencias)
|   |-- Entities/LogEntry.cs               Entidad del registro de log
|   |-- Entities/VueloPantalla.cs          Entidad de una fila del tablero de vuelos
|   `-- Enums/Serveridad.cs                Enumeración NivelSeveridad
|-- Aplication/                            Casos de uso y contratos
|   |-- Abstractions/
|   |   |-- ILogRepository.cs              Contrato de persistencia
|   |   `-- IPantallaNotifier.cs           Contrato de notificación en tiempo real
|   |-- Config/LogsBusinessConfig.cs       Retención y eventos públicos
|   |-- DTOs/
|   |   |-- BusEventEnvelopeDto.cs         Envelope estándar del bus
|   |   |-- DashboardResumenDto.cs         Métricas para el Panel de Administración
|   |   |-- DirectLogRequestDto.cs         Solicitud de log directo
|   |   `-- LogFilterDto.cs                Filtros y paginación de consulta
|   `-- UseCases/
|       |-- ProcesarEventoUseCase.cs       Ingesta de eventos (bus y directo)
|       `-- ConsultarLogsUseCase.cs        Consultas (logs, vuelos, resumen)
|-- Infrastructure/                        Implementaciones concretas
|   |-- Messaging/RabbitMqConsumerHostedService.cs
|   |-- Notifications/SsePantallaNotifier.cs
|   `-- Repositories/MongoLogRepository.cs
`-- Logs/                                  Proyecto web (API.csproj)
    |-- Program.cs                         Composición, middleware y endpoints
    |-- Dockerfile                         Imagen del servicio
    |-- Logs.http                          Peticiones de prueba
    |-- appsettings*.json                  Configuración
    |-- Properties/launchSettings.json     Perfiles de ejecución local
    `-- wwwroot/
        |-- index.html                     Pantallas Públicas, bitácora y simulador
        |-- app.js                         Lógica de la página (carga inicial, SSE, simulador)
        `-- style.css                      Estilos
```

### Dependencias entre proyectos

```
Logs (API)  ---->  Aplication  ---->  Domain
    |                  ^
    +----> Infrastructure  (implementa las interfaces de Aplication)
```

### Diagrama de flujo general

```
 Grupos emisores                         Servicio Grupo 8                              Consumidores
 ---------------                         ----------------                              ------------
 RabbitMQ (envelope) ---> RabbitMqConsumerHostedService --+
                                                          |
 POST /api/logs/envelope ---------------------------------+--> ProcesarEventoUseCase --> MongoLogRepository (MongoDB / memoria)
                                                          |            |
 POST /api/logs ------------------------------------------+            +--> SsePantallaNotifier --+--> /api/pantallas/stream    (Pantallas Públicas)
                                                                                                  +--> /api/logs/stream-interno (Bitácora / PopUps)

 GET /api/logs, /api/logs/{id}, /api/logs/resumen, /api/pantallas/* ---> ConsultarLogsUseCase --> MongoLogRepository
```

### Tecnologías

| Componente | Tecnología | Versión |
|---|---|---|
| Plataforma | .NET, ASP.NET Core Minimal API | 8.0 |
| Persistencia | MongoDB (`MongoDB.Driver`), colecciones `LogsCentralizados` y `TableroVuelos` | Driver 3.12.0 |
| Mensajería | RabbitMQ (`RabbitMQ.Client`), exchange tipo *topic* | Cliente 7.2.2 |
| Tiempo real | Server-Sent Events (SSE) con `System.Threading.Channels` | Incluido en .NET 8 |
| Autenticación | JWT Bearer (`Microsoft.AspNetCore.Authentication.JwtBearer`), tokens emitidos por el Grupo 7 | 8.0.10 |
| Documentación | Swagger / Swashbuckle (`Swashbuckle.AspNetCore`) | 6.6.2 |
| Abstracciones de infraestructura | `Microsoft.Extensions.Configuration.Abstractions`, `Hosting.Abstractions`, `Logging.Abstractions` | 8.0.0 |
| Contenedores | Docker (imágenes `mcr.microsoft.com/dotnet/sdk:8.0` y `aspnet:8.0`) | - |
| Interfaz web | HTML, CSS y JavaScript sin frameworks (`EventSource`, `fetch`) | - |

**Justificación de MongoDB:** el servicio recibe un volumen alto de escrituras con estructura variable (cada grupo define su propio `payload`), y MongoDB permite guardar ese contenido sin esquema fijo y eliminar los registros vencidos de forma automática mediante un índice TTL.

**Justificación de SSE:** las pantallas solo necesitan recibir datos del servidor (comunicación unidireccional). SSE funciona sobre HTTP estándar, atraviesa el API Gateway sin configuración especial y el navegador reconecta automáticamente con `EventSource`.

---

## 3. Funcionamiento interno

### 3.1 Inicio de la aplicación

Al iniciar, `Program.cs` realiza las siguientes acciones, en este orden:

1. Registra Swagger y una política CORS que permite cualquier origen, encabezado y método.
2. Configura la autenticación JWT Bearer con los valores `Jwt:Key`, `Jwt:Audience` y lee el indicador `Jwt:RequireJwtInLogs`.
3. Lee la sección `BusinessConfig` de `appsettings.json` y la registra como *singleton* (`LogsBusinessConfig`).
4. Registra como *singleton* el repositorio (`MongoLogRepository`) y el notificador SSE (`SsePantallaNotifier`, también expuesto como `IPantallaNotifier`).
5. Registra como *scoped* los casos de uso `ProcesarEventoUseCase` y `ConsultarLogsUseCase`.
6. Registra el consumidor de RabbitMQ como servicio en segundo plano (`BackgroundService`).
7. Activa CORS, archivos por defecto (`index.html`), archivos estáticos, Swagger, autenticación y autorización.
8. Publica los endpoints REST y SSE (sección 10).

Cuando se construye por primera vez, `MongoLogRepository`:

1. Registra el serializador de `Guid` en formato estándar (requerido por el driver 3.x) y define `VueloId` como clave (`_id`) de `VueloPantalla`.
2. Si existe `MongoDb:ConnectionString`, se conecta (con un tiempo máximo de selección de servidor de 3 segundos) y crea tres índices en `LogsCentralizados`:
   - `idx_ttl_expiracion`: índice TTL sobre `ExpiraEnUtc`, para que MongoDB elimine los logs vencidos.
   - `idx_unique_eventId`: índice único sobre `EventId`, para impedir eventos repetidos.
   - `idx_idempotencia`: índice compuesto `Origen + TipoEvento + UbicacionId + TimestampEmision (desc)`, para que la verificación de idempotencia sea rápida.
3. Si no hay cadena de conexión o MongoDB no responde, opera en modo de memoria local (sección 3.8).

### 3.2 Flujo A: log directo (`POST /api/logs`)

```
Cliente --> POST /api/logs --> ProcesarEventoUseCase.EjecutarDirectoAsync
                                  1. Valida que origen y mensaje no estén vacíos
                                  2. Normaliza la severidad
                                  3. Completa valores por defecto (tipo, ubicación)
                                  4. Calcula EsPublico
                                  5. Calcula ExpiraEnUtc (ahora + retención)
                                  6. Guarda el log en el repositorio
                                  7. Si corresponde, actualiza la fila del vuelo en el tablero
                                  8. Notifica por SSE (canal público y/o interno)
                               <-- 201 Created + LogEntry
```

Paso a paso:

1. El endpoint recibe un `DirectLogRequestDto` y llama a `EjecutarDirectoAsync`.
2. Si `origen` o `mensaje` están vacíos, se lanza `ArgumentException` y el endpoint responde `400`.
3. La severidad se normaliza sin distinguir mayúsculas: `error` se convierte en `Error`; `alerta` o `warning` en `Alerta`; `aprobado`, `ok` o `success` en `Aprobado`; cualquier otro valor, o un valor vacío, se guarda como `Informacion`.
4. Si no se informa `tipoEvento` se usa `LogManual` (si se envía vacío, `LogDirecto`); si no se informa `ubicacionId` se usa `General`.
5. El `timestampEmision` es la hora del servidor (UTC, truncada a milisegundos).
6. `esPublico` es verdadero si `mostrarEnPantallaPublica = true` o si el tipo está en la lista de eventos públicos.
7. Se guarda el log.
8. Si el cuerpo trae datos de vuelo (`estadoVuelo`, `destino`, `puerta` u `horario`) o el `tipoEvento` contiene la palabra `Vuelo`, y además `ubicacionId` no es `General`, se actualiza la fila del tablero cuyo `vueloId` es el `ubicacionId`. Los datos que no se envían se conservan de la fila existente (sección 8.6).
9. Se notifica por SSE y se responde `201 Created`.

En este flujo **no se aplica idempotencia**: cada llamada genera un nuevo registro.

### 3.3 Flujo B: envelope (RabbitMQ o `POST /api/logs/envelope`)

```
Publicador --> RabbitMQ (aeropuerto_events) --> grupo8_logs_queue --+
                                                                    +--> ProcesarEventoUseCase.EjecutarDesdeBusAsync
Cliente HTTP --> POST /api/logs/envelope ---------------------------+      1. Valida origen, tipo y timestamp
                                                                           2. Genera el eventId si no viene; descarta si ya existe
                                                                           3. Descarta si el timestamp no es posterior al último
                                                                              del mismo origen + tipo + ubicación
                                                                           4. Deduce la severidad a partir del tipo y del estado
                                                                           5. Determina si el evento es público
                                                                           6. Extrae la ubicación y el mensaje del payload
                                                                           7. Guarda el log
                                                                           8. Si corresponde, actualiza la fila del vuelo
                                                                           9. Notifica por SSE
```

Paso a paso:

1. Se valida que `origen` y `tipo` no estén vacíos y que `timestamp` esté informado. Si no, se lanza `ArgumentException` (`400` por REST, `Nack` por RabbitMQ).
2. El `timestamp` se convierte a UTC y se trunca a milisegundos, que es la precisión con la que MongoDB guarda las fechas.
3. Si el `eventId` no viene o es vacío, el servicio genera uno nuevo. Si viene y ya existe un registro con ese valor, el evento se descarta.
4. Se extrae la ubicación del `payload` (sección 8.5) y se busca el último `timestampEmision` registrado para la combinación `origen + tipo + ubicación`. Si el `timestamp` recibido es menor o igual, el evento se descarta.
5. Se deduce la severidad a partir del texto del `tipo` y del estado informado en el `payload` (`estadoNuevo` o, si no viene, `estado`) (sección 8.4), y se determina si es público (sección 8.2).
6. Se arma el `LogEntry` (con `origen` y `tipo` sin espacios al inicio ni al final), guardando el `payload` original como texto en `payloadJson`, y se persiste.
7. Si el evento corresponde a un vuelo, se actualiza su fila en el tablero combinando los datos nuevos con los existentes (sección 8.6).
8. Se notifica por SSE. El método devuelve `true` si el evento se guardó y `false` si se descartó.

### 3.4 Flujo C: consultas

| Endpoint | Método del caso de uso | Método del repositorio | Resultado |
|---|---|---|---|
| `GET /api/logs` | `ConsultarLogsUseCase.EjecutarAsync` | `GetFilteredAsync` | Logs filtrados, ordenados por `timestampEmision` descendente y paginados (máximo 200 por página). |
| `GET /api/logs/{id}` | `ConsultarLogsUseCase.ObtenerPorIdAsync` | `GetByIdAsync` | Un log por su `id`. |
| `GET /api/logs/resumen` | `ConsultarLogsUseCase.ObtenerResumenDashboardAsync` | `GetResumenDashboardAsync` | Métricas agregadas para el dashboard. |
| `GET /api/pantallas/vuelos` | `ConsultarLogsUseCase.ObtenerTableroVuelosAsync` | `GetVuelosPantallaAsync` | Hasta 50 vuelos, del más reciente al más antiguo según su última actualización. |
| `GET /api/pantallas/eventos` | `ConsultarLogsUseCase.ObtenerEventosPantallaPublicaAsync` | `GetRecentPublicEventsAsync(30)` | Últimos 30 eventos públicos. |

### 3.5 Flujo D: tiempo real (SSE)

1. Un cliente abre `GET /api/pantallas/stream` (canal público) o `GET /api/logs/stream-interno` (canal interno). Ambos endpoints delegan en la función `ManejarStreamSseAsync` de `Program.cs`.
2. La respuesta se configura con `Content-Type: text/event-stream`, `Cache-Control: no-cache` y `X-Accel-Buffering: no` (evita que Nginx o el API Gateway retengan los mensajes).
3. `SsePantallaNotifier.SuscribirCliente` crea una suscripción con una cola propia (`Channel`) de hasta 100 mensajes e indica si el cliente es público o interno.
4. El servidor envía el comentario `: conectado` y entra en un ciclo que:
   - escribe los mensajes pendientes de la cola como `data: <json>`;
   - si pasan 15 segundos sin mensajes, envía el comentario `: heartbeat` para mantener viva la conexión.
5. Cada vez que se guarda un log (flujos A o B), `NotificarEventoAsync` arma dos versiones del mensaje y las deposita en la cola de cada suscriptor:
   - **Clientes públicos:** reciben el mensaje solo si `esPublico = true`. El contenido no incluye `payloadJson`, `eventId` ni fechas internas.
   - **Clientes internos:** reciben todos los mensajes, con el `LogEntry` completo.
6. Si un cliente no consume a tiempo y su cola se llena, se descartan sus mensajes más antiguos; los demás clientes no se ven afectados.
7. Al cerrarse la conexión, `DesuscribirCliente` elimina la suscripción.

Como cada cliente tiene su propia cola, la notificación nunca bloquea al consumidor de RabbitMQ ni a las peticiones REST, y dos eventos simultáneos no se mezclan en la misma conexión.

### 3.6 Consumidor de RabbitMQ

`RabbitMqConsumerHostedService` se ejecuta durante toda la vida de la aplicación:

1. Se conecta al broker con `RabbitMq:Host`, `Port`, `Username`, `Password` y `VirtualHost`.
2. Configura un *prefetch* de 10 mensajes: el broker no entrega más de 10 mensajes sin confirmar a la vez.
3. Declara el exchange `aeropuerto_events` (tipo *topic*, durable) y la cola `grupo8_logs_queue` (durable), y las vincula con la clave `#`, por lo que **recibe todos los mensajes sin importar la routing key**.
4. Por cada mensaje: lo deserializa como `BusEventEnvelopeDto`, crea un *scope* de inyección de dependencias, obtiene `ProcesarEventoUseCase` y llama a `EjecutarDesdeBusAsync`.
5. Confirma con control manual:
   - Mensaje procesado correctamente (o descartado por idempotencia): `Ack`.
   - Mensaje con JSON inválido o que falla la validación: `Nack` **sin reencolar**, es decir, se descarta. El error y el contenido del mensaje quedan en el log de la aplicación.
6. Si el broker no está disponible, registra una advertencia y reintenta cada **10 segundos**. La API REST continúa operativa durante ese lapso.

### 3.7 Tolerancia a fallos

| Situación | Comportamiento |
|---|---|
| No hay `MongoDb:ConnectionString` o MongoDB no responde al iniciar | Se utiliza memoria local acotada (sección 3.8). Los datos se pierden al reiniciar. |
| Falla una inserción en MongoDB | Se registra el error y el log se guarda en memoria local. |
| Inserción con un `eventId` que ya existe (dos envíos simultáneos del mismo evento) | El índice único lo rechaza; el servicio lo registra como duplicado y no lo guarda en ningún lado. |
| Falla una consulta en MongoDB | Se registra la advertencia y se responde con los datos de memoria local. |
| RabbitMQ no disponible | Reintento cada 10 segundos; la API REST sigue funcionando. |
| Un cliente SSE es lento o se desconecta | Solo se afecta su propia cola; el resto de los clientes sigue recibiendo. |
| Conexión SSE inactiva a través de un proxy | El *heartbeat* de 15 segundos evita que el proxy la cierre por inactividad. |

### 3.8 Modo de memoria local

Cuando MongoDB no está disponible, el repositorio usa dos estructuras en memoria:

- **Logs:** una cola concurrente que conserva como máximo **1000** registros; al superar ese número se eliminan los más antiguos.
- **Tablero de vuelos:** un diccionario concurrente por `vueloId`.

Al igual que con MongoDB, en este modo las búsquedas por `origen`, `tipo` y `severidad` no distinguen mayúsculas y minúsculas.

---

## 4. Requisitos

| Requisito | Detalle | Obligatorio |
|---|---|---|
| .NET SDK | 8.0 o superior | Sí, para ejecutar sin Docker |
| Docker | Cualquier versión reciente | Sí, para ejecutar en contenedor |
| MongoDB | 6.x o 7.x (por defecto `localhost:27017`) | Recomendado. Sin MongoDB se usa memoria local. |
| RabbitMQ | 3.x (puertos 5672 y 15672 con el plugin de administración) | Necesario para la integración por bus. |
| Postman o Insomnia | Para probar los endpoints | Opcional |
| Navegador moderno | Para las Pantallas Públicas (soporte de `EventSource`) | Opcional |

La solución utiliza el formato `Logs.slnx`, que requiere Visual Studio 2022 17.13 o superior, o un SDK de .NET 9 o superior. Si el IDE no lo soporta, el proyecto puede ejecutarse directamente desde la carpeta `Logs/` con `dotnet run`.

Sistemas operativos soportados: Windows, Linux y macOS (cualquier sistema con .NET 8 o Docker).

### Servicios auxiliares con Docker

```bash
docker run -d --name mongo  -p 27017:27017 mongo:7
docker run -d --name rabbit -p 5672:5672 -p 15672:15672 rabbitmq:3-management
```

La consola de administración de RabbitMQ queda disponible en `http://localhost:15672` (usuario `guest`, contraseña `guest`).

En el entorno integrado, RabbitMQ y el API Gateway los despliega el Grupo 6; en ese caso solo se debe levantar MongoDB y configurar los datos del broker compartido (sección 5).

---

## 5. Configuración

Toda la configuración está en `Logs/appsettings.json`:

```json
{
  "MongoDb": {
    "ConnectionString": "mongodb://localhost:27017",
    "DatabaseName": "AeropuertoLogsDb"
  },
  "RabbitMq": {
    "Host": "localhost",
    "Port": 5672,
    "Username": "guest",
    "Password": "guest",
    "VirtualHost": "/",
    "Exchange": "aeropuerto_events",
    "Queue": "grupo8_logs_queue"
  },
  "BusinessConfig": {
    "DiasRetencionLogs": 30,
    "EventosPublicos": [
      "EstadoVueloCambiado",
      "VueloCreado",
      "PistaBloqueada",
      "PistaLiberada",
      "AutorizacionOtorgada",
      "AutorizacionDenegada",
      "ReporteClimatico"
    ]
  },
  "Jwt": {
    "Key": "ClaveSecretaSuperSeguraAeropuertoUAI2026!",
    "Audience": "logs-api",
    "RequireJwtInLogs": false
  }
}
```

| Clave | Valor por defecto | Descripción |
|---|---|---|
| `MongoDb:ConnectionString` | `mongodb://localhost:27017` | Cadena de conexión a MongoDB. Si se deja vacía, se usa memoria local. |
| `MongoDb:DatabaseName` | `AeropuertoLogsDb` | Base de datos. Colecciones: `LogsCentralizados` y `TableroVuelos`. |
| `RabbitMq:Host` | `localhost` | Host del broker. |
| `RabbitMq:Port` | `5672` | Puerto AMQP. |
| `RabbitMq:Username` | `guest` | Usuario del broker. |
| `RabbitMq:Password` | `guest` | Contraseña del broker. |
| `RabbitMq:VirtualHost` | `/` | Virtual host. |
| `RabbitMq:Exchange` | `aeropuerto_events` | Exchange tipo *topic*, durable. |
| `RabbitMq:Queue` | `grupo8_logs_queue` | Cola durable propia del Grupo 8. |
| `BusinessConfig:DiasRetencionLogs` | `30` | Días que se conserva cada log antes de su eliminación automática. Si se configura `0` o un valor negativo, se usan 30 días. |
| `BusinessConfig:EventosPublicos` | Lista anterior | Tipos de evento que se muestran en las Pantallas Públicas (sección 8.2). |
| `Jwt:Key` | Valor de desarrollo | Clave simétrica (HS256) con la que el Grupo 7 firma los tokens. Debe reemplazarse por la clave real acordada. |
| `Jwt:Audience` | `logs-api` | `audience` que deben tener los tokens. |
| `Jwt:RequireJwtInLogs` | `false` | Si es `true`, `GET /api/logs` y `GET /api/logs/resumen` exigen un token válido (sección 14). |

Todas las claves pueden sobrescribirse con variables de entorno, usando doble guion bajo como separador de niveles: `MongoDb__ConnectionString`, `RabbitMq__Host`, `RabbitMq__Password`, `Jwt__Key`, `Jwt__RequireJwtInLogs`. Para una lista, cada elemento lleva su índice: `BusinessConfig__EventosPublicos__0`, `BusinessConfig__EventosPublicos__1`, etc.

---

## 6. Instalación y ejecución

### 6.1 Ejecución local con .NET

```bash
git clone https://github.com/TobiasJasson/Logs.git
cd Logs/Logs
dotnet restore
dotnet run --launch-profile http
```

| Recurso | URL |
|---|---|
| API (URL base de desarrollo) | `http://localhost:5097` |
| API con HTTPS (perfil `https`) | `https://localhost:7078` |
| Swagger UI | `http://localhost:5097/swagger` |
| Pantallas Públicas y simulador | `http://localhost:5097/` |
| Estado del servicio | `http://localhost:5097/health` |

### 6.2 Ejecución con Docker

El `Dockerfile` está en `Logs/Dockerfile`, pero debe construirse **desde la raíz del repositorio**, porque copia los cuatro proyectos:

```bash
cd Logs
docker build -f Logs/Dockerfile -t grupo8-logs .
```

Dentro del contenedor el servicio escucha en el puerto **8080**. Ejemplo de ejecución publicando ese puerto como 5097 en el equipo:

```bash
docker run -d --name grupo8-logs -p 5097:8080 \
  -e MongoDb__ConnectionString=mongodb://host.docker.internal:27017 \
  -e RabbitMq__Host=host.docker.internal \
  grupo8-logs
```

Dentro de un contenedor, `localhost` es el propio contenedor y no el equipo. Por eso MongoDB y RabbitMQ se indican como `host.docker.internal` (Docker Desktop) o, si todos los contenedores están en la misma red de Docker, por el nombre de su contenedor:

```bash
docker network create aeropuerto
docker run -d --name mongo  --network aeropuerto mongo:7
docker run -d --name rabbit --network aeropuerto -p 5672:5672 -p 15672:15672 rabbitmq:3-management
docker run -d --name grupo8-logs --network aeropuerto -p 5097:8080 \
  -e MongoDb__ConnectionString=mongodb://mongo:27017 \
  -e RabbitMq__Host=rabbit \
  grupo8-logs
```

Verificación: `http://localhost:5097/health` debe responder `{"status":"Healthy", ...}`.

### 6.3 Exponer el servicio a otras máquinas

Por defecto el perfil `http` escucha únicamente en `localhost`. Para que otros grupos puedan invocar la API desde sus equipos, se debe indicar que escuche en todas las interfaces:

```bash
dotnet run --project Logs --urls "http://0.0.0.0:5097"
```

La URL base que utilicen los demás grupos será `http://<IP-o-nombre-del-equipo-del-Grupo-8>:5097`, o la ruta que el Grupo 6 defina en el API Gateway. Debe verificarse que el firewall permita el puerto elegido. Con Docker, el contenedor ya escucha en todas las interfaces.

---

## 7. Modelo de datos

### 7.1 `LogEntry` (colección `LogsCentralizados`)

Cada evento se persiste con la estructura `LogEntry`. En las respuestas JSON los nombres de campo están en camelCase.

| Campo | Tipo | Descripción |
|---|---|---|
| `id` | GUID | Identificador interno del registro (clave `_id` en MongoDB). |
| `eventId` | GUID | Identificador del evento original. Lo provee el emisor en el envelope; si no lo envía, o en un log directo, lo genera el servicio. Es único. |
| `origen` | string | Grupo o componente que emitió el evento (por ejemplo `TorreControl`). |
| `tipoEvento` | string | Tipo de evento (por ejemplo `AutorizacionOtorgada`). |
| `severidad` | string | `Informacion`, `Aprobado`, `Alerta` o `Error`. |
| `mensaje` | string | Texto descriptivo mostrado en la bitácora y en el PopUp. |
| `ubicacionId` | string | Identificador de vuelo, pista o método donde ocurrió el evento. |
| `timestampEmision` | datetime (UTC) | Momento de emisión informado por el origen, truncado a milisegundos. Se usa para idempotencia y ordenamiento. |
| `fechaRegistroUtc` | datetime (UTC) | Momento en que el servicio guardó el registro. |
| `expiraEnUtc` | datetime (UTC) | Fecha a partir de la cual el registro se elimina automáticamente. |
| `esPublico` | boolean | Indica si el evento se muestra en las Pantallas Públicas. |
| `payloadJson` | string | Contenido original del evento serializado como texto JSON. |

### 7.2 `VueloPantalla` (colección `TableroVuelos`)

Cada vuelo ocupa una única fila del tablero. Cuando llega un evento del mismo vuelo, la fila se actualiza con los datos recibidos y conserva los que el evento no trae (sección 8.6).

| Campo | Tipo | Descripción |
|---|---|---|
| `vueloId` | string | Identificador del vuelo (clave `_id` en MongoDB), siempre en mayúsculas, por ejemplo `AR1234`. Un evento con `ar1234` actualiza la misma fila. |
| `tipoOperacion` | string | `Salida` o `Llegada`. |
| `aerolineaOOrigen` | string | Aerolínea, aeropuerto de origen o grupo emisor, según la vía de ingreso. |
| `destino` | string | Destino del vuelo. |
| `horario` | string | Horario programado, en el formato que envíe el emisor (por ejemplo `18:45`). |
| `puerta` | string | Puerta de embarque. |
| `pista` | string | Pista asignada. |
| `estado` | string | Estado mostrado en pantalla (por ejemplo `Programado`, `Embarcando`, `Despegando`, `En Vuelo`, `Arribado`, `Demorado`, `Cancelado`). |
| `severidadVisual` | string | Severidad usada para colorear el estado en la pantalla. |
| `ultimaActualizacionUtc` | datetime (UTC) | Momento de la última actualización de la fila. |

### 7.3 `DashboardResumenDto`

| Campo | Tipo | Descripción |
|---|---|---|
| `totalLogsRegistrados` | número | Cantidad total de logs guardados (no vencidos). |
| `autorizacionesOtorgadas` | número | Logs cuyo tipo contiene `Otorgada` (sin distinguir mayúsculas). |
| `autorizacionesDenegadas` | número | Logs cuyo tipo contiene `Denegada` (sin distinguir mayúsculas). |
| `alertasActivas` | número | Logs con severidad `Alerta` (sin distinguir mayúsculas) dentro del período de retención. |
| `erroresRegistrados` | número | Logs con severidad `Error` dentro del período de retención. |
| `vuelosEnCurso` | número | Vuelos del tablero (entre los 50 actualizados más recientemente) cuyo estado no es `Arribado` ni `Cancelado`. |
| `logsPorComponente` | objeto | Cantidad de logs por `origen`, por ejemplo `{ "TorreControl": 12 }`. Los orígenes que solo difieren en mayúsculas o espacios (`TorreControl`, `torrecontrol `) se suman en una misma entrada. |

Con MongoDB, los conteos se calculan sobre toda la colección; en modo de memoria local, sobre los registros en memoria (hasta 1000).

### 7.4 Severidades

Las severidades están definidas en la enumeración `NivelSeveridad` (`Domain/Enums/Serveridad.cs`).

| Severidad | Color en pantalla | Significado | Ejemplos |
|---|---|---|---|
| `Informacion` | Azul | Eventos generales o rutinarios | Reporte climático normal, cambio de estado de vuelo |
| `Aprobado` | Verde | Operación autorizada o completada | `AutorizacionOtorgada`, `PistaLiberada`, `VueloCreado` |
| `Alerta` | Amarillo | Situación que requiere atención | Demora, pista ocupada, viento fuerte |
| `Error` | Rojo | Rechazo o falla | `AutorizacionDenegada`, `PistaBloqueada`, vuelo cancelado, timeout entre servicios |

---

## 8. Reglas de negocio

### 8.1 Retención

Cada log se guarda con `expiraEnUtc = fecha de registro + DiasRetencionLogs` (30 días por defecto). MongoDB elimina los registros vencidos mediante el índice TTL; el proceso interno de MongoDB se ejecuta aproximadamente cada 60 segundos, por lo que la eliminación no es instantánea. Las filas del tablero de vuelos no tienen vencimiento.

### 8.2 Eventos públicos

Un evento es público (`esPublico = true`) y llega a las Pantallas Públicas cuando su tipo pertenece a la lista `BusinessConfig:EventosPublicos`. La comparación **no distingue mayúsculas y minúsculas**. Lista por defecto:

`EstadoVueloCambiado`, `VueloCreado`, `PistaBloqueada`, `PistaLiberada`, `AutorizacionOtorgada`, `AutorizacionDenegada`, `ReporteClimatico`.

En el log directo, además, el emisor puede forzar la publicación con `mostrarEnPantallaPublica = true`. En el envelope no existe ese indicador: solo los tipos de la lista son públicos.

### 8.3 Idempotencia (solo envelope)

Antes de guardar un envelope, el servicio realiza dos controles:

1. **Por `eventId`:** si ya existe un registro con el mismo `eventId`, el evento se descarta. Esto cubre el caso de un mismo mensaje entregado dos veces.
2. **Por `timestamp`:** busca el último `timestampEmision` registrado para la combinación **`origen` + `tipo` + `ubicación`** (la ubicación se obtiene del `payload`, sección 8.5). Si el `timestamp` recibido es **menor o igual** al último guardado, el evento se descarta por considerarse duplicado o llegado fuera de orden.

Las comparaciones de tiempo se hacen con precisión de milisegundos, y la comparación de `origen`, `tipo` y ubicación no distingue mayúsculas y minúsculas (`TorreControl` y `torrecontrol` se consideran el mismo origen).

La respuesta REST de un evento descartado indica `procesado: false`; en RabbitMQ el mensaje se confirma (`Ack`) sin guardarse.

Se incluye la ubicación en la clave para que dos eventos del mismo tipo sobre recursos distintos (por ejemplo, dos vuelos que cambian de estado en el mismo milisegundo) no se descarten entre sí. Es una extensión de la regla del enunciado, que compara por tipo y origen.

Consecuencias para los emisores:

- Cada evento debe llevar un `eventId` nuevo (GUID) y un `timestamp` en UTC.
- Para un mismo origen, tipo y recurso, el `timestamp` debe ser estrictamente creciente. Se recomienda usar `DateTime.UtcNow` con precisión de milisegundos o mayor.
- El envelope sin `timestamp` se rechaza (`400` por REST, `Nack` por RabbitMQ).

### 8.4 Severidad automática (solo envelope)

La severidad se deduce del texto del `tipo` junto con el estado informado en el `payload` (el campo `estadoNuevo` o, si no viene, el campo `estado`), sin distinguir mayúsculas. De esta forma, un mismo tipo `EstadoVueloCambiado` se muestra con distinto color según el estado al que pasa el vuelo. Se evalúan las reglas en este orden:

| Prioridad | El tipo o el estado contiene | Severidad |
|---|---|---|
| 1 | `error`, `denegada`, `bloqueada`, `timeout`, `cancelado` | `Error` |
| 2 | `alerta`, `ocupada`, `demora`, `viento` | `Alerta` |
| 3 | `otorgada`, `liberada`, `creado`, `arribado`, `aprobado`, `despegando` | `Aprobado` |
| 4 | Ninguna de las anteriores | `Informacion` |

La palabra `demora` también se encuentra dentro de `Demorado`, por lo que un vuelo demorado se muestra como `Alerta`.

Ejemplos:

| Tipo | Severidad resultante | Público |
|---|---|---|
| `VueloCreado` | Aprobado | Sí |
| `EstadoVueloCambiado` (sin `estadoNuevo`, o con `Embarcando`) | Informacion | Sí |
| `EstadoVueloCambiado` con `estadoNuevo: "Demorado"` | Alerta | Sí |
| `EstadoVueloCambiado` con `estadoNuevo: "Despegando"` o `"Arribado"` | Aprobado | Sí |
| `EstadoVueloCambiado` con `estadoNuevo: "Cancelado"` | Error | Sí |
| `PistaBloqueada` | Error | Sí |
| `PistaLiberada` | Aprobado | Sí |
| `AutorizacionOtorgada` | Aprobado | Sí |
| `AutorizacionDenegada` | Error | Sí |
| `ReporteClimatico` | Informacion | Sí |
| `DemoraVuelo` | Alerta | No |
| `PistaOcupada` | Alerta | No |
| `VueloArribado` | Aprobado | No |
| `ErrorTimeoutServicio` | Error | No |

En el log directo la severidad la indica el emisor (sección 9.3).

### 8.5 Ubicación y mensaje (solo envelope)

- **Ubicación:** se toma del `payload` según esta prioridad: `vueloId` (valor tal cual), `pista` (se guarda como `Pista <valor>`), `ubicacionId` (valor tal cual). Si no existe ninguna, se guarda `Sistema`.
- **Mensaje:** se elige según esta prioridad:
  1. El campo `mensaje` del `payload`, si viene.
  2. Si vienen `vueloId` y `estadoNuevo`: `Vuelo <VUELOID> cambió a estado: <estadoNuevo>.` (con el identificador en mayúsculas).
  3. En otro caso: `[<origen>] Evento '<tipo>' registrado sobre <ubicación>.`

Los campos del `payload` se buscan sin distinguir mayúsculas en el nombre (`vueloId` o `VueloId`). Se aceptan valores de texto, numéricos o booleanos: `"vueloId": "AR1234"` y `"vueloId": 1234` se reconocen; los objetos, listas y valores `null` se ignoran.

Normalización: el `vueloId` se guarda en mayúsculas (`ar1234` pasa a `AR1234`) y la pista también (`Pista 09L`). Los demás valores se guardan tal como llegan, sin espacios al inicio ni al final.

### 8.6 Tablero de vuelos

El tablero muestra una fila por vuelo y se actualiza automáticamente. Cada actualización **combina** los datos recibidos con los que la fila ya tenía: si un evento no trae un campo (por ejemplo `destino` o `puerta`), se conserva el valor anterior. Solo cuando el vuelo es nuevo y el dato nunca se informó se muestra el valor por defecto.

**Desde un envelope**, se decide si el evento modifica el tablero con estos pasos:

1. Solo modifican el tablero los eventos cuyo `origen` es `GestionVuelos` o cuyo `tipo` contiene la palabra `Vuelo` (sin distinguir mayúsculas). Cualquier otro evento, aunque traiga `vueloId` o un campo `estado`, queda registrado en la bitácora pero no cambia el tablero. Por ejemplo, una `AutorizacionOtorgada` de Torre de Control o un `EquipajeDespachado` de Equipaje con `vueloId` no modifican la fila del vuelo.
2. Se identifica el vuelo: es el `vueloId` del `payload` o, si no viene, la ubicación extraída (sección 8.5). Si el resultado es `Sistema`, `General` o una pista (`Pista ...`), el tablero no se modifica.
3. El identificador se pasa a mayúsculas y se busca la fila existente para combinar los datos.

Los datos de la fila se obtienen así:

| Campo de la fila | Se toma de | Si no viene, se conserva el valor anterior; si el vuelo es nuevo |
|---|---|---|
| `vueloId` | `vueloId` del `payload` | La ubicación extraída (sección 8.5) |
| `tipoOperacion` | `tipoOperacion` | `Salida` |
| `aerolineaOOrigen` | `origen` del `payload` (en mayúsculas) | `EZE` |
| `destino` | `destino` | `Sin especificar` |
| `horario` | `horario` | `--:--` |
| `puerta` | `puerta` (en mayúsculas) | `-` |
| `pista` | `pista` (en mayúsculas) | `-` |
| `estado` | `estadoNuevo`, o en su defecto `estado` | `Programado` si el tipo es `VueloCreado`; en otro caso, el `tipo` del evento |
| `severidadVisual` | Severidad del log (sección 8.4), solo si el evento trae un estado o el vuelo es nuevo | Se conserva la anterior |

**Desde un log directo**, se actualiza la fila si el cuerpo trae `estadoVuelo`, `destino`, `puerta` u `horario`, o si el `tipoEvento` contiene la palabra `Vuelo`, siempre que `ubicacionId` no sea `General`, `Sistema` ni una pista (`Pista ...`). El `vueloId` es el `ubicacionId` en mayúsculas. Los datos se toman del cuerpo con la misma lógica de combinación:

| Campo de la fila | Se toma de | Si no viene, se conserva el valor anterior; si el vuelo es nuevo |
|---|---|---|
| `tipoOperacion` | `tipoOperacion` | `Salida` |
| `aerolineaOOrigen` | Se conserva el valor anterior | El `origen` del log |
| `destino` | `destino` | `Sin especificar` |
| `horario` | `horario` | `--:--` |
| `puerta` | `puerta` | `-` |
| `pista` | Se conserva el valor anterior | `-` |
| `estado` | `estadoVuelo` | `En Horario` |
| `severidadVisual` | Severidad del log | - |

La fila actualizada se envía por SSE junto con el log en el campo `vuelo` (sección 10.9).

---

## 9. Guía de integración para los demás grupos

Esta sección describe cómo debe invocar cada grupo al servicio.

### 9.1 Elección de la vía de integración

| Criterio | Usar RabbitMQ (envelope) | Usar REST (`POST /api/logs`) |
|---|---|---|
| Acoplamiento | Desacoplado: el emisor no depende de que este servicio esté activo. | El emisor depende de la disponibilidad del servicio. |
| Severidad | La deduce el servicio a partir del tipo. | La define el emisor explícitamente. |
| Idempotencia | Sí (por `eventId` y por origen + tipo + ubicación + timestamp). | No. |
| Control de publicación en pantallas | Solo tipos públicos configurados. | Campo `mostrarEnPantallaPublica`. |
| Caso típico | Eventos de negocio del dominio (vuelos, pistas, autorizaciones, clima, equipaje). | Errores técnicos entre servicios (timeouts, reintentos agotados), diagnósticos y avisos puntuales. |

Los dos mecanismos pueden combinarse en un mismo grupo. En particular, el enunciado exige que, cuando una llamada REST a otro grupo falle después de los reintentos, el servicio origen registre el error en Logs: para eso se recomienda `POST /api/logs` con `severidad: "Error"`.

### 9.2 Requisitos comunes de toda petición HTTP

| Aspecto | Requisito |
|---|---|
| URL base | `http://<host-del-grupo-8>:5097`, o la ruta definida en el API Gateway (en desarrollo local, `http://localhost:5097`). |
| Encabezado `Content-Type` | `application/json` en todas las peticiones con cuerpo. De lo contrario, el servicio responde `415 Unsupported Media Type`. |
| Codificación | UTF-8. |
| Nombres de propiedad | Se aceptan sin distinguir mayúsculas y minúsculas (`origen`, `Origen`). Se recomienda camelCase. |
| Fechas | ISO 8601 en **UTC** con sufijo `Z`, por ejemplo `2026-10-18T15:30:00Z`. Es obligatorio para `timestamp` y recomendado en los filtros `fechaDesde` y `fechaHasta`. |
| Identificadores | `eventId` debe ser un GUID válido (formato `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`). |
| Autenticación | Solo `GET /api/logs`, `GET /api/logs/{id}` y `GET /api/logs/resumen` pueden exigir token, cuando `Jwt:RequireJwtInLogs = true`. En ese caso se envía `Authorization: Bearer <token>` con un JWT emitido por el Grupo 7 y `audience = logs-api`. Se recomienda que los clientes ya lo envíen. |
| CORS | El servicio permite cualquier origen, método y encabezado; las aplicaciones web pueden invocarlo y suscribirse a los streams desde otro dominio. |
| Paginación | `GET /api/logs` devuelve 50 registros por página por defecto y como máximo 200 (parámetros `page` y `pageSize`). |

### 9.3 Vía A: REST directo

**Solicitud**

```
POST {baseUrl}/api/logs
Content-Type: application/json
```

| Campo | Tipo | Obligatorio | Valor por defecto | Observaciones |
|---|---|---|---|---|
| `origen` | string | Sí | - | Nombre del grupo o componente emisor. Debe ser constante entre llamadas (por ejemplo `GestionVuelos`). |
| `mensaje` | string | Sí | - | Texto legible que se mostrará en la bitácora y el PopUp. |
| `severidad` | string | No | `Informacion` | Valores aceptados (sin distinguir mayúsculas): `Informacion`, `Aprobado` (o `ok`, `success`), `Alerta` (o `warning`), `Error`. Cualquier otro valor se guarda como `Informacion`. |
| `tipoEvento` | string | No | `LogManual` | Clasificación del evento. Si coincide con un tipo público, el log se muestra en pantallas. Si contiene `Vuelo`, actualiza el tablero. |
| `ubicacionId` | string | No | `General` | Vuelo, pista o nombre del método donde ocurrió. Si se actualiza el tablero, es el `vueloId` (se guarda en mayúsculas); con `General`, `Sistema` o `Pista ...` el tablero no se modifica. |
| `mostrarEnPantallaPublica` | boolean | No | `false` | Fuerza la aparición en las Pantallas Públicas. |
| `estadoVuelo` | string | No | - | Estado del vuelo a mostrar en el tablero. |
| `destino` | string | No | - | Destino a mostrar en el tablero. |
| `horario` | string | No | - | Horario a mostrar en el tablero. |
| `puerta` | string | No | - | Puerta a mostrar en el tablero. |

Basta con informar uno de los cuatro campos anteriores para que se actualice la fila del vuelo; los demás conservan su valor anterior (sección 8.6).
| `tipoOperacion` | string | No | - | `Salida` o `Llegada`. |

**Ejemplo: error técnico (solo bitácora interna)**

```json
{
  "origen": "Equipaje",
  "ubicacionId": "VincularPasajeroAsync",
  "severidad": "Error",
  "tipoEvento": "TimeoutError",
  "mensaje": "Timeout de 5s al consultar Check-in luego de 3 reintentos",
  "mostrarEnPantallaPublica": false
}
```

**Ejemplo: aviso de vuelo con actualización del tablero**

```json
{
  "origen": "GestionVuelos",
  "ubicacionId": "AR5500",
  "severidad": "Alerta",
  "tipoEvento": "DemoraVuelo",
  "mensaje": "Demora de 20 minutos por carga de equipaje",
  "mostrarEnPantallaPublica": true,
  "destino": "Córdoba (COR)",
  "horario": "19:30",
  "puerta": "A4",
  "estadoVuelo": "Demorado",
  "tipoOperacion": "Salida"
}
```

**Respuestas**

| Código | Significado |
|---|---|
| `201 Created` | Log guardado. El cuerpo contiene el `LogEntry` creado y el encabezado `Location` apunta a `/api/logs/{id}`. |
| `400 Bad Request` | Falta `origen` o `mensaje`, o el cuerpo no es un JSON válido. |
| `415 Unsupported Media Type` | No se envió `Content-Type: application/json`. |

### 9.4 Vía B: RabbitMQ

**Parámetros de conexión**

| Elemento | Valor |
|---|---|
| Host, puerto, usuario y contraseña | Los del broker compartido que despliega el Grupo 6 (`localhost:5672`, `guest`/`guest` en desarrollo). |
| Exchange | `aeropuerto_events` |
| Tipo de exchange | `topic` |
| Durabilidad | Durable (`durable = true`) |
| Routing key | Libre. Se recomienda `<Origen>.<Tipo>`, por ejemplo `TorreControl.PistaBloqueada`. El Grupo 8 recibe todas (`#`). |
| Cuerpo | JSON en UTF-8 con el envelope estándar. |
| Propiedades recomendadas | `content_type = application/json`, mensaje persistente (`delivery_mode = 2`). |

Si el emisor declara el exchange, debe hacerlo con los mismos parámetros (`topic`, durable). Una declaración con parámetros distintos provoca el error `PRECONDITION_FAILED` y cierra el canal.

**Envelope estándar**

| Campo | Tipo | Obligatorio | Descripción |
|---|---|---|---|
| `eventId` | GUID | Sí (recomendado) | Identificador único del evento. Si se omite o es vacío, el servicio genera uno y lo devuelve en la respuesta REST, pero no puede detectar reenvíos del mismo mensaje. |
| `tipo` | string | Sí | Tipo de evento. Define severidad y publicidad (secciones 8.2 y 8.4). |
| `origen` | string | Sí | Grupo emisor. |
| `timestamp` | datetime UTC | Sí | Momento de emisión (ver idempotencia en la sección 8.3). Si falta, el mensaje se rechaza. |
| `payload` | objeto JSON | No | Datos del evento. Campos reconocidos (con valor de texto, número o booleano): `mensaje`, `vueloId`, `pista`, `ubicacionId` y, para el tablero, `destino`, `origen`, `horario`, `puerta`, `tipoOperacion`, `estadoNuevo`, `estado`. Cualquier otro campo queda guardado en `payloadJson`. |

**Ejemplo: cambio de estado de un vuelo (Grupo 2)**

```json
{
  "eventId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "tipo": "EstadoVueloCambiado",
  "origen": "GestionVuelos",
  "timestamp": "2026-10-18T15:40:00.000Z",
  "payload": {
    "vueloId": "AR1234",
    "origen": "EZE",
    "destino": "Madrid (MAD)",
    "horario": "18:45",
    "puerta": "B4",
    "tipoOperacion": "Salida",
    "estadoAnterior": "Programado",
    "estadoNuevo": "Embarcando",
    "mensaje": "Vuelo AR1234 embarcando en puerta B4"
  }
}
```

**Qué ocurre con el mensaje**

| Resultado | Acción del servicio |
|---|---|
| Procesado correctamente | `Ack`. Se guarda, se actualiza el tablero si corresponde y se notifica por SSE. |
| Descartado por idempotencia | `Ack`. No se guarda. |
| JSON inválido, o `origen`, `tipo` o `timestamp` vacíos | `Nack` sin reencolar. El mensaje se descarta y se registra el error en el log del servicio. |

Como no existe cola de mensajes fallidos, los emisores deben validar el envelope antes de publicarlo.

### 9.5 Convenciones recomendadas para los emisores

| Elemento | Convención |
|---|---|
| `origen` | Nombre estable del grupo en PascalCase, sin espacios: `TorreControl`, `GestionVuelos`, `PistasAeronaves`, `CheckIn`, `Equipaje`, `Meteorologia`, `Seguridad`, `PanelAdministracion`. |
| `tipo` / `tipoEvento` | PascalCase descriptivo, en pasado o estado: `VueloCreado`, `PistaBloqueada`. Incluir palabras clave de la tabla 8.4 cuando se quiera una severidad determinada. |
| `ubicacionId` / `payload.vueloId` / `payload.pista` | Usar siempre el mismo formato para el mismo recurso (`AR1234`, `09L`) para facilitar los filtros y para que el tablero agrupe correctamente. |
| `payload.vueloId` | Puede enviarse como texto o número y en cualquier combinación de mayúsculas; el servicio lo normaliza a mayúsculas. Solo los eventos de `GestionVuelos`, o cuyo tipo contiene `Vuelo`, modifican el tablero (sección 8.6); el resto de los grupos puede incluir `vueloId` para que el log quede asociado al vuelo sin alterar la pantalla. |
| `payload.estadoNuevo` | Usarlo en los cambios de estado de vuelo: define el estado mostrado en el tablero y el color (sección 8.4). |
| `mensaje` | Frase completa, legible por un operador, sin datos sensibles. |
| Fechas | Siempre UTC. |

### 9.6 Buenas prácticas para el cliente

1. **No bloquear la operación principal por el log.** Encapsular el envío en `try/catch`; un fallo del servicio de logs no debe interrumpir el proceso de negocio.
2. **Definir un tiempo de espera** (por ejemplo, 3 a 5 segundos) en las llamadas HTTP.
3. **Reintentar solo ante errores de red o `5xx`**, con un número limitado de intentos (hasta 3) y espera creciente. **No reintentar ante `4xx`**: indican un problema en la solicitud.
4. **Considerar que `POST /api/logs` no es idempotente.** Un reintento tras un tiempo de espera agotado puede generar un registro duplicado.
5. **Reutilizar la conexión HTTP** (en .NET, una única instancia de `HttpClient` o `IHttpClientFactory`) y la conexión AMQP en lugar de abrirlas por cada evento.
6. **Generar un `eventId` nuevo por cada evento** y un `timestamp` creciente (sección 8.3).

### 9.7 Ejemplos de código

#### C# (.NET) - cliente REST reutilizable

```csharp
using System.Net.Http.Json;

public sealed class LogsClient
{
    private readonly HttpClient _http;

    public LogsClient(HttpClient http) => _http = http; // BaseAddress = http://<host-grupo-8>:5097

    public async Task RegistrarAsync(
        string origen, string mensaje, string severidad = "Informacion",
        string tipoEvento = "LogManual", string ubicacionId = "General",
        bool mostrarEnPantallaPublica = false, CancellationToken ct = default)
    {
        try
        {
            var body = new { origen, mensaje, severidad, tipoEvento, ubicacionId, mostrarEnPantallaPublica };
            using var response = await _http.PostAsJsonAsync("/api/logs", body, ct);
            // 201 = correcto; 400/415 = error de solicitud (no reintentar)
        }
        catch (Exception)
        {
            // El fallo del servicio de logs no debe interrumpir el proceso de negocio.
        }
    }
}

// Registro en Program.cs del grupo emisor:
// builder.Services.AddHttpClient<LogsClient>(c =>
// {
//     c.BaseAddress = new Uri("http://localhost:5097");
//     c.Timeout = TimeSpan.FromSeconds(5);
// });
```

#### C# (.NET) - publicación en RabbitMQ

```csharp
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

var factory = new ConnectionFactory { HostName = "localhost", UserName = "guest", Password = "guest" };
await using var connection = await factory.CreateConnectionAsync();
await using var channel = await connection.CreateChannelAsync();

await channel.ExchangeDeclareAsync("aeropuerto_events", ExchangeType.Topic, durable: true);

var envelope = new
{
    eventId = Guid.NewGuid(),
    tipo = "PistaBloqueada",
    origen = "TorreControl",
    timestamp = DateTime.UtcNow,
    payload = new { pista = "09L", mensaje = "Pista bloqueada por viento cruzado" }
};

var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));
var props = new BasicProperties { ContentType = "application/json", Persistent = true };

await channel.BasicPublishAsync(
    exchange: "aeropuerto_events",
    routingKey: "TorreControl.PistaBloqueada",
    mandatory: false,
    basicProperties: props,
    body: body);
```

#### JavaScript / TypeScript (Node o navegador)

```js
const BASE_URL = "http://localhost:5097";

async function registrarLog({ origen, mensaje, severidad = "Informacion",
                              tipoEvento = "LogManual", ubicacionId = "General",
                              mostrarEnPantallaPublica = false }) {
  try {
    const res = await fetch(`${BASE_URL}/api/logs`, {
      method: "POST",
      headers: { "Content-Type": "application/json" /*, Authorization: `Bearer ${token}` */ },
      body: JSON.stringify({ origen, mensaje, severidad, tipoEvento, ubicacionId, mostrarEnPantallaPublica }),
      signal: AbortSignal.timeout(5000),
    });
    return res.ok ? await res.json() : null;
  } catch {
    return null; // No interrumpir el flujo de negocio
  }
}
```

#### Python

```python
import requests

BASE_URL = "http://localhost:5097"

def registrar_log(origen, mensaje, severidad="Informacion", tipo_evento="LogManual",
                  ubicacion_id="General", publico=False):
    try:
        r = requests.post(
            f"{BASE_URL}/api/logs",
            json={
                "origen": origen,
                "mensaje": mensaje,
                "severidad": severidad,
                "tipoEvento": tipo_evento,
                "ubicacionId": ubicacion_id,
                "mostrarEnPantallaPublica": publico,
            },
            timeout=5,
        )
        return r.json() if r.status_code == 201 else None
    except requests.RequestException:
        return None
```

#### Python - publicación en RabbitMQ (pika)

```python
import json, uuid
from datetime import datetime, timezone
import pika

conn = pika.BlockingConnection(pika.ConnectionParameters("localhost"))
ch = conn.channel()
ch.exchange_declare(exchange="aeropuerto_events", exchange_type="topic", durable=True)

envelope = {
    "eventId": str(uuid.uuid4()),
    "tipo": "VueloCreado",
    "origen": "GestionVuelos",
    "timestamp": datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z"),
    "payload": {"vueloId": "AR1234", "destino": "Madrid (MAD)", "horario": "18:45",
                "puerta": "B4", "estadoNuevo": "Programado", "mensaje": "Vuelo AR1234 creado"},
}

ch.basic_publish(
    exchange="aeropuerto_events",
    routing_key="GestionVuelos.VueloCreado",
    body=json.dumps(envelope),
    properties=pika.BasicProperties(content_type="application/json", delivery_mode=2),
)
conn.close()
```

#### Consumo de los streams en tiempo real (aplicaciones web)

```js
// Canal público: Pantallas Públicas
const publico = new EventSource("http://localhost:5097/api/pantallas/stream");
publico.onmessage = (evento) => {
  const data = JSON.parse(evento.data);   // evento público (sección 10.9)
  if (data.vuelo) actualizarFilaVuelo(data.vuelo);
  agregarAviso(data.severidad, data.mensaje);
};

// Canal interno: bitácora y PopUps de monitoreo
const interno = new EventSource("http://localhost:5097/api/logs/stream-interno");
interno.onmessage = (evento) => {
  const { log, vuelo } = JSON.parse(evento.data);   // LogEntry completo (sección 10.10)
  mostrarPopup(log.severidad, log.mensaje);
};
```

### 9.8 Lista de verificación de integración

Antes de dar por finalizada la integración, cada grupo debe comprobar:

1. La URL base es accesible desde el equipo del grupo (`GET /health` responde `200`).
2. Un `POST /api/logs` de prueba devuelve `201` y el registro aparece en `GET /api/logs?origen=<MiGrupo>`.
3. Si usa RabbitMQ: el exchange existe con los parámetros correctos y un envelope de prueba se guarda (`GET /api/logs?origen=<MiGrupo>`).
4. Los envelopes incluyen `eventId` nuevo y `timestamp` UTC creciente por origen + tipo + recurso.
5. Si el grupo emite eventos de vuelos, el vuelo aparece en `GET /api/pantallas/vuelos` con todos sus datos.
6. El código del emisor no se interrumpe si el servicio de logs no responde.
7. Se envía el encabezado `Authorization` cuando se habilite la validación de tokens.

---

## 10. Referencia de la API

**URL base:** `http://localhost:5097` | **Formato:** JSON | **Documentación interactiva:** `/swagger`

| N.º | Método | Ruta | Consumidor principal | Requiere token | Descripción |
|---|---|---|---|---|---|
| 1 | `GET` | `/health` | API Gateway, Docker | No | Estado del servicio. |
| 2 | `GET` | `/api/logs` | Grupo 9 (Panel de Administración) | Si `RequireJwtInLogs = true` | Consulta de logs con filtros y paginación. |
| 3 | `GET` | `/api/logs/{id}` | Grupo 9, cualquier grupo | Si `RequireJwtInLogs = true` | Consulta de un log por su identificador. |
| 4 | `GET` | `/api/logs/resumen` | Grupo 9 (dashboard) | Si `RequireJwtInLogs = true` | Métricas agregadas. |
| 5 | `POST` | `/api/logs` | Cualquier grupo | No | Registro de un log directo. |
| 6 | `POST` | `/api/logs/envelope` | Cualquier grupo, pruebas | No | Ingreso de un envelope del bus por HTTP. |
| 7 | `GET` | `/api/pantallas/vuelos` | Pantallas Públicas | No | Estado actual del tablero de vuelos. |
| 8 | `GET` | `/api/pantallas/eventos` | Pantallas Públicas | No | Últimos 30 eventos públicos. |
| 9 | `GET` | `/api/pantallas/stream` | Pantallas Públicas | No | Canal SSE público. |
| 10 | `GET` | `/api/logs/stream-interno` | Bitácora interna, monitoreo | No | Canal SSE interno con todos los logs. |

### 10.1 `GET /health` - Estado del servicio

Permite al API Gateway, a Docker o a otro grupo verificar que el servicio está en ejecución. No verifica la conexión con MongoDB ni con RabbitMQ.

```
GET http://localhost:5097/health
```

**Respuesta `200 OK`**

```json
{ "status": "Healthy", "service": "Grupo8-LogsNotificaciones", "timestamp": "2026-10-18T15:30:00.123Z" }
```

### 10.2 `GET /api/logs` - Consultar logs

Devuelve los registros ordenados por `timestampEmision` descendente, paginados. Todos los parámetros son opcionales y se combinan con el operador AND.

| Parámetro | Tipo | Ejemplo | Descripción |
|---|---|---|---|
| `fechaDesde` | datetime | `2026-10-01T00:00:00Z` | Límite inferior inclusivo sobre `timestampEmision`. |
| `fechaHasta` | datetime | `2026-10-31T23:59:59Z` | Límite superior inclusivo. |
| `origen` | string | `TorreControl` | Grupo emisor. |
| `tipo` | string | `PistaBloqueada` | Tipo de evento. |
| `severidad` | string | `Error` | `Informacion`, `Aprobado`, `Alerta` o `Error`. |
| `page` | entero | `1` | Número de página, desde 1. Por defecto `1`. |
| `pageSize` | entero | `50` | Registros por página, entre 1 y 200. Por defecto `50`. |

Los filtros `origen`, `tipo` y `severidad` buscan el valor **exacto, sin distinguir mayúsculas y minúsculas** (`torrecontrol` encuentra `TorreControl`), tanto con MongoDB como en memoria local. Si `fechaDesde` es posterior a `fechaHasta`, el servicio intercambia ambos valores. Un `pageSize` menor que 1 se toma como 50 y uno mayor que 200 se toma como 200.

**Solicitud (Postman / Insomnia)**

```
GET http://localhost:5097/api/logs?origen=TorreControl&severidad=Error&fechaDesde=2026-10-01T00:00:00Z&page=1&pageSize=50
```

**Solicitud (curl)**

```bash
curl "http://localhost:5097/api/logs?origen=TorreControl&severidad=Error"
```

**Respuesta `200 OK`**

```json
[
  {
    "id": "b1c2d3e4-1111-4a5b-9c8d-0123456789ab",
    "eventId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "origen": "TorreControl",
    "tipoEvento": "PistaBloqueada",
    "severidad": "Error",
    "mensaje": "Pista bloqueada por viento cruzado",
    "ubicacionId": "Pista 09L",
    "timestampEmision": "2026-10-18T15:35:00Z",
    "fechaRegistroUtc": "2026-10-18T15:35:01.412Z",
    "expiraEnUtc": "2026-11-17T15:35:01.412Z",
    "esPublico": true,
    "payloadJson": "{\"pista\":\"09L\",\"mensaje\":\"Pista bloqueada por viento cruzado\"}"
  }
]
```

Si ningún registro coincide, o la página pedida no tiene datos, la respuesta es `200 OK` con un arreglo vacío (`[]`). La respuesta no informa el total de registros: para saber si hay más páginas, se debe pedir la siguiente y comprobar si viene vacía. Un valor de fecha o número con formato inválido produce `400 Bad Request`.

### 10.3 `GET /api/logs/{id}` - Consultar un log

Devuelve un único registro por su `id` (el campo `id` del `LogEntry`, no el `eventId`). Es la ruta que se informa en el encabezado `Location` de `POST /api/logs`.

```
GET http://localhost:5097/api/logs/7d9e2b10-aaaa-4c3d-8e1f-abcdef012345
```

| Código | Significado |
|---|---|
| `200 OK` | El cuerpo contiene el `LogEntry`. |
| `404 Not Found` | No existe un log con ese `id` (`{ "error": "Log no encontrado." }`), o el valor no tiene formato de GUID. |

### 10.4 `GET /api/logs/resumen` - Métricas para el dashboard

Devuelve las métricas descritas en la sección 7.3, para el dashboard del Panel de Administración.

```
GET http://localhost:5097/api/logs/resumen
```

**Respuesta `200 OK`**

```json
{
  "totalLogsRegistrados": 57,
  "autorizacionesOtorgadas": 21,
  "autorizacionesDenegadas": 4,
  "alertasActivas": 6,
  "erroresRegistrados": 9,
  "vuelosEnCurso": 5,
  "logsPorComponente": {
    "TorreControl": 25,
    "GestionVuelos": 18,
    "Meteorologia": 14
  }
}
```

Con MongoDB, todas las métricas de logs se calculan sobre la colección completa (los registros dentro del período de retención). `vuelosEnCurso` se calcula sobre los 50 vuelos actualizados más recientemente.

### 10.5 `POST /api/logs` - Registrar un log directo

Guarda el log, actualiza el tablero si corresponde, lo difunde por SSE y devuelve el registro creado. Los campos y validaciones se describen en la sección 9.3.

**Solicitud (Postman / Insomnia)**

```
POST http://localhost:5097/api/logs
Content-Type: application/json
```

```json
{
  "origen": "TorreControl",
  "ubicacionId": "AR1234",
  "severidad": "Aprobado",
  "tipoEvento": "AutorizacionOtorgada",
  "mensaje": "Autorización de despegue otorgada",
  "mostrarEnPantallaPublica": true
}
```

**Solicitud (curl)**

```bash
curl -X POST http://localhost:5097/api/logs \
  -H "Content-Type: application/json" \
  -d '{"origen":"TorreControl","ubicacionId":"AR1234","severidad":"Aprobado","tipoEvento":"AutorizacionOtorgada","mensaje":"Autorización de despegue otorgada","mostrarEnPantallaPublica":true}'
```

**Respuesta `201 Created`** (encabezado `Location: /api/logs/{id}`)

```json
{
  "id": "7d9e2b10-aaaa-4c3d-8e1f-abcdef012345",
  "eventId": "0c5e8a77-bbbb-4d2a-9f10-123456789abc",
  "origen": "TorreControl",
  "tipoEvento": "AutorizacionOtorgada",
  "severidad": "Aprobado",
  "mensaje": "Autorización de despegue otorgada",
  "ubicacionId": "AR1234",
  "timestampEmision": "2026-10-18T15:30:00.123Z",
  "fechaRegistroUtc": "2026-10-18T15:30:00.123Z",
  "expiraEnUtc": "2026-11-17T15:30:00.123Z",
  "esPublico": true,
  "payloadJson": "{\"Origen\":\"TorreControl\",\"UbicacionId\":\"AR1234\",\"Severidad\":\"Aprobado\",\"TipoEvento\":\"AutorizacionOtorgada\",\"Mensaje\":\"Autorización de despegue otorgada\",\"MostrarEnPantallaPublica\":true,\"Destino\":null,\"Horario\":null,\"Puerta\":null,\"EstadoVuelo\":null,\"TipoOperacion\":null}"
}
```

**Respuesta `400 Bad Request`** (falta `origen` o `mensaje`)

```json
{ "error": "El componente de origen y el mensaje son obligatorios." }
```

### 10.6 `POST /api/logs/envelope` - Ingresar un envelope por HTTP

Procesa el mismo envelope que viaja por RabbitMQ, aplicando idempotencia, severidad automática, extracción de ubicación y actualización del tablero (secciones 8.3 a 8.6). Los campos se describen en la sección 9.4.

**Solicitud (Postman / Insomnia)**

```
POST http://localhost:5097/api/logs/envelope
Content-Type: application/json
```

```json
{
  "eventId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "tipo": "PistaBloqueada",
  "origen": "TorreControl",
  "timestamp": "2026-10-18T15:35:00Z",
  "payload": {
    "pista": "09L",
    "mensaje": "Pista bloqueada por viento cruzado"
  }
}
```

**Respuesta `200 OK` - evento procesado**

```json
{ "procesado": true, "eventId": "3fa85f64-5717-4562-b3fc-2c963f66afa6" }
```

Si el envelope no traía `eventId`, la respuesta devuelve el identificador generado por el servicio.

El registro resultante queda con `severidad = "Error"`, `ubicacionId = "Pista 09L"` y `esPublico = true`.

**Respuesta `200 OK` - descartado por idempotencia** (mismo `eventId` reenviado, o `timestamp` menor o igual al último del mismo origen, tipo y ubicación)

```json
{ "procesado": false, "motivo": "Descartado por idempotencia (evento duplicado o antiguo)." }
```

**Respuesta `400 Bad Request`**

```json
{ "error": "El origen y el tipo de evento son obligatorios." }
```

```json
{ "error": "El timestamp de emisión es obligatorio para validar idempotencia." }
```

Un descarte por idempotencia no es un error: la respuesta es `200`. El emisor debe revisar el campo `procesado`.

### 10.7 `GET /api/pantallas/vuelos` - Tablero de vuelos

Devuelve hasta 50 filas del tablero, ordenadas por `ultimaActualizacionUtc` descendente. Se utiliza al abrir una pantalla, antes de suscribirse al stream.

```
GET http://localhost:5097/api/pantallas/vuelos
```

**Respuesta `200 OK`**

```json
[
  {
    "vueloId": "AR1234",
    "tipoOperacion": "Salida",
    "aerolineaOOrigen": "EZE",
    "destino": "Madrid (MAD)",
    "horario": "18:45",
    "puerta": "B4",
    "pista": "09L",
    "estado": "Embarcando",
    "severidadVisual": "Informacion",
    "ultimaActualizacionUtc": "2026-10-18T15:40:00.512Z"
  }
]
```

### 10.8 `GET /api/pantallas/eventos` - Carga inicial de avisos públicos

Devuelve los últimos 30 eventos con `esPublico = true`, del más reciente al más antiguo.

```
GET http://localhost:5097/api/pantallas/eventos
```

**Respuesta `200 OK`:** arreglo de objetos `LogEntry` con el mismo formato que en 10.2.

### 10.9 `GET /api/pantallas/stream` - Canal SSE público

Abre una conexión persistente con `Content-Type: text/event-stream`. Solo emite los eventos con `esPublico = true`, sin datos internos.

```
GET http://localhost:5097/api/pantallas/stream
Accept: text/event-stream
```

```bash
curl -N http://localhost:5097/api/pantallas/stream
```

**Contenido recibido**

```
: conectado

data: {"id":"7d9e2b10-...","origen":"GestionVuelos","tipoEvento":"EstadoVueloCambiado","severidad":"Informacion","mensaje":"Vuelo AR1234 embarcando en puerta B4","ubicacionId":"AR1234","timestampEmision":"2026-10-18T15:40:00Z","esPublico":true,"vuelo":{"vueloId":"AR1234","tipoOperacion":"Salida","aerolineaOOrigen":"EZE","destino":"Madrid (MAD)","horario":"18:45","puerta":"B4","pista":"09L","estado":"Embarcando","severidadVisual":"Informacion","ultimaActualizacionUtc":"2026-10-18T15:40:00.512Z"}}

: heartbeat

```

- La línea `: conectado` es un comentario SSE que confirma la suscripción.
- La línea `: heartbeat` se envía cada 15 segundos sin eventos; el cliente la ignora.
- Cada mensaje `data:` contiene los campos `id`, `origen`, `tipoEvento`, `severidad`, `mensaje`, `ubicacionId`, `timestampEmision`, `esPublico` y `vuelo`.
- `vuelo` contiene la fila actualizada del tablero cuando el evento modificó un vuelo; en caso contrario es `null`.
- No se reenvían eventos anteriores a la conexión; para el estado inicial se deben usar los endpoints 10.7 y 10.8.

### 10.10 `GET /api/logs/stream-interno` - Canal SSE interno

Igual que el canal público, pero emite **todos** los logs, públicos o no, con el `LogEntry` completo. Está pensado para la bitácora interna y los PopUps de monitoreo.

```bash
curl -N http://localhost:5097/api/logs/stream-interno
```

**Contenido recibido**

```
: conectado

data: {"log":{"id":"...","eventId":"...","origen":"Equipaje","tipoEvento":"TimeoutError","severidad":"Error","mensaje":"Timeout de 5s al consultar Check-in luego de 3 reintentos","ubicacionId":"Cinta-3","timestampEmision":"...","fechaRegistroUtc":"...","expiraEnUtc":"...","esPublico":false,"payloadJson":"{...}"},"vuelo":null}

```

Cada mensaje `data:` tiene la forma `{ "log": <LogEntry>, "vuelo": <VueloPantalla o null> }`.

---

## 11. Códigos de estado y manejo de errores

| Código | Aplica a | Causa | Acción del cliente |
|---|---|---|---|
| `200 OK` | 10.1 a 10.4, 10.6 a 10.10 | Consulta correcta, o envelope recibido (revisar `procesado`). | Continuar. |
| `201 Created` | 10.5 | Log creado. | Continuar. |
| `400 Bad Request` | 10.2, 10.5, 10.6 | Campos obligatorios vacíos, JSON mal formado, fecha o número inválido. | Corregir la solicitud. No reintentar sin cambios. |
| `401 Unauthorized` | 10.2, 10.4 | Solo con `RequireJwtInLogs = true`: token ausente, vencido, con firma inválida o con `audience` distinto de `logs-api`. | Obtener un token válido del Grupo 7. |
| `404 Not Found` | 10.3 | El log no existe o el identificador no es un GUID. | Verificar el `id`. |
| `415 Unsupported Media Type` | 10.5, 10.6 | Falta `Content-Type: application/json`. | Agregar el encabezado. |
| `5xx` | Todos | Error interno del servicio. | Reintentar con espera creciente y número limitado de intentos. |

Los errores de validación propios del servicio tienen la forma `{ "error": "<mensaje>" }`.

---

## 12. Pruebas con Postman o Insomnia

### 12.1 Preparación

1. Crear un entorno con la variable `baseUrl = http://localhost:5097`.
2. Crear una colección con las peticiones de la sección 10, usando `{{baseUrl}}/api/...`.
3. En las peticiones `POST`, seleccionar *Body*, luego *raw* y luego *JSON*, y pegar los ejemplos de esta documentación.
4. Opcionalmente, ejecutar `curl -N {{baseUrl}}/api/logs/stream-interno` en una terminal y abrir `http://localhost:5097/` en el navegador para ver el tablero y los PopUps.

### 12.2 Casos de prueba sugeridos

| N.º | Petición | Resultado esperado |
|---|---|---|
| 1 | `GET /health` | `200` con `status: "Healthy"`. |
| 2 | `POST /api/logs` con `severidad: "Aprobado"` y `tipoEvento: "AutorizacionOtorgada"` | `201`; PopUp verde; el aviso figura en la tabla de avisos públicos. |
| 3 | `GET` a la URL del encabezado `Location` de la prueba 2 | `200` con el mismo log. |
| 4 | `POST /api/logs` sin el campo `mensaje` | `400` con `{ "error": "El componente de origen y el mensaje son obligatorios." }`. |
| 5 | `POST /api/logs` con `severidad: "Critica"` | `201` con `severidad: "Informacion"` (valor no reconocido). |
| 6 | `POST /api/logs/envelope` con tipo `PistaBloqueada` | `200` con `procesado: true`; severidad `Error`; ubicación `Pista 09L`. |
| 7 | Repetir exactamente la petición 6 | `200` con `procesado: false` (mismo `eventId`). |
| 8 | Repetir la petición 6 con otro `eventId` y el mismo `timestamp` | `200` con `procesado: false` (timestamp no posterior). |
| 9 | `POST /api/logs/envelope` sin `timestamp` | `400`. |
| 10 | `POST /api/logs/envelope` con tipo `EstadoVueloCambiado` y `payload` con `vueloId`, `destino`, `horario`, `puerta` y `estadoNuevo` | `200`; el vuelo aparece en `GET /api/pantallas/vuelos` y se actualiza en la pantalla. |
| 10b | Nuevo envelope del mismo vuelo con `estadoNuevo: "Demorado"` y sin `destino` ni `puerta` (otro `eventId` y `timestamp` posterior) | El estado pasa a `Demorado` en amarillo; `destino` y `puerta` se conservan. |
| 10c | Envelope de `Equipaje` con tipo `EquipajeDespachado`, `vueloId` y `estado: "EnBodega"` | `200`; el log se guarda pero la fila del vuelo no cambia. |
| 10d | Envelope de `GestionVuelos` con `vueloId: "ar1234"` (minúsculas) | Se actualiza la fila existente `AR1234`; no se crea una fila nueva. |
| 11 | `POST /api/logs/envelope` con tipo `DemoraVuelo` | `200`; severidad `Alerta`; `esPublico: false`; no aparece en el canal público. |
| 12 | `GET /api/logs?severidad=Error&pageSize=5` | `200`; como máximo 5 registros, incluido el de la prueba 6. |
| 13 | `GET /api/logs?origen=Inexistente` | `200` con `[]`. |
| 14 | `GET /api/logs/resumen` | `200` con las métricas. |
| 15 | Con `Jwt:RequireJwtInLogs = true`, `GET /api/logs` sin token | `401`. |

### 12.3 Archivo `.http` (Visual Studio / VS Code)

El archivo `Logs/Logs.http` incluye peticiones de ejemplo. Puede ampliarse con el siguiente contenido:

```http
@baseUrl = http://localhost:5097

### Estado del servicio
GET {{baseUrl}}/health

### Registrar log directo
POST {{baseUrl}}/api/logs
Content-Type: application/json

{
  "origen": "TorreControl",
  "ubicacionId": "AR1234",
  "severidad": "Aprobado",
  "tipoEvento": "AutorizacionOtorgada",
  "mensaje": "Autorización de despegue otorgada",
  "mostrarEnPantallaPublica": true
}

### Enviar envelope de vuelo
POST {{baseUrl}}/api/logs/envelope
Content-Type: application/json

{
  "eventId": "{{$guid}}",
  "tipo": "EstadoVueloCambiado",
  "origen": "GestionVuelos",
  "timestamp": "{{$datetime iso8601}}",
  "payload": {
    "vueloId": "AR1234",
    "destino": "Madrid (MAD)",
    "horario": "18:45",
    "puerta": "B4",
    "estadoNuevo": "Embarcando"
  }
}

### Consultar logs
GET {{baseUrl}}/api/logs?severidad=Error&page=1&pageSize=20

### Resumen para el dashboard
GET {{baseUrl}}/api/logs/resumen

### Tablero de vuelos
GET {{baseUrl}}/api/pantallas/vuelos

### Eventos públicos
GET {{baseUrl}}/api/pantallas/eventos
```

### 12.4 Prueba del bus con la consola de RabbitMQ

En `http://localhost:15672`: ingresar a *Exchanges*, seleccionar `aeropuerto_events` y abrir *Publish message*. Pegar un envelope en el campo *Payload* y publicar. Luego verificar el resultado con `GET /api/logs`.

---

## 13. Pantallas Públicas y simulador

La página `http://localhost:5097/` (archivo `wwwroot/index.html`, con la lógica en `app.js` y los estilos en `style.css`) incluye cuatro áreas:

| Área | Qué muestra | De dónde obtiene los datos |
|---|---|---|
| **Indicador de conexión** | Estado del canal SSE público: verde si está conectado, amarillo si está reconectando. | Eventos `onopen` y `onerror` del canal SSE público. |
| **Tablero de Salidas y Llegadas** | Una fila por vuelo: operación, vuelo, destino, horario, puerta y estado coloreado según severidad. | Carga inicial con `GET /api/pantallas/vuelos`; actualización con el campo `vuelo` del canal SSE público. |
| **Avisos Públicos del Aeropuerto** | Eventos públicos: hora, sector o vuelo, tipo y mensaje. | Carga inicial con `GET /api/pantallas/eventos`; actualización con el canal SSE público. |
| **Bitácora Interna Completa** | Todos los logs, públicos o no. | Carga inicial con `GET /api/pantallas/eventos`; actualización con el canal SSE interno. |
| **PopUps** | Ventana emergente por cada log nuevo, coloreada según severidad, que se cierra sola a los 7 segundos. | Canal SSE interno. |

**Simulador de pruebas:** cuatro botones que envían eventos de ejemplo con `POST /api/logs`:

| Botón | Qué envía | Resultado visible |
|---|---|---|
| Info (Vuelo) | Vuelo `AR1234` a Roma, estado `Programado`, severidad `Informacion`. | Fila azul en el tablero, aviso público y PopUp. |
| Aprobado | Vuelo `AR1234`, estado `Despegando`, severidad `Aprobado`. | La fila de `AR1234` cambia a verde. |
| Alerta | Vuelo `LA450` a Santiago, estado `Demorado`, severidad `Alerta`. | Fila amarilla nueva. |
| Error Interno (Solo Bitácora) | Timeout de Equipaje, `mostrarEnPantallaPublica: false`. | PopUp rojo y bitácora; no aparece en el tablero ni en los avisos públicos. |

**URL de la API:** si la página se abre desde el propio servicio, usa rutas relativas. Si se abre como archivo local o con Live Server (puerto 5500), apunta a `http://localhost:5097`. Esta lógica está en la constante `API_BASE_URL` de `app.js`.

---

## 14. Seguridad

La API está configurada para validar tokens **JWT Bearer** emitidos por el Grupo 7 (Seguridad):

| Parámetro | Valor |
|---|---|
| Algoritmo | Firma simétrica HS256 con la clave `Jwt:Key` |
| `audience` esperado | `Jwt:Audience` (por defecto `logs-api`) |
| Validación de emisor (`issuer`) | No se valida |
| Validación de expiración | Sí |
| Endpoints protegidos | `GET /api/logs`, `GET /api/logs/{id}` y `GET /api/logs/resumen`, solo si `Jwt:RequireJwtInLogs = true` |

Formato del encabezado cuando se exija autenticación:

```
Authorization: Bearer <token>
```

Ejemplo de contenido de un token válido para este servicio (flujo *client credentials* del Grupo 7):

```json
{
  "sub": "panel-administracion-service",
  "aud": "logs-api",
  "role": "service",
  "iat": 1755870600,
  "exp": 1755874200
}
```

Para habilitar la protección se debe establecer `Jwt:RequireJwtInLogs = true` y configurar `Jwt:Key` con la clave real acordada con el Grupo 7. No se debe utilizar el valor de desarrollo en un entorno compartido.

Los endpoints de ingesta (`POST`), de pantallas y los canales SSE no exigen token. El canal SSE interno queda abierto porque `EventSource` no permite enviar el encabezado `Authorization` (sección 16).

---

## 15. Resolución de problemas

| Síntoma | Causa probable | Solución |
|---|---|---|
| Mensaje *"RabbitMQ no disponible... Reintentando en 10s"* | El broker no está en ejecución, o el host, puerto o credenciales son incorrectos. | Iniciar RabbitMQ o corregir la sección `RabbitMq`. El servicio se conecta automáticamente en el siguiente intento. La API REST no se ve afectada. |
| Mensaje *"MongoDB no disponible... Operando con memoria local acotada"* | MongoDB no está en ejecución o la cadena de conexión es incorrecta. | Iniciar MongoDB y reiniciar el servicio: la conexión se evalúa solo al iniciar. |
| Los logs desaparecen al reiniciar | El servicio está en modo de memoria local. | Configurar MongoDB (sección 5). |
| `GET /api/logs` devuelve `[]` con filtros | El valor del filtro no coincide completo (por ejemplo `Torre` en lugar de `TorreControl`), o la página pedida no tiene datos. | Utilizar el valor completo (las mayúsculas no importan) y revisar `page`. |
| `415 Unsupported Media Type` | Falta el encabezado `Content-Type`. | Enviar `Content-Type: application/json`. |
| `400 Bad Request` en `POST /api/logs` | Falta `origen` o `mensaje`, o el JSON es inválido. | Revisar el cuerpo. |
| `400 Bad Request` en el envelope | Falta `origen`, `tipo` o `timestamp`. | Completar el envelope. |
| `procesado: false` en el envelope | Idempotencia: `eventId` repetido o `timestamp` no posterior al último del mismo origen, tipo y ubicación. | Enviar un `eventId` nuevo y un `timestamp` más reciente. |
| `401 Unauthorized` | `RequireJwtInLogs = true` y el token falta, venció o no corresponde. | Solicitar un token al Grupo 7 con `audience = logs-api`. |
| Un evento por RabbitMQ no aparece | JSON inválido, campos obligatorios vacíos, o descarte por idempotencia. | Revisar el log del servicio y validar el envelope. |
| Un vuelo aparece con destino `Sin especificar`, horario `--:--` o puerta `-` | Ningún evento de ese vuelo informó esos datos todavía. | Enviar `destino`, `horario` y `puerta` al menos en el primer evento del vuelo (por ejemplo, en `VueloCreado`); los siguientes eventos los conservan (sección 8.6). |
| Un evento con `vueloId` no cambia el tablero | El evento no es de `GestionVuelos` y su tipo no menciona `Vuelo`. | Es el comportamiento esperado (sección 8.6). Los cambios de estado de vuelo los informa Gestión de Vuelos. |
| Otros equipos no pueden conectarse | El servicio escucha solo en `localhost` o el firewall bloquea el puerto. | Iniciar con `--urls "http://0.0.0.0:5097"` y habilitar el puerto. |
| El contenedor no se conecta a MongoDB o RabbitMQ | Dentro del contenedor, `localhost` es el propio contenedor. | Usar `host.docker.internal` o el nombre del contenedor en la misma red (sección 6.2). |
| `docker build` falla al copiar los `.csproj` | Se ejecutó desde la carpeta `Logs/Logs`. | Ejecutar desde la raíz del repositorio con `-f Logs/Dockerfile`. |
| El stream SSE no muestra datos | No se generaron eventos después de conectarse, o un proxy retiene la respuesta. | Generar un evento de prueba; usar `curl -N`; verificar que el proxy respete `X-Accel-Buffering: no`. |
| RabbitMQ rechaza la conexión desde otro equipo | El usuario `guest` solo puede conectarse desde `localhost`. | Usar un usuario propio del broker compartido en `RabbitMq:Username` y `RabbitMq:Password`. |

---

## 16. Limitaciones conocidas

1. **Página pública y monitoreo en la misma vista.** `index.html` muestra en una sola página el tablero público, la bitácora interna, los PopUps y el simulador para facilitar la demostración e integración del trabajo práctico. En un despliegue productivo, las pantallas de terminal utilizarían únicamente el canal público (`/api/pantallas/stream`).
2. **Autenticación en canales SSE.** Cuando `Jwt:RequireJwtInLogs = true`, se protegen `GET /api/logs`, `GET /api/logs/{id}` y `GET /api/logs/resumen`. El canal SSE interno (`/api/logs/stream-interno`) permanece abierto porque la API estándar `EventSource` de los navegadores no soporta el envío del encabezado `Authorization: Bearer`.
3. **Conexión a MongoDB evaluada al iniciar.** Si MongoDB no está disponible cuando arranca el microservicio, el repositorio opera en modo de memoria local acotada (hasta 1000 registros) durante esa ejecución.
4. **Sin total global de páginas en el listado.** `GET /api/logs` devuelve el arreglo paginado según `page` y `pageSize`, sin incluir un metadato adicional con el conteo total de páginas.
5. **Sin cola de mensajes fallidos (DLQ).** Los mensajes de RabbitMQ con JSON inválido o sin campos obligatorios se descartan mediante `Nack` (`requeue: false`) y se registran en el log de consola del servicio.
6. **Escalado horizontal de SSE.** Si se ejecutan múltiples instancias detrás de un balanceador, cada instancia mantiene en memoria las conexiones SSE de sus propios clientes.

---

## 17. Referencia de componentes

Esta sección describe la función de cada parte del código, desde dónde se llama y para qué se usa.

### 17.1 Dominio (`Domain`)

| Componente | Archivo | Función | Dónde se usa |
|---|---|---|---|
| `LogEntry` | `Entities/LogEntry.cs` | Representa un registro de log persistido (sección 7.1). | Lo crea `ProcesarEventoUseCase`; lo guarda y consulta `MongoLogRepository`; lo difunde `SsePantallaNotifier`; lo devuelven los endpoints de consulta. |
| `VueloPantalla` | `Entities/VueloPantalla.cs` | Representa una fila del tablero de vuelos (sección 7.2). | Lo crea `ProcesarEventoUseCase`; lo guarda `MongoLogRepository` en `TableroVuelos`; lo envía `SsePantallaNotifier` en el campo `vuelo`; lo devuelve `GET /api/pantallas/vuelos`. |
| `NivelSeveridad` | `Enums/Serveridad.cs` | Enumeración de las cuatro severidades. | `ProcesarEventoUseCase` (métodos `DeterminarSeveridadAutomatica` y `NormalizarSeveridad`). |

### 17.2 Aplicación (`Aplication`)

**Contratos y configuración**

| Componente | Archivo | Función | Implementado por / Dónde se usa |
|---|---|---|---|
| `ILogRepository` | `Abstractions/ILogRepository.cs` | Contrato de persistencia de logs y vuelos. | Implementado por `MongoLogRepository`. Usado por ambos casos de uso. |
| `IPantallaNotifier` | `Abstractions/IPantallaNotifier.cs` | Contrato de notificación en tiempo real. | Implementado por `SsePantallaNotifier`. Usado por `ProcesarEventoUseCase`. |
| `LogsBusinessConfig` | `Config/LogsBusinessConfig.cs` | Retención (`DiasRetencionLogs`), lista de eventos públicos y método `EsEventoPublico`. | Se carga en `Program.cs` desde `BusinessConfig`. Usado por `ProcesarEventoUseCase`. |

**DTOs**

| Componente | Archivo | Función | Dónde se usa |
|---|---|---|---|
| `BusEventEnvelopeDto` | `DTOs/BusEventEnvelopeDto.cs` | Envelope estándar del bus. | Deserializado en `RabbitMqConsumerHostedService` y recibido en `POST /api/logs/envelope`; procesado por `EjecutarDesdeBusAsync`. |
| `DirectLogRequestDto` | `DTOs/DirectLogRequestDto.cs` | Cuerpo de un log directo. | Recibido en `POST /api/logs`; procesado por `EjecutarDirectoAsync`. |
| `LogFilterDto` | `DTOs/LogFilterDto.cs` | Filtros y paginación de consulta. | Armado en `GET /api/logs`; usado por `GetFilteredAsync`. |
| `DashboardResumenDto` | `DTOs/DashboardResumenDto.cs` | Métricas del dashboard. | Armado por `GetResumenDashboardAsync`; devuelto por `GET /api/logs/resumen`. |

**`ProcesarEventoUseCase`** (`UseCases/ProcesarEventoUseCase.cs`): ingesta de eventos.

| Método | Función | Se llama desde |
|---|---|---|
| `EjecutarDesdeBusAsync(envelope)` | Flujo B completo (sección 3.3). Devuelve `true` si guardó y `false` si descartó. | `RabbitMqConsumerHostedService` (por cada mensaje) y endpoint `POST /api/logs/envelope`. |
| `EjecutarDirectoAsync(dto)` | Flujo A completo (sección 3.2). Devuelve el `LogEntry` creado. | Endpoint `POST /api/logs`. |
| `ActualizarTableroVuelosSiCorrespondeAsync` (privado) | Decide si el envelope modifica el tablero, combina los datos del `payload` con la fila existente y la guarda (sección 8.6). | `EjecutarDesdeBusAsync`. |
| `TruncarMilisegundos` (privado) | Recorta una fecha a milisegundos en UTC. | `EjecutarDesdeBusAsync` y `EjecutarDirectoAsync`. |
| `DeterminarSeveridadAutomatica` (privado) | Deduce la severidad a partir del tipo y del estado del `payload` (sección 8.4). Devuelve el nombre de la severidad como texto. | `EjecutarDesdeBusAsync`. |
| `NormalizarSeveridad` (privado) | Convierte el texto recibido en una severidad válida. | `EjecutarDirectoAsync`. |
| `ExtraerCampoPayload` (privado) | Devuelve el valor de un campo del `payload` como texto, buscando el nombre sin distinguir mayúsculas. Acepta valores de texto, numéricos y booleanos; si el campo no existe, está vacío o es de otro tipo, devuelve `null`. | `EjecutarDesdeBusAsync`, `ActualizarTableroVuelosSiCorrespondeAsync`, `ExtraerIdentificadorUbicacion` y `ConstruirMensajeLegible`. |
| `ExtraerIdentificadorUbicacion` (privado) | Obtiene la ubicación del `payload` (sección 8.5). | `EjecutarDesdeBusAsync` y `ConstruirMensajeLegible`. |
| `ConstruirMensajeLegible` (privado) | Obtiene o genera el mensaje del log (sección 8.5). | `EjecutarDesdeBusAsync`. |

**`ConsultarLogsUseCase`** (`UseCases/ConsultarLogsUseCase.cs`): consultas de solo lectura.

| Método | Función | Se llama desde | Llama a |
|---|---|---|---|
| `EjecutarAsync(filtro)` | Logs filtrados y paginados. | `GET /api/logs` | `GetFilteredAsync` |
| `ObtenerPorIdAsync(id)` | Un log por `id`. | `GET /api/logs/{id}` | `GetByIdAsync` |
| `ObtenerResumenDashboardAsync()` | Métricas del dashboard. | `GET /api/logs/resumen` | `GetResumenDashboardAsync` |
| `ObtenerTableroVuelosAsync()` | Filas del tablero. | `GET /api/pantallas/vuelos` | `GetVuelosPantallaAsync` |
| `ObtenerEventosPantallaPublicaAsync()` | Últimos 30 eventos públicos. | `GET /api/pantallas/eventos` | `GetRecentPublicEventsAsync(30)` |

### 17.3 Infraestructura (`Infrastructure`)

**`MongoLogRepository`** (`Repositories/MongoLogRepository.cs`): implementa `ILogRepository` con MongoDB y respaldo en memoria. Se registra como *singleton*.

| Método | Función | Se llama desde |
|---|---|---|
| Constructor | Configura serializadores, se conecta y crea índices (sección 3.1). | Contenedor de dependencias, la primera vez que se necesita. |
| `SaveAsync` | Inserta un log; si el `eventId` ya existe lo ignora; si falla por otro motivo, lo guarda en memoria. | `EjecutarDesdeBusAsync`, `EjecutarDirectoAsync`. |
| `GetByIdAsync` | Busca un log por `id`. | `ObtenerPorIdAsync`. |
| `ExistsEventIdAsync` | Indica si ya existe un log con ese `eventId`. | `EjecutarDesdeBusAsync`. |
| `GetFilteredAsync` | Filtra (sin distinguir mayúsculas), ordena y pagina logs; corrige el rango de fechas invertido y los valores de paginación fuera de rango. | `EjecutarAsync`. |
| `GetRecentPublicEventsAsync` | Últimos eventos públicos. | `ObtenerEventosPantallaPublicaAsync`. |
| `GetLastTimestampAsync` | Último `timestampEmision` por origen, tipo y ubicación. | `EjecutarDesdeBusAsync`. |
| `GetVueloPantallaByIdAsync` | Devuelve la fila actual de un vuelo, buscando el identificador sin distinguir mayúsculas, para combinarla con los datos nuevos. | `EjecutarDirectoAsync`, `ActualizarTableroVuelosSiCorrespondeAsync`. |
| `UpsertVueloPantallaAsync` | Normaliza el `vueloId` a mayúsculas e inserta o reemplaza la fila del vuelo (ya combinada). | `EjecutarDirectoAsync`, `ActualizarTableroVuelosSiCorrespondeAsync`. |
| `GetVuelosPantallaAsync` | Hasta 50 filas del tablero. | `ObtenerTableroVuelosAsync`, `GetResumenDashboardAsync`. |
| `GetResumenDashboardAsync` | Calcula las métricas del dashboard con conteos y una agregación por `origen` en MongoDB (o sobre la memoria local). | `ObtenerResumenDashboardAsync`. |

**`SsePantallaNotifier`** (`Notifications/SsePantallaNotifier.cs`): implementa `IPantallaNotifier`. Se registra como *singleton* (una única lista de suscriptores para toda la aplicación).

| Componente / Método | Función | Se llama desde |
|---|---|---|
| `SseSubscriber` | Suscripción de un cliente: cola propia de hasta 100 mensajes e indicador `SoloPublicos`. | Creado por `SuscribirCliente`. |
| `SuscribirCliente(soloPublicos)` | Registra un cliente y devuelve su identificador y el lector de su cola. | `ManejarStreamSseAsync` en `Program.cs`. |
| `DesuscribirCliente(id)` | Elimina la suscripción y cierra su cola. | `ManejarStreamSseAsync`, al cerrarse la conexión. |
| `NotificarEventoAsync(log, vuelo)` | Arma el mensaje público y el interno y los deposita en la cola de cada suscriptor (sección 3.5). | `EjecutarDesdeBusAsync`, `EjecutarDirectoAsync`. |

**`RabbitMqConsumerHostedService`** (`Messaging/RabbitMqConsumerHostedService.cs`): servicio en segundo plano registrado con `AddHostedService` en `Program.cs`; ASP.NET Core lo inicia automáticamente al arrancar la aplicación.

| Método | Función |
|---|---|
| `ExecuteAsync` | Conecta, declara exchange y cola, consume mensajes y reintenta la conexión (sección 3.6). Por cada mensaje llama a `ProcesarEventoUseCase.EjecutarDesdeBusAsync`. |
| `Dispose` | Cierra el canal y la conexión al detener la aplicación. |

### 17.4 API (`Logs`)

| Componente | Función |
|---|---|
| `Program.cs` | Registra las dependencias (sección 3.1), configura el middleware y define los endpoints de la sección 10. |
| `ManejarStreamSseAsync` (función local de `Program.cs`) | Atiende una conexión SSE: configura encabezados, suscribe al cliente, escribe los mensajes y envía el *heartbeat*. La usan `GET /api/pantallas/stream` (`soloPublicos: true`) y `GET /api/logs/stream-interno` (`soloPublicos: false`). |
| `Dockerfile` | Compila y publica el proyecto en una imagen basada en `aspnet:8.0` que escucha en el puerto 8080 (sección 6.2). |
| `Logs.http` | Peticiones de prueba para Visual Studio o VS Code (sección 12.3). |
| `appsettings.json` | Configuración del servicio (sección 5). |
| `Properties/launchSettings.json` | Perfiles `http` (puerto 5097), `https` (7078 y 5097) e IIS Express para desarrollo local. |

### 17.5 Interfaz web (`Logs/wwwroot`)

**`app.js`**

| Función / Bloque | Función | Se llama desde |
|---|---|---|
| `API_BASE_URL` | Define la URL base de la API (sección 13). | Todas las llamadas `fetch` y `EventSource`. |
| `mostrarPopUp(log)` | Crea un PopUp coloreado según la severidad y lo elimina a los 7 segundos. | Canal SSE interno. |
| `renderizarFilaVuelo(vuelo)` | Crea o reemplaza la fila del vuelo en el tablero, identificada por `vuelo-<vueloId>`. | Carga inicial de `/api/pantallas/vuelos` y canal SSE público. |
| `agregarFilaPublica(log)` | Agrega un aviso a la tabla pública si `esPublico` es verdadero. | Carga inicial de `/api/pantallas/eventos` y canal SSE público. |
| `agregarBitacora(log)` | Agrega una línea a la bitácora interna. | Carga inicial de `/api/pantallas/eventos` y canal SSE interno. |
| Carga inicial | Dos `fetch` a `/api/pantallas/vuelos` y `/api/pantallas/eventos` al abrir la página. | Se ejecuta al cargar el script. |
| `ssePublico` | `EventSource` sobre `/api/pantallas/stream`. Al conectarse, el indicador `conn-status` muestra *Conectado en Tiempo Real (SSE)* en verde; si se pierde la conexión, muestra *Reconectando SSE...* en amarillo mientras el navegador reintenta. | Se abre al cargar el script. |
| `sseInterno` | `EventSource` sobre `/api/logs/stream-interno`. | Se abre al cargar el script. |
| `simularVuelo(...)` | Envía un `POST /api/logs` de tipo `EstadoVueloCambiado` con datos de vuelo. | Botones Info, Aprobado y Alerta de `index.html`. |
| `simularErrorInterno()` | Envía un `POST /api/logs` de error no público. | Botón Error Interno de `index.html`. |

**`index.html`** define la estructura de la página (tablero, avisos, simulador, bitácora y contenedor de PopUps) y **`style.css`** los colores por severidad (`sev-Informacion`, `sev-Aprobado`, `sev-Alerta`, `sev-Error`), las tablas y los PopUps.

---

**Grupo 8 - Logs / Notificaciones y Pantallas | Aeropuerto Internacional UAI**
