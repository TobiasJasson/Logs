window.addEventListener('error', function (evento) {
    console.error('Error crítico detectado:', evento.error, evento.message, 'Línea:', evento.lineno);
});

const API_BASE_URL = window.location.protocol === 'file:' || window.location.port === '5500'
    ? 'http://localhost:5097'
    : '';

const tablaVuelos = document.getElementById('tabla-vuelos');
const tablaPublica = document.getElementById('tabla-publica');
const bitacoraInterna = document.getElementById('bitacora-interna');
const popupContainer = document.getElementById('popup-container');

const titulos = {
    'Informacion': '[INFORMACIÓN]',
    'Aprobado': '[APROBADO]',
    'Alerta': '[ALERTA]',
    'Error': '[ERROR CRÍTICO]'
};

function mostrarPopUp(log) {
    const card = document.createElement('div');
    card.className = `popup-card ${log.severidad}`;
    const hora = new Date(log.timestampEmision).toLocaleTimeString();

    card.innerHTML = `
        <div class="popup-header">
            <span>${titulos[log.severidad] || '[NOTIFICACIÓN]'}</span>
            <span class="close-btn" onclick="this.parentElement.parentElement.remove()">X</span>
        </div>
        <div>${log.mensaje}</div>
        <div class="popup-meta">Origen: <b>${log.origen}</b> | ID: <b>${log.ubicacionId}</b> | ${hora}</div>
    `;
    popupContainer.prepend(card);
    setTimeout(() => { if (card.parentElement) card.remove(); }, 7000);
}

function renderizarFilaVuelo(vuelo) {
    if (!vuelo || !vuelo.vueloId) return;
    let tr = document.getElementById(`vuelo-${vuelo.vueloId}`);
    if (!tr) {
        tr = document.createElement('tr');
        tr.id = `vuelo-${vuelo.vueloId}`;
        tablaVuelos.prepend(tr);
    }
    tr.innerHTML = `
        <td>${vuelo.tipoOperacion || 'Salida'}</td>
        <td><b>${vuelo.vueloId}</b></td>
        <td>${vuelo.destino}</td>
        <td>${vuelo.horario}</td>
        <td><b>${vuelo.puerta}</b></td>
        <td><span class="badge sev-${vuelo.severidadVisual || 'Informacion'}">${vuelo.estado.toUpperCase()}</span></td>
    `;
}

function agregarFilaPublica(log) {
    if (!log.esPublico) return;
    const tr = document.createElement('tr');
    const hora = new Date(log.timestampEmision).toLocaleTimeString();
    tr.innerHTML = `
        <td>${hora}</td>
        <td><b>${log.ubicacionId}</b></td>
        <td>${log.tipoEvento}</td>
        <td><span class="badge sev-${log.severidad}">${log.severidad.toUpperCase()}</span> ${log.mensaje}</td>
    `;
    tablaPublica.prepend(tr);
}

function agregarBitacora(log) {
    const div = document.createElement('div');
    div.className = 'log-item';
    const hora = new Date(log.timestampEmision).toLocaleTimeString();
    div.innerHTML = `<span class="badge sev-${log.severidad}">${log.severidad}</span> <b>[${hora}] ${log.origen} (${log.ubicacionId}):</b> ${log.mensaje}`;
    bitacoraInterna.prepend(div);
}

// 1. Cargar estado inicial de vuelos y eventos públicos
fetch(`${API_BASE_URL}/api/pantallas/vuelos`)
    .then(r => r.json())
    .then(vuelos => vuelos.reverse().forEach(renderizarFilaVuelo))
    .catch(err => console.warn('Sin vuelos iniciales:', err));

fetch(`${API_BASE_URL}/api/pantallas/eventos`)
    .then(r => r.json())
    .then(logs => logs.reverse().forEach(l => {
        agregarFilaPublica(l);
        agregarBitacora(l);
    }))
    .catch(err => console.warn('Sin eventos iniciales:', err));

// 2. Canal SSE Público (Pantallas Públicas: actualiza vuelos y avisos públicos)
const ssePublico = new EventSource(`${API_BASE_URL}/api/pantallas/stream`);
ssePublico.onmessage = (event) => {
    const data = JSON.parse(event.data);
    if (data.vuelo) renderizarFilaVuelo(data.vuelo);
    agregarFilaPublica(data);
};

// 3. Canal SSE Interno (Bitácora completa y PopUps de monitoreo)
const sseInterno = new EventSource(`${API_BASE_URL}/api/logs/stream-interno`);
sseInterno.onmessage = (event) => {
    const data = JSON.parse(event.data);
    if (data.log) {
        mostrarPopUp(data.log);
        agregarBitacora(data.log);
    }
};

async function simularVuelo(severidad, origen, vueloId, destino, horario, puerta, estadoVuelo, mensaje) {
    await fetch(`${API_BASE_URL}/api/logs`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            origen: origen,
            ubicacionId: vueloId,
            severidad: severidad,
            tipoEvento: 'EstadoVueloCambiado',
            mensaje: mensaje,
            mostrarEnPantallaPublica: true,
            destino: destino,
            horario: horario,
            puerta: puerta,
            estadoVuelo: estadoVuelo,
            tipoOperacion: 'Salida'
        })
    });
}

async function simularErrorInterno() {
    await fetch(`${API_BASE_URL}/api/logs`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            origen: 'Equipaje',
            ubicacionId: 'Cinta-3',
            severidad: 'Error',
            tipoEvento: 'TimeoutError',
            mensaje: 'Timeout de 5s al consultar Check-in luego de 3 reintentos (Solo Log Interno)',
            mostrarEnPantallaPublica: false
        })
    });
}