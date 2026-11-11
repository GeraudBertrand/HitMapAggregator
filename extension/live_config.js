'use strict';

const $ = (id) => document.getElementById(id);

let auth = null;

async function call(method, path, body) {
  const response = await fetch(`${window.HITMAP_EBS_URL}/api/control/${path}`, {
    method,
    headers: {
      'Authorization': `Bearer ${auth.token}`,
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });

  if (response.status === 403) {
    throw new Error('reserve au proprietaire de la chaine');
  }

  if (!response.ok) {
    const text = await response.text();
    throw new Error(`${response.status} ${text}`);
  }

  return response.json();
}

async function guarded(action) {
  $('error').textContent = '';
  try {
    await action();
  } catch (error) {
    $('error').textContent = error.message;
  }
}

function showState(state) {
  $('status').textContent = state.running ? 'en cours' : 'arretee';

  // Sans session, le serveur n'a pas encore de reglages a modifier : la collecte doit
  // avoir ete demarree une premiere fois.
  const settings = state.settings;
  for (const input of $('settings').querySelectorAll('select, input')) {
    input.disabled = !settings;
  }

  if (settings) {
    $('renderMode').value = settings.renderMode;
    $('visible').checked = settings.visible;
    $('allowAnonymous').checked = settings.allowAnonymous;
  }
}

function showToken(result) {
  const url = `${window.HITMAP_EBS_URL}/overlay/?channel=${encodeURIComponent(result.channelId)}&token=${encodeURIComponent(result.token)}`;
  $('overlay-url').value = url;
}

async function refresh() {
  showState(await call('GET', 'state'));
  showToken(await call('GET', 'overlay-token'));
}

async function command(path) {
  await call('POST', path);
  showState(await call('GET', 'state'));
}

async function patch(body) {
  await call('POST', 'settings', body);
  showState(await call('GET', 'state'));
}

Twitch.ext.onAuthorized((value) => {
  const first = auth === null;
  auth = value;
  if (first) {
    guarded(refresh);
  }
});

$('start').addEventListener('click', () => guarded(() => command('start')));
$('stop').addEventListener('click', () => guarded(() => command('stop')));
$('reset').addEventListener('click', () => guarded(() => command('reset')));

$('renderMode').addEventListener('change', (e) => guarded(() => patch({ renderMode: e.target.value })));
$('visible').addEventListener('change', (e) => guarded(() => patch({ visible: e.target.checked })));
$('allowAnonymous').addEventListener('change', (e) => guarded(() => patch({ allowAnonymous: e.target.checked })));

$('show').addEventListener('click', () => {
  const field = $('overlay-url');
  field.type = field.type === 'password' ? 'text' : 'password';
});

$('copy').addEventListener('click', () => guarded(async () => {
  try {
    await navigator.clipboard.writeText($('overlay-url').value);
  } catch {
    // L'iframe Twitch peut refuser le presse-papiers : on laisse la selection manuelle.
    const field = $('overlay-url');
    field.type = 'text';
    field.select();
    throw new Error('copie automatique refusee, utiliser Ctrl+C');
  }
}));

// Confirmation en deux clics plutot que confirm() : l'iframe Twitch est sandboxee et peut
// bloquer les boites modales, ce qui rendrait le bouton muet.
let rotateArmed = 0;
$('rotate').addEventListener('click', () => guarded(async () => {
  const button = $('rotate');

  if (!rotateArmed) {
    button.textContent = "Confirmer (l'URL actuelle cessera de marcher)";
    rotateArmed = setTimeout(() => {
      rotateArmed = 0;
      button.textContent = 'Regenerer';
    }, 4000);
    return;
  }

  clearTimeout(rotateArmed);
  rotateArmed = 0;
  button.textContent = 'Regenerer';
  showToken(await call('POST', 'overlay-token/rotate'));
}));
