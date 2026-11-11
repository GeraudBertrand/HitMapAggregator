import { render } from './renderers/index.js';

const params = new URLSearchParams(location.search);
const channelId = params.get('channel') ?? 'demo';
const token = params.get('token') ?? '';

// `?mode=` force le rendu, sinon on suit le mode transporte par le serveur.
// L'override existe pour pouvoir comparer les deux rendus sur un meme flux.
const forcedMode = params.get('mode');

const canvas = document.getElementById('heatmap');
const context = canvas.getContext('2d');
const status = document.getElementById('status');

let snapshot = null;
let source = null;
let retryDelay = 1000;

function resize() {
  // On dessine a la resolution physique : sur un ecran HiDPI, s'en tenir aux pixels CSS
  // donnerait des ellipses floues.
  const ratio = window.devicePixelRatio || 1;
  canvas.width = Math.round(window.innerWidth * ratio);
  canvas.height = Math.round(window.innerHeight * ratio);
  draw();
}

function decodeGrid(data, count) {
  const binary = atob(data);
  const cells = new Float32Array(count);

  // Les octets sont ramenes dans [0,1] ; l'echelle reelle voyage a part dans `grid.max`.
  for (let i = 0; i < count; i++) {
    cells[i] = binary.charCodeAt(i) / 255;
  }

  return cells;
}

function draw() {
  context.clearRect(0, 0, canvas.width, canvas.height);

  if (!snapshot || !snapshot.visible) {
    return;
  }

  render(context, canvas, snapshot, forcedMode);
}

function showStatus(message) {
  if (message) {
    status.textContent = message;
    status.style.display = 'block';
  } else {
    status.style.display = 'none';
  }
}

function connect() {
  source = new EventSource(
    `/api/stream/heatmap?channelId=${encodeURIComponent(channelId)}&token=${encodeURIComponent(token)}`);

  source.addEventListener('snapshot', (event) => {
    const payload = JSON.parse(event.data);
    payload.cells = decodeGrid(payload.grid.data, payload.grid.w * payload.grid.h);
    snapshot = payload;

    retryDelay = 1000;
    showStatus(null);
    draw();
  });

  source.addEventListener('error', () => {
    // EventSource se reconnecte tout seul, mais pas si le serveur a ferme franchement :
    // on relance a la main, avec un delai qui double pour ne pas marteler.
    // EventSource ne donne pas le code HTTP : un jeton revoque (401) et un serveur
    // injoignable se ressemblent ici.
    showStatus(`overlay deconnecte (serveur injoignable ou URL revoquee), nouvelle tentative dans ${retryDelay / 1000} s`);

    if (source.readyState === EventSource.CLOSED) {
      source.close();
      setTimeout(connect, retryDelay);
      retryDelay = Math.min(retryDelay * 2, 15000);
    }
  });
}

window.addEventListener('resize', resize);
resize();
connect();
