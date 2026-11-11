'use strict';

// Configuration hors live : URL de l'overlay OBS et reglages de la hitmap.

function showSettings(settings) {
  $('renderMode').value = settings.renderMode;
  $('visible').checked = settings.visible;
  $('allowAnonymous').checked = settings.allowAnonymous;
}

function showToken(result) {
  $('overlay-url').value =
    `${window.HITMAP_EBS_URL}/overlay/?channel=${encodeURIComponent(result.channelId)}&token=${encodeURIComponent(result.token)}`;
}

async function patch(body) {
  showSettings(await call('POST', 'settings', body));
}

onFirstAuth(async () => {
  // /state cree la session si besoin : les reglages existent avant tout demarrage.
  showSettings((await call('GET', 'state')).settings);
  showToken(await call('GET', 'overlay-token'));
});

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

confirmable($('rotate'), "Confirmer (l'URL actuelle cessera de marcher)", async () => {
  showToken(await call('POST', 'overlay-token/rotate'));
});
