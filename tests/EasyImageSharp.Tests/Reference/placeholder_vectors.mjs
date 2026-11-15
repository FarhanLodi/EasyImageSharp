// Regenerates the BlurHash / ThumbHash reference vectors used by PlaceholderHashTests.cs.
//
// The expected values come from the reference implementations, never from EasyImageSharp:
//   blurhash  2.0.5  (npm, MIT, https://github.com/woltapp/blurhash - the official TypeScript implementation)
//   thumbhash 0.1.1  (npm, MIT, https://github.com/evanw/thumbhash  - the official JavaScript implementation)
//
// Usage (in an empty scratch directory, not inside the repository):
//   npm install --ignore-scripts blurhash@2.0.5 thumbhash@0.1.1
//   node <path-to>/placeholder_vectors.mjs
//
// The synthetic inputs use integer arithmetic only, so PlaceholderHashTests.Pattern builds byte-identical
// pixels in C#.
//
// The TypeScript BlurHash encoder normalises the AC terms by their largest *signed* value, while the
// specification (Algorithm.md) and the reference C encoder use the largest *absolute* value. The two agree
// whenever the largest-magnitude AC term is positive; the script reports which vectors that holds for, and
// only those are used as exact expectations.
import { createHash } from 'node:crypto';
import { encode, decode } from 'blurhash';
import { rgbaToThumbHash, thumbHashToRGBA } from 'thumbhash';

const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');

// kind: 'opaque', 'alpha', or 'mirror' (the opaque pattern rotated by 180 degrees).
function pattern(w, h, kind) {
  if (kind === 'mirror') {
    const source = pattern(w, h, 'opaque');
    const rgba = new Uint8ClampedArray(w * h * 4);
    for (let i = 0; i < w * h; i++) rgba.set(source.subarray((w * h - 1 - i) * 4, (w * h - i) * 4), i * 4);
    return rgba;
  }
  const rgba = new Uint8ClampedArray(w * h * 4);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const i = (y * w + x) * 4;
      const cx = (w / 2) | 0, cy = (h / 3) | 0, rad = (Math.min(w, h) / 3) | 0;
      const inDisc = (x - cx) * (x - cx) + (y - cy) * (y - cy) < rad * rad;
      rgba[i] = Math.floor((x * 255) / Math.max(1, w - 1));
      rgba[i + 1] = inDisc ? 230 : Math.floor((y * 200) / Math.max(1, h - 1));
      rgba[i + 2] = inDisc ? 30 : (x * 7 + y * 13) % 256;
      rgba[i + 3] = kind === 'alpha' ? (inDisc ? 255 : (x * 9 + y * 5) % 256) : 255;
    }
  }
  return rgba;
}

// Mirrors the reference's own factor computation to report whether signed-max == absolute-max quantisation.
function blurhashMaxAgreement(rgba, w, h, nx, ny) {
  const lin = (v) => { const c = v / 255; return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
  let maxSigned = -Infinity, maxAbs = 0;
  for (let j = 0; j < ny; j++) {
    for (let i = 0; i < nx; i++) {
      if (i === 0 && j === 0) continue;
      const f = [0, 0, 0];
      for (let x = 0; x < w; x++) for (let y = 0; y < h; y++) {
        const b = 2 * Math.cos(Math.PI * i * x / w) * Math.cos(Math.PI * j * y / h);
        const o = 4 * x + y * w * 4;
        f[0] += b * lin(rgba[o]); f[1] += b * lin(rgba[o + 1]); f[2] += b * lin(rgba[o + 2]);
      }
      for (const v of f) { const s = v / (w * h); maxSigned = Math.max(maxSigned, s); maxAbs = Math.max(maxAbs, Math.abs(s)); }
    }
  }
  const q = (m) => Math.floor(Math.max(0, Math.min(82, Math.floor(m * 166 - 0.5))));
  return nx * ny === 1 || q(maxSigned) === q(maxAbs);
}

const blurCases = [
  [32, 24, 4, 3], [20, 30, 9, 9], [17, 13, 1, 1], [64, 40, 5, 4], [7, 50, 3, 7],
];
for (const kind of ['opaque', 'mirror']) {
  for (const [w, h, nx, ny] of blurCases) {
    const rgba = pattern(w, h, kind);
    const hash = encode(rgba, w, h, nx, ny);
    console.log(`blurhash ${kind} ${w}x${h} ${nx}x${ny} ${hash} specAgrees=${blurhashMaxAgreement(rgba, w, h, nx, ny)}`);
  }
}

for (const [hash, w, h] of [['LlMF%n00%#MwS|WCWEM{R*bbWBbH', 12, 9], ['L.Hf62}Gzhw5pHn$kok8etfCgKf%', 31, 17]]) {
  const decoded = decode(hash, w, h);
  console.log(`blurhash-decode ${hash} ${w}x${h} sha256=${sha256(decoded)} ${Buffer.from(decoded).toString('base64')}`);
}

const thumbCases = [[32, 24, 'opaque'], [24, 40, 'alpha'], [100, 75, 'opaque'], [1, 1, 'opaque'], [60, 60, 'alpha']];
for (const [w, h, kind] of thumbCases) {
  const hash = rgbaToThumbHash(w, h, pattern(w, h, kind));
  const image = thumbHashToRGBA(hash);
  console.log(`thumbhash ${w}x${h} ${kind} ${Buffer.from(hash).toString('hex')} decoded ${image.w}x${image.h} sha256=${sha256(image.rgba)} ${Buffer.from(image.rgba).toString('base64')}`);
}
