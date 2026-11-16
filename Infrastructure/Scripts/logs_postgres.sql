-- ============================================================================
-- Grupo 8 - Logs / Notificaciones y Pantallas
-- Tablas del servicio de logs en el schema "logs" de la base compartida "aeropuerto".
-- ============================================================================

CREATE TABLE IF NOT EXISTS logs.logs_centralizados (
    id                  uuid        PRIMARY KEY,
    event_id            uuid        NOT NULL,
    origen              text        NOT NULL,
    tipo_evento         text        NOT NULL,
    severidad           text        NOT NULL,
    mensaje             text        NOT NULL,
    ubicacion_id        text        NOT NULL,
    timestamp_emision   timestamptz NOT NULL,
    fecha_registro_utc  timestamptz NOT NULL,
    expira_en_utc       timestamptz NOT NULL,
    es_publico          boolean     NOT NULL,
    payload             jsonb       NOT NULL DEFAULT '{}'::jsonb
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_logs_unique_event_id ON logs.logs_centralizados (event_id);
CREATE INDEX IF NOT EXISTS idx_logs_idempotencia ON logs.logs_centralizados (lower(origen), lower(tipo_evento), lower(ubicacion_id), timestamp_emision DESC);
CREATE INDEX IF NOT EXISTS idx_logs_expiracion ON logs.logs_centralizados (expira_en_utc);
CREATE INDEX IF NOT EXISTS idx_logs_timestamp ON logs.logs_centralizados (timestamp_emision DESC);
CREATE INDEX IF NOT EXISTS idx_logs_publicos  ON logs.logs_centralizados (timestamp_emision DESC) WHERE es_publico;

CREATE TABLE IF NOT EXISTS logs.tablero_vuelos (
    vuelo_id                  text        PRIMARY KEY,
    tipo_operacion            text        NOT NULL,
    aerolinea_o_origen        text        NOT NULL,
    destino                   text        NOT NULL,
    horario                   text        NOT NULL,
    puerta                    text        NOT NULL,
    pista                     text        NOT NULL,
    estado                    text        NOT NULL,
    severidad_visual          text        NOT NULL,
    ultima_actualizacion_utc  timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_vuelos_actualizacion ON logs.tablero_vuelos (ultima_actualizacion_utc DESC);