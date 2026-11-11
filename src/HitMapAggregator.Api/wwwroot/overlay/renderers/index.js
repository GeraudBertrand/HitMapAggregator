import { renderEllipses } from './ellipse.js';
import { renderContour } from './contour.js';
import { renderLabels } from './labels.js';

/**
 * Choisit le rendu et l'applique.
 *
 * Le serveur calcule toujours les deux representations et transporte `renderMode` sans
 * jamais brancher dessus : l'arbitrage a lieu ici, et nulle part ailleurs.
 *
 * `?mode=` prend le pas sur le mode transporte. C'est un outil de comparaison — il permet
 * d'ouvrir deux fenetres sur le meme flux et de juger les deux rendus cote a cote.
 */
export function render(context, canvas, snapshot, forcedMode) {
  const mode = (forcedMode ?? snapshot.renderMode ?? 'both').toLowerCase();

  if (mode === 'contour' || mode === 'both') {
    renderContour(context, canvas, snapshot);
  }

  if (mode === 'ellipse' || mode === 'both') {
    renderEllipses(context, canvas, snapshot);
  }

  renderLabels(context, canvas, snapshot);
}
