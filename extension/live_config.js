'use strict';

// Pilotage pendant le live. Les reglages et l'URL OBS sont dans la page de configuration.

function showState(state) {
  $('status').textContent = state.running ? 'en cours' : 'arretee';
}

async function command(path) {
  await call('POST', path);
  showState(await call('GET', 'state'));
}

onFirstAuth(async () => showState(await call('GET', 'state')));

$('start').addEventListener('click', () => guarded(() => command('start')));
$('stop').addEventListener('click', () => guarded(() => command('stop')));

// Efface les votes puis rouvre la collecte : une nouvelle question, une carte vierge.
confirmable($('round'), 'Confirmer (efface les votes)', async () => {
  await call('POST', 'reset');
  await command('start');
});
