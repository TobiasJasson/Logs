# Servicio de Logs Centralizados y Notificaciones

**Aeropuerto Internacional UAI — Grupo 8 (Logs / Notificaciones)**

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
17. [Resumen de componentes](#17-resumen-de-componentes)

---

## 1. Descripción general

Este repositorio contiene el servicio desarrollado por el **Grupo 8** del proyecto *Aeropuerto Internacional UAI*. Su función es actuar como **bitácora centralizada del sistema** y como **canal de notificaciones en tiempo real**.

Los demás grupos (Torre de Control, Gestión de Vuelos, Meteorología, Seguridad, Panel de Administración, entre otros) informan a este servicio los eventos relevantes de su operación. El servicio se encarga de:

1. **Registrar** cada evento en una base de datos centralizada (MongoDB) con una política de retención automática.
2. **Clasificar** cada evento según su severidad: `Informacion`, `Aprobado`, `Alerta` o `Error`.
3. **Notificar en tiempo real** a las Pantallas Públicas (tablero de salidas y llegadas) y a los PopUps de las aplicaciones, mediante Server-Sent Events (SSE).
4. **Exponer una API REST de consulta** para que el Panel de Administración (Grupo 9) pueda listar y filtrar los registros.

### Vías de ingreso de eventos

| Vía | Descripción | Uso recomendado |
|---|---|---|
| **RabbitMQ** | Los grupos publican un mensaje con el *envelope* estándar en el exchange `aeropuerto_events`. | Integración asíncrona oficial entre grupos. |
| **API REST directa** (`POST /api/logs`) | El grupo envía un log ya estructurado (origen, severidad, mensaje, etc.). | Registro puntual desde cualquier lenguaje o desde un método compartido. |
| **API REST envelope** (`POST /api/logs/envelope`) | Recibe el mismo envelope que el bus, pero por HTTP. | Pruebas de integración sin broker y casos en que RabbitMQ no esté disponible. |

### Responsabilidades del servicio

El servicio **sí** realiza las siguientes tareas: recibir, validar, clasificar, persistir, expirar y difundir eventos, y permitir su consulta filtrada.

El servicio **no** realiza las siguientes tareas: emitir tokens de autenticación (corresponde al Grupo 7), decidir reglas operativas del aeropuerto (corresponde a cada grupo emisor) ni reintentar el envío de eventos en nombre de otros grupos.

---

## 2. Arquitectura

La solución está desarrollada en **.NET 8** con **Arquitectura Limpia** (Clean Architecture) y aplicación del principio de inversión de dependencias. Las capas internas no conocen a las externas: los casos de uso dependen de interfaces y la infraestructura las implementa.

### Estructura del repositorio

```
Logs/
├── Logs.slnx                          Solución
├── Domain/                            Núcleo del dominio (sin dependencias)
│   ├── Entities/LogEntry.cs           Entidad del registro de log
│   └── Enums/Serveridad.cs            Enumeración Severidad
├── Aplication/                        Casos de uso y contratos
│   ├── Abstractions/
│   │   ├── ILogRepository.cs          Contrato de persistencia
│   │   └── IPantallaNotifier.cs       Contrato de notificación en tiempo real
│   ├── Config/LogsBusinessConfig.cs   Retención y eventos públicos
│   ├── DTOs/
│   │   ├── BusEventEnvelopeDto.cs     Envelope estándar del bus
│   │   ├── DirectLogRequestDto.cs     Solicitud de log directo
│   │   └── LogFilterDto.cs            Filtros de consulta
│   └── UseCases/
│       ├── ProcesarEventoUseCase.cs   Ingesta de eventos
│       └── ConsultarLogsUseCase.cs    Consulta de eventos
├── Infrastructure/                    Implementaciones concretas
│   ├── Messaging/RabbitMqConsumerHostedService.cs
│   ├── Notifications/SsePantallaNotifier.cs
│   └── Repositories/MongoLogRepository.cs
└── Logs/                              Proyecto web (API.csproj)
    ├── Program.cs                     Composición, middleware y endpoints
    ├── appsettings*.json              Configuración
    └── wwwroot/index.html             Pantallas Públicas y simulador
```

### Dependencias entre proyectos

```
Logs (API)  ---->  Aplication  ---->  Domain
    |                  ^
    +----> Infrastructure  (implementa las interfaces de Aplication)
```

### Tecnologías

| Componente | Tecnología |
|---|---|
| Plataforma | .NET 8, ASP.NET Core Minimal API |
| Persistencia | MongoDB (`MongoDB.Driver`), colección `LogsCentralizados`, índice TTL |
| Mensajería | RabbitMQ (`RabbitMQ.Client` 7.x), exchange tipo *topic* |
| Tiempo real | Server-Sent Events (SSE) |
| Autenticación | JWT Bearer (tokens emitidos por el Grupo 7) |
| Documentación | Swagger / Swashbuckle |

---

## 3. Funcionamiento interno

### 3.1 Inicio de la aplicación

Al iniciar, `Program.cs` realiza las siguientes acciones:

1. Registra como *singleton* la configuración de negocio, el repositorio de logs y el notificador SSE.
2. Registra como *scoped* los casos de uso `ProcesarEventoUseCase` y `ConsultarLogsUseCase`.
3. Registra el consumidor de RabbitMQ como servicio en segundo plano (`BackgroundService`).
4. Configura CORS (cualquier origen, encabezado y método), Swagger, archivos estáticos y autenticación JWT.
5. Publica los cinco endpoints REST.

El repositorio de MongoDB, al construirse, crea un **índice TTL** sobre el campo `ExpiraEnUtc`, de modo que MongoDB elimine automáticamente los logs vencidos.

### 3.2 Flujo A: log directo (`POST /api/logs`)

```
Cliente --> POST /api/logs --> ProcesarEventoUseCase.EjecutarDirectoAsync
                                  1. Valida que origen y mensaje no estén vacíos
                                  2. Normaliza la severidad
                                  3. Completa valores por defecto (tipo, ubicación)
                                  4. Calcula EsPublico
                                  5. Calcula ExpiraEnUtc (ahora + retención)
                                  6. Guarda en el repositorio
                                  7. Notifica por SSE a todos los clientes conectados
                               <-- 201 Created + LogEntry
```

En este flujo **no se aplica idempotencia**: cada llamada genera un nuevo registro.

### 3.3 Flujo B: envelope (RabbitMQ o `POST /api/logs/envelope`)

```
Publicador --> RabbitMQ (aeropuerto_events) --> grupo8_logs_queue --+
                                                                    +--> ProcesarEventoUseCase.EjecutarDesdeBusAsync
Cliente HTTP --> POST /api/logs/envelope ---------------------------+      1. Valida origen y tipo
                                                                           2. Verifica idempotencia (origen + tipo)
                                                                           3. Deduce la severidad a partir del tipo
                                                                           4. Determina si el evento es público
                                                                           5. Extrae la ubicación y el mensaje del payload
                                                                           6. Guarda y notifica por SSE
```

### 3.4 Flujo C: consulta (`GET /api/logs`)

El Panel de Administración envía los filtros como parámetros de consulta. `ConsultarLogsUseCase` los traduce a un `LogFilterDto` y el repositorio ejecuta la búsqueda, ordenando por `timestampEmision` descendente y limitando el resultado a **200 registros**.

### 3.5 Flujo D: tiempo real (SSE)

1. Un cliente abre `GET /api/pantallas/stream`. El servidor responde con `Content-Type: text/event-stream`, envía el comentario `: conectado` y mantiene la conexión abierta.
2. `SsePantallaNotifier` registra el escritor de ese cliente en un diccionario concurrente.
3. Cada vez que se guarda un log (por cualquiera de los flujos A o B), el notificador serializa el `LogEntry` en JSON (camelCase) y lo escribe como `data: <json>` en todas las conexiones activas.
4. Si la escritura en una conexión falla, el cliente se elimina de la lista. Al cerrarse la conexión, también se desuscribe.

### 3.6 Consumidor de RabbitMQ

`RabbitMqConsumerHostedService` se ejecuta durante toda la vida de la aplicación:

1. Se conecta al broker (`RabbitMq:Host`).
2. Declara el exchange `aeropuerto_events` (tipo *topic*, durable) y la cola `grupo8_logs_queue` (durable), y las vincula con la clave `#`, por lo que **recibe todos los mensajes sin importar la routing key**.
3. Consume con confirmación manual:
   - Mensaje procesado correctamente (o descartado por idempotencia): `Ack`.
   - Mensaje con JSON inválido o que falla la validación: `Nack` **sin reencolar**, es decir, se descarta.
4. Si el broker no está disponible, registra una advertencia y reintenta cada **10 segundos**. La API REST continúa operativa durante ese lapso.

### 3.7 Tolerancia a fallos

| Situación | Comportamiento |
|---|---|
| No hay `MongoDb:ConnectionString` o no se puede conectar | Se utiliza una colección en memoria. Los datos se pierden al reiniciar. |
| Falla una consulta o inserción en MongoDB | Se registra la advertencia; las consultas leen desde memoria. |
| RabbitMQ no disponible | Reintento cada 10 segundos; la API REST sigue funcionando. |
| Falla la escritura hacia un cliente SSE | Se elimina esa suscripción sin afectar a los demás. |

---

## 4. Requisitos

| Requisito | Detalle | Obligatorio |
|---|---|---|
| .NET SDK | 8.0 o superior | Sí |
| MongoDB | 6.x o 7.x (por defecto `localhost:27017`) | Recomendado. Sin MongoDB se usa memoria local. |
| RabbitMQ | 3.x (puertos 5672 y 15672 con el plugin de administración) | Opcional. Necesario solo para la integración por bus. |
| Postman o Insomnia | Para probar los endpoints | Opcional |
| Navegador moderno | Para las Pantallas Públicas (soporte de `EventSource`) | Opcional |

La solución utiliza el formato `Logs.slnx`. Si la versión del SDK o del IDE no lo soporta, el proyecto puede ejecutarse directamente desde la carpeta `Logs/`.

### Servicios auxiliares con Docker (opcional)

```bash
docker run -d --name mongo  -p 27017:27017 mongo:7
docker run -d --name rabbit -p 5672:5672 -p 15672:15672 rabbitmq:3-management
```

La consola de administración de RabbitMQ queda disponible en `http://localhost:15672` (usuario `guest`, contraseña `guest`).

---

## 5. Configuración

El servicio funciona sin configuración adicional (RabbitMQ en `localhost` y persistencia en memoria). Para utilizar MongoDB y personalizar el resto, se deben agregar las siguientes claves en `Logs/appsettings.json` o como variables de entorno:

```json
{
  "MongoDb": {
    "ConnectionString": "mongodb://localhost:27017",
    "DatabaseName": "AeropuertoLogsDb"
  },
  "RabbitMq": {
    "Host": "localhost",
    "Exchange": "aeropuerto_events",
    "Queue": "grupo8_logs_queue"
  },
  "Jwt": {
    "Key": "<clave-simetrica-acordada-con-el-grupo-7>"
  }
}
```

| Clave | Valor por defecto | Descripción |
|---|---|---|
| `MongoDb:ConnectionString` | vacío (memoria local) | Cadena de conexión a MongoDB. |
| `MongoDb:DatabaseName` | `AeropuertoLogsDb` | Nombre de la base de datos. La colección es `LogsCentralizados`. |
| `RabbitMq:Host` | `localhost` | Host del broker. |
| `RabbitMq:Exchange` | `aeropuerto_events` | Exchange tipo *topic*, durable. |
| `RabbitMq:Queue` | `grupo8_logs_queue` | Cola durable propia del Grupo 8. |
| `Jwt:Key` | valor interno del proyecto | Clave simétrica (HS256) con la que el Grupo 7 firma los tokens. Debe configurarse con la clave real en cualquier entorno compartido. |

En variables de entorno, el separador de niveles es el doble guion bajo: `MongoDb__ConnectionString`, `RabbitMq__Host`, `Jwt__Key`.

Los parámetros de negocio (`DiasRetencionLogs = 30` y la lista de eventos públicos) se definen en `Aplication/Config/LogsBusinessConfig.cs`.

---

## 6. Instalación y ejecución

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
| Pantallas Públicas y simulador | `http://localhost:5097/index.html` |

### Exponer el servicio a otras máquinas

Por defecto el perfil `http` escucha únicamente en `localhost`. Para que otros grupos puedan invocar la API desde sus equipos, se debe indicar que escuche en todas las interfaces:

```bash
dotnet run --project Logs --urls "http://0.0.0.0:5097"
```

La URL base que utilicen los demás grupos será `http://<IP-o-nombre-del-equipo-del-Grupo-8>:5097`. Debe verificarse que el firewall permita el puerto elegido.

---

## 7. Modelo de datos

Cada evento se persiste y se difunde con la estructura `LogEntry`. En las respuestas JSON los nombres de campo están en camelCase.

| Campo | Tipo | Descripción |
|---|---|---|
| `id` | GUID | Identificador interno del registro. |
| `eventId` | GUID | Identificador del evento original (provisto por el emisor en el envelope). |
| `origen` | string | Grupo o componente que emitió el evento (por ejemplo `TorreControl`). |
| `tipoEvento` | string | Tipo de evento (por ejemplo `AutorizacionOtorgada`). |
| `severidad` | string | `Informacion`, `Aprobado`, `Alerta` o `Error`. |
| `mensaje` | string | Texto descriptivo mostrado en la bitácora y en el PopUp. |
| `ubicacionId` | string | Identificador de vuelo, pista o método donde ocurrió el evento. |
| `timestampEmision` | datetime (UTC) | Momento de emisión informado por el origen. Se usa para idempotencia y ordenamiento. |
| `fechaRegistroUtc` | datetime (UTC) | Momento en que el servicio guardó el registro. |
| `expiraEnUtc` | datetime (UTC) | Fecha a partir de la cual el registro se elimina automáticamente. |
| `esPublico` | boolean | Indica si el evento se muestra en las Pantallas Públicas. |
| `payloadJson` | string | Contenido original del evento serializado como texto JSON. |

### Severidades

| Severidad | Color del PopUp | Significado | Ejemplos |
|---|---|---|---|
| `Informacion` | Azul | Eventos generales o rutinarios | Reporte climático normal, cambio de estado de vuelo |
| `Aprobado` | Verde | Operación autorizada o completada | `AutorizacionOtorgada`, `PistaLiberada`, `VueloCreado` |
| `Alerta` | Amarillo | Situación que requiere atención | Demora, pista ocupada, viento fuerte |
| `Error` | Rojo | Rechazo o falla | `AutorizacionDenegada`, `PistaBloqueada`, timeout entre servicios |

---

## 8. Reglas de negocio

### 8.1 Retención

Cada log se guarda con `expiraEnUtc = fecha de registro + 30 días`. MongoDB elimina los registros vencidos mediante el índice TTL. El valor se define en `DiasRetencionLogs`.

### 8.2 Eventos públicos

Un evento es público (`esPublico = true`) y llega a las Pantallas Públicas cuando su tipo pertenece al siguiente conjunto. La comparación es **exacta y sensible a mayúsculas y minúsculas**:

`EstadoVueloCambiado`, `VueloCreado`, `PistaBloqueada`, `PistaLiberada`, `AutorizacionOtorgada`, `AutorizacionDenegada`, `ReporteClimatico`.

En el log directo, además, el emisor puede forzar la publicación con `mostrarEnPantallaPublica = true`. En el envelope no existe ese indicador: solo los tipos de la lista anterior son públicos.

### 8.3 Idempotencia (solo envelope)

Antes de guardar un envelope, el servicio busca el último `timestampEmision` registrado para la combinación **`origen` + `tipo`**. Si el `timestamp` recibido es **menor o igual** al último guardado, el evento se descarta por considerarse duplicado o llegado fuera de orden. La respuesta REST indica `procesado: false`; en RabbitMQ el mensaje se confirma (`Ack`) sin guardarse.

Consecuencias para los emisores:

- Cada evento debe llevar un `timestamp` estrictamente creciente para su combinación origen + tipo.
- Dos eventos distintos del mismo origen y tipo (por ejemplo, dos vuelos) con el mismo `timestamp` serán tratados como duplicados. Se recomienda utilizar marcas de tiempo con precisión de milisegundos o más.
- El envelope debe incluir siempre `timestamp`. Si se omite, se interpreta como `0001-01-01` y los eventos posteriores del mismo tipo serían descartados.

### 8.4 Severidad automática (solo envelope)

La severidad se deduce del texto del `tipo`, sin distinguir mayúsculas. Se evalúan las reglas en este orden:

| Prioridad | El tipo contiene | Severidad |
|---|---|---|
| 1 | `error`, `denegada`, `bloqueada`, `timeout` | `Error` |
| 2 | `alerta`, `ocupada`, `demora`, `viento` | `Alerta` |
| 3 | `otorgada`, `liberada`, `creado`, `arribado`, `aprobado` | `Aprobado` |
| 4 | Ninguna de las anteriores | `Informacion` |

Ejemplos:

| Tipo | Severidad resultante | Público |
|---|---|---|
| `VueloCreado` | Aprobado | Sí |
| `EstadoVueloCambiado` | Informacion | Sí |
| `PistaBloqueada` | Error | Sí |
| `PistaLiberada` | Aprobado | Sí |
| `AutorizacionOtorgada` | Aprobado | Sí |
| `AutorizacionDenegada` | Error | Sí |
| `ReporteClimatico` | Informacion | Sí |
| `DemoraVuelo` | Alerta | No |
| `PistaOcupada` | Alerta | No |
| `VueloArribado` | Aprobado | No |
| `ErrorTimeoutServicio` | Error | No |

En el log directo la severidad la indica el emisor (ver sección 10.2).

### 8.5 Ubicación y mensaje (solo envelope)

- **Ubicación:** se toma del `payload` según esta prioridad: `vueloId` (valor tal cual), `pista` (se guarda como `Pista <valor>`), `ubicacionId` (valor tal cual). Si no existe ninguna, se guarda `Sistema`.
- **Mensaje:** si el `payload` incluye el campo `mensaje`, se utiliza ese texto. En caso contrario se genera `[<origen>] Evento '<tipo>' registrado sobre <ubicación>.`

---

## 9. Guía de integración para los demás grupos

Esta sección describe cómo debe invocar cada grupo al servicio.

### 9.1 Elección de la vía de integración

| Criterio | Usar RabbitMQ (envelope) | Usar REST (`POST /api/logs`) |
|---|---|---|
| Acoplamiento | Desacoplado: el emisor no depende de que este servicio esté activo. | El emisor depende de la disponibilidad del servicio. |
| Severidad | La deduce el servicio a partir del tipo. | La define el emisor explícitamente. |
| Idempotencia | Sí (por origen + tipo + timestamp). | No. |
| Control de publicación en pantallas | Solo tipos públicos predefinidos. | Campo `mostrarEnPantallaPublica`. |
| Caso típico | Eventos de negocio del dominio (vuelos, pistas, autorizaciones, clima). | Mensajes de diagnóstico, errores técnicos y avisos puntuales. |

Los dos mecanismos pueden combinarse en un mismo grupo.

### 9.2 Requisitos comunes de toda petición HTTP

| Aspecto | Requisito |
|---|---|
| URL base | `http://<host-del-grupo-8>:5097` (en desarrollo local, `http://localhost:5097`). |
| Encabezado `Content-Type` | `application/json` en todas las peticiones con cuerpo. De lo contrario, el servicio responde `415 Unsupported Media Type`. |
| Codificación | UTF-8. |
| Nombres de propiedad | Se aceptan sin distinguir mayúsculas y minúsculas (`origen`, `Origen`). Se recomienda camelCase. |
| Fechas | ISO 8601 en **UTC** con sufijo `Z`, por ejemplo `2026-10-18T15:30:00Z`. Es obligatorio para `timestamp` y recomendado en los filtros `fechaDesde` y `fechaHasta`. |
| Identificadores | `eventId` debe ser un GUID válido (formato `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`). |
| Autenticación | Los endpoints actualmente no exigen token. Cuando se habilite, se enviará `Authorization: Bearer <token>` con un JWT emitido por el Grupo 7 y `audience = logs-api`. Se recomienda que los clientes ya lo envíen. |
| CORS | El servicio permite cualquier origen, método y encabezado; las aplicaciones web pueden invocarlo y suscribirse al stream desde otro dominio. |
| Tamaño y volumen | `GET /api/logs` devuelve como máximo 200 registros; no hay paginación (ver sección 16). |

### 9.3 Vía A: REST directo

**Solicitud**

```
POST {baseUrl}/api/logs
Content-Type: application/json
```

| Campo | Tipo | Obligatorio | Valor por defecto | Observaciones |
|---|---|---|---|---|
| `origen` | string | Sí | — | Nombre del grupo o componente emisor. Debe ser constante y consistente entre llamadas (por ejemplo `GestionVuelos`). |
| `mensaje` | string | Sí | — | Texto legible que se mostrará en la bitácora y el PopUp. |
| `severidad` | string | No | `Informacion` | Valores aceptados (sin distinguir mayúsculas): `Informacion`, `Aprobado`, `Alerta`, `Error`. Cualquier otro valor se guarda como `Informacion`. |
| `tipoEvento` | string | No | `LogManual` | Clasificación del evento. Si coincide con un tipo público, el log se muestra en pantallas. |
| `ubicacionId` | string | No | `General` | Vuelo, pista o nombre del método donde ocurrió. |
| `mostrarEnPantallaPublica` | boolean | No | `false` | Fuerza la aparición en las Pantallas Públicas. |

**Ejemplo**

```json
{
  "origen": "GestionVuelos",
  "ubicacionId": "AR5500",
  "severidad": "Alerta",
  "tipoEvento": "DemoraVuelo",
  "mensaje": "Demora de 20 minutos por carga de equipaje",
  "mostrarEnPantallaPublica": true
}
```

**Respuestas**

| Código | Significado |
|---|---|
| `201 Created` | Log guardado. El cuerpo contiene el `LogEntry` creado. |
| `400 Bad Request` | Falta `origen` o `mensaje`, o el cuerpo no es un JSON válido. |
| `415 Unsupported Media Type` | No se envió `Content-Type: application/json`. |

### 9.4 Vía B: RabbitMQ

**Parámetros de conexión**

| Elemento | Valor |
|---|---|
| Host | El del broker acordado por los grupos (`localhost` en desarrollo). |
| Puerto | 5672 |
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
| `eventId` | GUID | Sí (recomendado) | Identificador único del evento. Si se omite o es vacío, el servicio genera uno. |
| `tipo` | string | Sí | Tipo de evento. Define severidad y publicidad (secciones 8.2 y 8.4). |
| `origen` | string | Sí | Grupo emisor. |
| `timestamp` | datetime UTC | Sí | Momento de emisión (ver idempotencia en la sección 8.3). |
| `payload` | objeto JSON | No | Datos del evento. Campos reconocidos: `mensaje`, `vueloId`, `pista`, `ubicacionId`. Puede incluir cualquier otro campo, que quedará guardado en `payloadJson`. |

**Ejemplo**

```json
{
  "eventId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "tipo": "EstadoVueloCambiado",
  "origen": "GestionVuelos",
  "timestamp": "2026-10-18T15:40:00Z",
  "payload": {
    "vueloId": "AR1234",
    "estadoAnterior": "Programado",
    "estadoNuevo": "Embarcando",
    "mensaje": "Vuelo AR1234 embarcando en puerta 5"
  }
}
```

**Qué ocurre con el mensaje**

| Resultado | Acción del servicio |
|---|---|
| Procesado correctamente | `Ack`. Se guarda y se notifica por SSE. |
| Descartado por idempotencia | `Ack`. No se guarda. |
| JSON inválido, o `origen`/`tipo` vacíos | `Nack` sin reencolar. El mensaje se descarta y se registra el error en el log del servicio. |

Como no existe cola de mensajes fallidos, los emisores deben validar el envelope antes de publicarlo.

### 9.5 Convenciones recomendadas para los emisores

| Elemento | Convención |
|---|---|
| `origen` | Nombre estable del grupo en PascalCase, sin espacios: `TorreControl`, `GestionVuelos`, `Meteorologia`, `Seguridad`, `PanelAdministracion`. |
| `tipo` / `tipoEvento` | PascalCase descriptivo, en pasado o estado: `VueloCreado`, `PistaBloqueada`. Incluir palabras clave de la tabla 8.4 cuando se quiera una severidad determinada. |
| `ubicacionId` / `payload.vueloId` / `payload.pista` | Usar siempre el mismo formato para el mismo recurso (`AR1234`, `09L`) para facilitar los filtros. |
| `mensaje` | Frase completa, legible por un operador, sin datos sensibles. |
| Fechas | Siempre UTC. |

### 9.6 Buenas prácticas para el cliente

1. **No bloquear la operación principal por el log.** Encapsular el envío en `try/catch`; un fallo del servicio de logs no debe interrumpir el proceso de negocio.
2. **Definir un tiempo de espera** (por ejemplo, 3 a 5 segundos) en las llamadas HTTP.
3. **Reintentar solo ante errores de red o `5xx`**, con un número limitado de intentos y espera creciente. **No reintentar ante `4xx`**: indican un problema en la solicitud.
4. **Considerar que `POST /api/logs` no es idempotente.** Un reintento tras un tiempo de espera agotado puede generar un registro duplicado.
5. **Reutilizar la conexión HTTP** (en .NET, una única instancia de `HttpClient` o `IHttpClientFactory`) y la conexión AMQP en lugar de abrirlas por cada evento.
6. **Generar un `eventId` nuevo por cada evento** y un `timestamp` creciente (sección 8.3).

### 9.7 Ejemplos de código

#### C# (.NET) — cliente REST reutilizable

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

#### C# (.NET) — publicación en RabbitMQ

```csharp
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

var factory = new ConnectionFactory { HostName = "localhost" };
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

#### Python — publicación en RabbitMQ (pika)

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
    "timestamp": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    "payload": {"vueloId": "AR1234", "mensaje": "Vuelo AR1234 creado"},
}

ch.basic_publish(
    exchange="aeropuerto_events",
    routing_key="GestionVuelos.VueloCreado",
    body=json.dumps(envelope),
    properties=pika.BasicProperties(content_type="application/json", delivery_mode=2),
)
conn.close()
```

#### Consumo del stream en tiempo real (aplicaciones web)

```js
const es = new EventSource("http://localhost:5097/api/pantallas/stream");

es.onmessage = (evento) => {
  const log = JSON.parse(evento.data);   // objeto LogEntry
  mostrarPopup(log.severidad, log.mensaje);
  if (log.esPublico) agregarFilaATablero(log);
};
```

### 9.8 Lista de verificación de integración

Antes de dar por finalizada la integración, cada grupo debe comprobar:

- [ ] La URL base es accesible desde el equipo del grupo (conectividad y firewall).
- [ ] Un `POST /api/logs` de prueba devuelve `201` y el registro aparece en `GET /api/logs?origen=<MiGrupo>`.
- [ ] Los PopUps de las cuatro severidades se visualizan correctamente.
- [ ] Si usa RabbitMQ: el exchange y la cola existen y un envelope de prueba se guarda (`GET /api/logs`).
- [ ] Los `timestamp` enviados son UTC y crecientes por origen + tipo.
- [ ] El código del emisor no se interrumpe si el servicio de logs no responde.
- [ ] Se envía el encabezado `Authorization` cuando el Grupo 7 habilite la validación.

---

## 10. Referencia de la API

**URL base:** `http://localhost:5097` · **Formato:** JSON · **Documentación interactiva:** `/swagger`

| N.º | Método | Ruta | Consumidor principal | Descripción |
|---|---|---|---|---|
| 1 | `GET` | `/api/logs` | Grupo 9 (Panel de Administración) | Consulta de logs con filtros. |
| 2 | `POST` | `/api/logs` | Cualquier grupo | Registro de un log directo. |
| 3 | `POST` | `/api/logs/envelope` | Cualquier grupo, pruebas | Ingreso de un envelope del bus por HTTP. |
| 4 | `GET` | `/api/pantallas/eventos` | Pantallas Públicas | Últimos 30 eventos públicos. |
| 5 | `GET` | `/api/pantallas/stream` | Pantallas Públicas y PopUps | Canal de eventos en tiempo real (SSE). |

### 10.1 `GET /api/logs` — Consultar logs

Devuelve hasta 200 registros ordenados por `timestampEmision` descendente. Todos los parámetros son opcionales y se combinan con el operador AND.

| Parámetro | Tipo | Ejemplo | Descripción |
|---|---|---|---|
| `fechaDesde` | datetime | `2026-10-01T00:00:00Z` | Límite inferior inclusivo sobre `timestampEmision`. |
| `fechaHasta` | datetime | `2026-10-31T23:59:59Z` | Límite superior inclusivo. |
| `origen` | string | `TorreControl` | Grupo emisor. |
| `tipo` | string | `PistaBloqueada` | Tipo de evento. |
| `severidad` | string | `Error` | `Informacion`, `Aprobado`, `Alerta` o `Error`. |

Con MongoDB, los filtros `origen`, `tipo` y `severidad` son **exactos y sensibles a mayúsculas y minúsculas**. En el modo de memoria local no distinguen mayúsculas.

**Solicitud (Postman / Insomnia)**

```
GET http://localhost:5097/api/logs?origen=TorreControl&severidad=Error&fechaDesde=2026-10-01T00:00:00Z
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

Si ningún registro coincide, la respuesta es `200 OK` con un arreglo vacío (`[]`). Un valor de fecha con formato inválido produce `400 Bad Request`.

### 10.2 `POST /api/logs` — Registrar un log directo

Guarda el log, lo difunde por SSE y devuelve el registro creado. Los campos y validaciones se describen en la sección 9.3.

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
  "payloadJson": "{\"Origen\":\"TorreControl\",\"UbicacionId\":\"AR1234\",\"Severidad\":\"Aprobado\",\"TipoEvento\":\"AutorizacionOtorgada\",\"Mensaje\":\"Autorización de despegue otorgada\",\"MostrarEnPantallaPublica\":true}"
}
```

**Respuesta `400 Bad Request`** (falta `origen` o `mensaje`)

```json
{ "error": "El componente de origen y el mensaje son obligatorios." }
```

### 10.3 `POST /api/logs/envelope` — Ingresar un envelope por HTTP

Procesa el mismo envelope que viaja por RabbitMQ, aplicando idempotencia, severidad automática y extracción de ubicación (secciones 8.3 a 8.5). Los campos se describen en la sección 9.4.

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

**Respuesta `200 OK` — evento procesado**

```json
{ "procesado": true, "eventId": "3fa85f64-5717-4562-b3fc-2c963f66afa6" }
```

El registro resultante queda con `severidad = "Error"`, `ubicacionId = "Pista 09L"` y `esPublico = true`.

**Respuesta `200 OK` — descartado por idempotencia** (mismo evento reenviado, o `timestamp` menor o igual al último del mismo origen y tipo)

```json
{ "procesado": false, "motivo": "Descartado por idempotencia (evento duplicado o antiguo)." }
```

**Respuesta `400 Bad Request`** (`origen` o `tipo` vacíos)

```json
{ "error": "El origen y el tipo de evento son obligatorios." }
```

Un descarte por idempotencia no es un error: la respuesta es `200`. El emisor debe revisar el campo `procesado`.

### 10.4 `GET /api/pantallas/eventos` — Carga inicial de pantallas

Devuelve los últimos 30 eventos con `esPublico = true`, del más reciente al más antiguo. Se utiliza al abrir una pantalla, antes de suscribirse al stream.

```
GET http://localhost:5097/api/pantallas/eventos
```

**Respuesta `200 OK`:** arreglo de objetos `LogEntry` con el mismo formato que en 10.1.

### 10.5 `GET /api/pantallas/stream` — Eventos en tiempo real (SSE)

Abre una conexión persistente con `Content-Type: text/event-stream`. Cada log guardado, por cualquier vía, se emite a todos los clientes conectados.

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

data: {"id":"7d9e2b10-...","eventId":"0c5e8a77-...","origen":"TorreControl","tipoEvento":"AutorizacionOtorgada","severidad":"Aprobado","mensaje":"Autorización de despegue otorgada","ubicacionId":"AR1234","timestampEmision":"2026-10-18T15:30:00.123Z","fechaRegistroUtc":"2026-10-18T15:30:00.123Z","expiraEnUtc":"2026-11-17T15:30:00.123Z","esPublico":true,"payloadJson":"{...}"}

```

- La línea `: conectado` es un comentario SSE que confirma la suscripción.
- Cada mensaje `data:` contiene un `LogEntry` completo. El cliente decide el color del PopUp según `severidad` y si agrega una fila al tablero según `esPublico`.
- El stream emite **todos** los logs, públicos o no. Las pantallas que solo deben mostrar eventos públicos deben filtrar por `esPublico`.
- No se reenvían eventos anteriores a la conexión; para el historial se debe usar el endpoint 10.4.

---

## 11. Códigos de estado y manejo de errores

| Código | Aplica a | Causa | Acción del cliente |
|---|---|---|---|
| `200 OK` | 10.1, 10.3, 10.4 | Consulta correcta, o envelope recibido (revisar `procesado`). | Continuar. |
| `201 Created` | 10.2 | Log creado. | Continuar. |
| `400 Bad Request` | 10.1, 10.2, 10.3 | Campos obligatorios vacíos, JSON mal formado o fecha inválida. | Corregir la solicitud. No reintentar sin cambios. |
| `415 Unsupported Media Type` | 10.2, 10.3 | Falta `Content-Type: application/json`. | Agregar el encabezado. |
| `401 Unauthorized` | Todos | Solo cuando se habilite la autenticación: token ausente, vencido o con `audience` distinto de `logs-api`. | Obtener un token válido del Grupo 7. |
| `5xx` | Todos | Error interno del servicio. | Reintentar con espera creciente y número limitado de intentos. |

Los errores de validación propios del servicio tienen la forma `{ "error": "<mensaje>" }`.

---

## 12. Pruebas con Postman o Insomnia

### 12.1 Preparación

1. Crear un entorno con la variable `baseUrl = http://localhost:5097`.
2. Crear una colección con las cinco peticiones de la sección 10, usando `{{baseUrl}}/api/...`.
3. En las peticiones `POST`, seleccionar *Body → raw → JSON* y pegar los ejemplos de esta documentación.
4. Opcionalmente, abrir `/api/pantallas/stream` en una pestaña adicional (Postman e Insomnia en sus versiones recientes permiten visualizar flujos SSE) o ejecutar `curl -N`, y abrir `http://localhost:5097/index.html` en el navegador para ver los PopUps.

### 12.2 Casos de prueba sugeridos

| N.º | Petición | Resultado esperado |
|---|---|---|
| 1 | `POST /api/logs` con `severidad: "Aprobado"` y `tipoEvento: "AutorizacionOtorgada"` | `201`; aparece un PopUp verde; el evento figura en el tablero público. |
| 2 | `POST /api/logs` sin el campo `mensaje` | `400` con `{ "error": "El componente de origen y el mensaje son obligatorios." }`. |
| 3 | `POST /api/logs` con `severidad: "Critica"` | `201` con `severidad: "Informacion"` (valor no reconocido). |
| 4 | `POST /api/logs/envelope` con tipo `PistaBloqueada` | `200` con `procesado: true`; severidad `Error`; ubicación `Pista 09L`. |
| 5 | Repetir exactamente la petición 4 | `200` con `procesado: false`. |
| 6 | `POST /api/logs/envelope` con tipo `DemoraVuelo` y `timestamp` posterior | `200` con `procesado: true`; severidad `Alerta`; `esPublico: false`. |
| 7 | `GET /api/logs?severidad=Error` | `200`; contiene los registros de las pruebas 4. |
| 8 | `GET /api/logs?origen=Inexistente` | `200` con `[]`. |
| 9 | `GET /api/pantallas/eventos` | `200`; contiene los eventos públicos, no los de la prueba 6. |
| 10 | Mantener abierto `/api/pantallas/stream` y ejecutar la prueba 1 | Llega un mensaje `data:` con el log creado. |

### 12.3 Archivo `.http` (Visual Studio / VS Code)

Puede reemplazar el contenido de `Logs/Logs.http`:

```http
@baseUrl = http://localhost:5097

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

### Enviar envelope
POST {{baseUrl}}/api/logs/envelope
Content-Type: application/json

{
  "eventId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "tipo": "PistaBloqueada",
  "origen": "TorreControl",
  "timestamp": "2026-10-18T15:35:00Z",
  "payload": { "pista": "09L", "mensaje": "Pista bloqueada por viento cruzado" }
}

### Consultar logs
GET {{baseUrl}}/api/logs?severidad=Error

### Eventos públicos
GET {{baseUrl}}/api/pantallas/eventos
```

### 12.4 Prueba del bus con la consola de RabbitMQ

En `http://localhost:15672`: *Exchanges → aeropuerto_events → Publish message*. Pegar un envelope en el campo *Payload* y publicar. Luego verificar el resultado con `GET /api/logs`.

---

## 13. Pantallas Públicas y simulador

La página `http://localhost:5097/index.html` incluye:

- **Tablero Público de Operaciones y Vuelos:** se inicializa con `GET /api/pantallas/eventos` y se actualiza en vivo con el stream SSE.
- **PopUps:** ventanas emergentes coloreadas según la severidad del evento recibido.
- **Simulador de pruebas:** cuatro botones (Información, Aprobado, Alerta y Error) que envían eventos de ejemplo a la API.
- **Bitácora Interna en Vivo:** lista todos los logs recibidos por el stream.

La página carga `style.css` y `app.js` desde `wwwroot/`; ambos archivos deben estar presentes junto a `index.html` para que la interfaz funcione.

---

## 14. Seguridad

La API está configurada para validar tokens **JWT Bearer** emitidos por el Grupo 7 (Seguridad):

| Parámetro | Valor |
|---|---|
| Algoritmo | Firma simétrica HS256 con la clave `Jwt:Key` |
| `audience` esperado | `logs-api` |
| Validación de emisor (`issuer`) | No se valida |
| Validación de expiración | Sí |

Formato del encabezado cuando se exija autenticación:

```
Authorization: Bearer <token>
```

En la versión actual los endpoints no exigen autenticación. Para protegerlos, se debe agregar `.RequireAuthorization()` a cada definición `app.Map...` en `Program.cs`. En cualquier entorno compartido debe configurarse `Jwt:Key` con la clave real acordada con el Grupo 7 y no utilizar el valor por defecto.

---

## 15. Resolución de problemas

| Síntoma | Causa probable | Solución |
|---|---|---|
| Mensaje *"RabbitMQ no disponible… Reintentando en 10s"* | El broker no está en ejecución o `RabbitMq:Host` es incorrecto. | Iniciar RabbitMQ. El servicio se conecta automáticamente en el siguiente intento. La API REST no se ve afectada. |
| Los logs desaparecen al reiniciar | No hay `MongoDb:ConnectionString`; se usa memoria local. | Configurar MongoDB (sección 5). |
| `GET /api/logs` devuelve `[]` con filtros | Con MongoDB los filtros distinguen mayúsculas y minúsculas. | Utilizar el valor exacto (`Error`, `Informacion`). |
| `415 Unsupported Media Type` | Falta el encabezado `Content-Type`. | Enviar `Content-Type: application/json`. |
| `400 Bad Request` en `POST /api/logs` | Falta `origen` o `mensaje`, o el JSON es inválido. | Revisar el cuerpo. |
| `procesado: false` en el envelope | Idempotencia: el `timestamp` no es posterior al último de ese origen y tipo. | Enviar un `timestamp` más reciente. |
| Un evento por RabbitMQ no aparece | JSON inválido, `origen`/`tipo` vacíos, o descarte por idempotencia. | Revisar el log del servicio y validar el envelope. |
| Otros equipos no pueden conectarse | El servicio escucha solo en `localhost` o el firewall bloquea el puerto. | Iniciar con `--urls "http://0.0.0.0:5097"` y habilitar el puerto. |
| El stream SSE no muestra datos | No se generaron eventos después de conectarse, o un proxy bufferiza la respuesta. | Generar un evento de prueba; usar `curl -N`. |
| La página de pantallas aparece sin estilos o sin datos | Faltan `style.css` o `app.js` en `wwwroot/`. | Agregar ambos archivos. |
| RabbitMQ rechaza la conexión desde otro equipo | El usuario `guest` solo puede conectarse desde `localhost`. | Ver limitaciones (sección 16). |

---

## 16. Limitaciones conocidas

1. **Sin paginación.** `GET /api/logs` devuelve como máximo 200 registros; los filtros por fecha permiten acotar la ventana.
2. **Sin consulta por identificador.** El encabezado `Location` de `POST /api/logs` apunta a `/api/logs/{id}`, pero no existe un endpoint que resuelva esa ruta.
3. **Autenticación no aplicada.** El JWT está configurado, pero ningún endpoint lo exige actualmente (sección 14).
4. **Credenciales de RabbitMQ fijas.** El consumidor utiliza las credenciales por defecto del cliente (`guest`/`guest`) y solo permite configurar el host. Para un broker remoto con otras credenciales se requiere ampliar la configuración.
5. **Sin cola de mensajes fallidos.** Los mensajes inválidos se descartan (`Nack` sin reencolar).
6. **Eventos públicos fijos.** La lista de tipos públicos está definida en el código (`LogsBusinessConfig`) y no se lee de `appsettings.json`.
7. **Idempotencia por origen y tipo.** No distingue entre recursos (por ejemplo, vuelos distintos) con el mismo timestamp (sección 8.3).
8. **Respuesta del envelope con `eventId` vacío.** Si el emisor no envía `eventId`, el registro se guarda con un identificador generado, pero la respuesta de `POST /api/logs/envelope` devuelve el valor recibido (GUID vacío).

---

## 17. Resumen de componentes

| Capa | Componente | Función |
|---|---|---|
| Aplicación | `ProcesarEventoUseCase.EjecutarDesdeBusAsync` | Procesa un envelope: valida, aplica idempotencia, clasifica severidad, extrae ubicación y mensaje, guarda y notifica. |
| Aplicación | `ProcesarEventoUseCase.EjecutarDirectoAsync` | Registra un log directo: valida, normaliza severidad, guarda y notifica. |
| Aplicación | `ConsultarLogsUseCase.EjecutarAsync` | Consulta logs aplicando filtros. |
| Aplicación | `ConsultarLogsUseCase.ObtenerEventosPantallaPublicaAsync` | Devuelve los últimos 30 eventos públicos. |
| Aplicación | `LogsBusinessConfig` | Define la retención (30 días) y los tipos de evento públicos. |
| Dominio | `LogEntry`, `Severidad` | Entidad del registro y enumeración de severidades. |
| Infraestructura | `MongoLogRepository` | Persistencia en MongoDB con índice TTL y respaldo en memoria. |
| Infraestructura | `RabbitMqConsumerHostedService` | Consumidor en segundo plano del exchange `aeropuerto_events`. |
| Infraestructura | `SsePantallaNotifier` | Administra las suscripciones SSE y difunde cada log. |
| API | `Program.cs` | Composición de dependencias, middleware (CORS, Swagger, JWT, archivos estáticos) y definición de los cinco endpoints. |

---

**Grupo 8 — Logs / Notificaciones · Aeropuerto Internacional UAI**
