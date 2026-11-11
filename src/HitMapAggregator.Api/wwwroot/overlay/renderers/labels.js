// Etiquettes et palette, partagees par les deux rendus.

const PALETTE = [
  '#58a6ff', '#ff8a60', '#7ee787', '#eebe5c',
  '#c792ff', '#60e0de', '#ff91be', '#a8d660',
];

/**
 * Couleur d'une zone deduite de son SEUL rang.
 *
 * L'overlay ne recoit aucun identifiant : il obtient une liste deja classee et colorie
 * par position. Quand le classement s'inverse, la couleur change donc de zone — c'est
 * voulu, on affiche un classement, pas des individus.
 */
export function zoneColor(rank) {
  return PALETTE[rank % PALETTE.length];
}

export function renderLabels(context, canvas, snapshot) {
  const width = canvas.width;
  const height = canvas.height;
  const scale = Math.max(1, width / 1920);
  const major = snapshot.majorZones ?? 3;

  context.textAlign = 'center';
  context.textBaseline = 'bottom';
  context.lineJoin = 'round';

  snapshot.zones.forEach((zone, index) => {
    const isMajor = index < major;
    const size = Math.round((isMajor ? 30 : 20) * scale);

    context.font = `${isMajor ? 'bold ' : ''}${size}px monospace`;

    const x = zone.cx * width;
    const y = zone.cy * height - zone.sb * 2 * width - 8 * scale;
    const text = `${zone.pct.toFixed(1)} %`;

    // Contour sombre avant le texte : lisible sur n'importe quelle image de fond.
    context.lineWidth = 5 * scale;
    context.strokeStyle = 'rgba(0, 0, 0, .8)';
    context.strokeText(text, x, y);

    context.fillStyle = isMajor ? '#ffffff' : 'rgba(255, 255, 255, .72)';
    context.fillText(text, x, y);

    if (isMajor) {
      renderRank(context, index + 1, x, zone.cy * height, zone.sb * 2 * width, scale);
    }
  });

  renderScattered(context, snapshot, width, height, scale);
}

function renderRank(context, rank, x, y, radius, scale) {
  const size = 16 * scale;
  const cx = x - radius - size;
  const cy = y - radius - size;

  context.beginPath();
  context.arc(cx, cy, size, 0, Math.PI * 2);
  context.fillStyle = zoneColor(rank - 1);
  context.fill();

  context.font = `bold ${Math.round(20 * scale)}px sans-serif`;
  context.textBaseline = 'middle';
  context.fillStyle = '#0d1117';
  context.fillText(String(rank), cx, cy + scale);
  context.textBaseline = 'bottom';
}

function renderScattered(context, snapshot, width, height, scale) {
  const size = Math.round(22 * scale);
  context.font = `${size}px monospace`;
  context.textAlign = 'left';

  const text = `${snapshot.totalVotes} clics  -  disperses ${snapshot.scatteredPct.toFixed(1)} %`;
  const x = 16 * scale;
  const y = height - 16 * scale;

  context.lineWidth = 5 * scale;
  context.strokeStyle = 'rgba(0, 0, 0, .8)';
  context.strokeText(text, x, y);
  context.fillStyle = 'rgba(255, 255, 255, .85)';
  context.fillText(text, x, y);
}
