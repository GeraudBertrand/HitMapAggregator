'use strict';

// Commun aux deux pages du diffuseur : configuration (hors live) et panneau live.

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

/** Appelle `ready` une seule fois, au premier JWT ; les suivants ne font que le renouveler. */
function onFirstAuth(ready) {
  Twitch.ext.onAuthorized((value) => {
    const first = auth === null;
    auth = value;
    if (first) {
      guarded(ready);
    }
  });
}

/**
 * Bouton destructif a confirmer d'un second clic.
 *
 * Plutot que confirm() : l'iframe Twitch est sandboxee et peut bloquer les boites
 * modales, ce qui rendrait le bouton muet.
 */
function confirmable(button, confirmLabel, action) {
  const label = button.textContent;
  let armed = 0;

  button.addEventListener('click', () => guarded(async () => {
    if (!armed) {
      button.textContent = confirmLabel;
      armed = setTimeout(() => {
        armed = 0;
        button.textContent = label;
      }, 4000);
      return;
    }

    clearTimeout(armed);
    armed = 0;
    button.textContent = label;
    await action();
  }));
}
