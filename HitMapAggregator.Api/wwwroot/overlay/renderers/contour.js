// Bandes de niveau obtenues par interpolation bilineaire du champ.
//
// Pas de marching squares : echantillonner le champ interpole en chaque pixel de la
// surface de travail donne deja des bords lisses, pour une fraction du code. La frontiere
// entre deux bandes est exacte a la resolution de cette surface, pas a celle de la grille.

const WORK_WIDTH = 960;
const WORK_HEIGHT = 540;

/** En deca, la cellule est consideree vide et reste transparente. */
const FLOOR = 0.04;

let work = null;
let workContext = null;
let image = null;

export function renderContour(context, canvas, snapshot) {
  const { w, h } = snapshot.grid;
  const cells = snapshot.cells;
  const bands = Math.max(1, snapshot.bandCount ?? 6);

  ensureSurface();

  const pixels = image.data;
  let offset = 0;

  for (let y = 0; y < WORK_HEIGHT; y++) {
    const v = (y + 0.5) / WORK_HEIGHT;

    for (let x = 0; x < WORK_WIDTH; x++) {
      const u = (x + 0.5) / WORK_WIDTH;
      const value = sampleBilinear(cells, w, h, u, v);

      if (value < FLOOR) {
        pixels[offset + 3] = 0;
        offset += 4;
        continue;
      }

      // Quantification en bandes : c'est elle qui cree les paliers visibles.
      const band = Math.min(bands - 1, Math.floor(value * bands));
      const [r, g, b] = ramp(band / Math.max(1, bands - 1));

      pixels[offset] = r;
      pixels[offset + 1] = g;
      pixels[offset + 2] = b;
      pixels[offset + 3] = Math.round(60 + 150 * (band / Math.max(1, bands - 1)));
      offset += 4;
    }
  }

  workContext.putImageData(image, 0, 0);

  // Sans lissage a l'agrandissement : on veut des paliers nets, pas un degrade.
  const smoothing = context.imageSmoothingEnabled;
  context.imageSmoothingEnabled = false;
  context.drawImage(work, 0, 0, canvas.width, canvas.height);
  context.imageSmoothingEnabled = smoothing;
}

function ensureSurface() {
  if (work) {
    return;
  }

  work = document.createElement('canvas');
  work.width = WORK_WIDTH;
  work.height = WORK_HEIGHT;
  workContext = work.getContext('2d');
  image = workContext.createImageData(WORK_WIDTH, WORK_HEIGHT);
}

/**
 * Interpolation bilineaire entre centres de cellules, avec prolongement des bords.
 * Reproduit exactement `DensityField.SampleBilinear` cote serveur.
 */
function sampleBilinear(cells, width, height, u, v) {
  const gx = u * width - 0.5;
  const gy = v * height - 0.5;

  const x0 = Math.floor(gx);
  const y0 = Math.floor(gy);
  const fx = gx - x0;
  const fy = gy - y0;

  const xa = clamp(x0, 0, width - 1);
  const xb = clamp(x0 + 1, 0, width - 1);
  const ya = clamp(y0, 0, height - 1);
  const yb = clamp(y0 + 1, 0, height - 1);

  const rowA = ya * width;
  const rowB = yb * width;

  const top = cells[rowA + xa] + (cells[rowA + xb] - cells[rowA + xa]) * fx;
  const bottom = cells[rowB + xa] + (cells[rowB + xb] - cells[rowB + xa]) * fx;

  return top + (bottom - top) * fy;
}

function clamp(value, low, high) {
  return value < low ? low : value > high ? high : value;
}

function ramp(t) {
  if (t < 0.25) return lerp([30, 60, 160], [0, 170, 200], t / 0.25);
  if (t < 0.50) return lerp([0, 170, 200], [40, 190, 90], (t - 0.25) / 0.25);
  if (t < 0.75) return lerp([40, 190, 90], [230, 200, 40], (t - 0.50) / 0.25);
  return lerp([230, 200, 40], [220, 50, 40], (t - 0.75) / 0.25);
}

function lerp(a, b, t) {
  return [
    Math.round(a[0] + (b[0] - a[0]) * t),
    Math.round(a[1] + (b[1] - a[1]) * t),
    Math.round(a[2] + (b[2] - a[2]) * t),
  ];
}
