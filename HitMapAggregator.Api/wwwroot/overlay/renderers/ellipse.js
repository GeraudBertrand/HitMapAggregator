import { zoneColor } from './labels.js';

/** Facteur applique aux ecarts-types pour tracer l'ellipse. */
const SIGMA_SCALE = 2;

export function renderEllipses(context, canvas, snapshot) {
  const width = canvas.width;
  const height = canvas.height;
  const scale = Math.max(1, width / 1920);
  const major = snapshot.majorZones ?? 3;

  // On dessine du dernier au premier pour que la zone majeure passe au-dessus.
  for (let index = snapshot.zones.length - 1; index >= 0; index--) {
    const zone = snapshot.zones[index];
    const isMajor = index < major;

    const cx = zone.cx * width;
    const cy = zone.cy * height;

    // Les DEUX rayons passent par la LARGEUR : les sigmas sont dans la meme echelle
    // isotrope, celle de l'espace corrige de l'aspect. Passer `sb` par la hauteur
    // deformerait toute ellipse pivotee.
    const rx = Math.max(2, zone.sa * SIGMA_SCALE * width);
    const ry = Math.max(2, zone.sb * SIGMA_SCALE * width);

    context.beginPath();

    // L'angle vient d'un repere ou Y descend, comme le canvas : il s'applique tel quel.
    context.ellipse(cx, cy, rx, ry, zone.angle, 0, Math.PI * 2);

    context.fillStyle = withAlpha(zoneColor(index), isMajor ? 0.16 : 0.07);
    context.fill();

    context.lineWidth = (isMajor ? 4 : 2) * scale;
    context.strokeStyle = withAlpha(zoneColor(index), isMajor ? 1 : 0.45);
    context.stroke();

    context.beginPath();
    context.arc(cx, cy, (isMajor ? 5 : 3) * scale, 0, Math.PI * 2);
    context.fillStyle = withAlpha(zoneColor(index), isMajor ? 1 : 0.5);
    context.fill();
  }
}

function withAlpha(hex, alpha) {
  const value = parseInt(hex.slice(1), 16);
  const r = (value >> 16) & 255;
  const g = (value >> 8) & 255;
  const b = value & 255;
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}
