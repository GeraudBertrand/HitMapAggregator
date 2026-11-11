'use strict';

const surface = document.getElementById('surface');
const marker = document.getElementById('marker');
const notice = document.getElementById('notice');

let auth = null;
let videoAspect = 16 / 9;
let noticeTimer = 0;

Twitch.ext.onAuthorized((value) => {
  // Twitch renouvelle le JWT de lui-meme et rappelle ce callback : toujours garder le dernier.
  auth = value;
});

Twitch.ext.onContext((context) => {
  const match = /^(\d+)x(\d+)$/.exec(context.videoResolution || '');
  if (match && Number(match[2]) > 0) {
    videoAspect = Number(match[1]) / Number(match[2]);
  }
});

// L'iframe couvre tout le lecteur, mais la video y est centree avec des bandes noires des
// que les proportions different (plein ecran 21:9, mode theatre...). Les positions doivent
// etre relatives a l'image, sinon le meme point de la video donnerait des x,y differents
// selon la taille de fenetre de chaque viewer.
function videoRect() {
  const width = window.innerWidth;
  const height = window.innerHeight;

  if (width / height > videoAspect) {
    const contentWidth = height * videoAspect;
    return { left: (width - contentWidth) / 2, top: 0, width: contentWidth, height };
  }

  const contentHeight = width / videoAspect;
  return { left: 0, top: (height - contentHeight) / 2, width, height: contentHeight };
}

function showNotice(message) {
  notice.textContent = message;
  notice.style.display = 'block';
  clearTimeout(noticeTimer);
  noticeTimer = setTimeout(() => { notice.style.display = 'none'; }, 2500);
}

// Garde-fou de confort, pas de securite : un client modifie peut l'ignorer, et la
// deduplication serveur protege deja l'exactitude du vote.
const MIN_CLICK_INTERVAL_MS = 250;
let lastClick = -Infinity;

surface.addEventListener('click', async (event) => {
  if (!auth || event.timeStamp - lastClick < MIN_CLICK_INTERVAL_MS) {
    return;
  }

  lastClick = event.timeStamp;

  const rect = videoRect();
  const x = (event.clientX - rect.left) / rect.width;
  const y = (event.clientY - rect.top) / rect.height;

  // Clic dans une bande noire : il ne designe aucun point de l'image.
  if (x < 0 || x > 1 || y < 0 || y > 1) {
    return;
  }

  try {
    const response = await fetch(`${window.HITMAP_EBS_URL}/api/clicks`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${auth.token}`,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ x, y }),
    });

    if (response.status === 204) {
      // Le marqueur montre le vote retenu : un nouveau clic le deplace, comme cote serveur.
      marker.style.left = `${event.clientX}px`;
      marker.style.top = `${event.clientY}px`;
      marker.style.display = 'block';
    } else if (response.status === 409) {
      marker.style.display = 'none';
      showNotice('Le vote est ferme pour le moment');
    } else {
      showNotice('Vote refuse');
    }
  } catch {
    showNotice('Serveur injoignable');
  }
});
