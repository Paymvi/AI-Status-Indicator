// Draws the static worker images in src\ from the creature art in phone.js, so they can't become a second design.
//   node tests/worker-icons.mjs           write them
//   node tests/worker-icons.mjs --check   exit 1 if a file's pixels differ from the art (run-tests.ps1 does this)
// Files: favicon.ico, worker-192.png, apple-touch-icon.png (the default worker), and workers.png, the sheet
// phone.cs crops notification images from: every look in every pose, one 16x16 cell each.
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { createRequire } from 'node:module';

const src = path.join(path.dirname(new URL(import.meta.url).pathname.replace(/^\/(\w:)/, '$1')), '..', 'src');
const art = createRequire(import.meta.url)(path.join(src, 'phone.js'));
const HATS = art.HATS.length, BODIES = art.BODIES.length, DEFAULT_LOOK = 0;   // look 0: phone.js's fallback and the empty board's creature

// The still pose of each status: phone.css's layers for it, one frame of any animation. Order = rows of the
// sheet (WorkerArt.Poses in phone.cs). No 'shadow': it is a dark bar meant for the dashboard's own background.
const BASE = ['legs-a', 'torso', 'arm-l', 'arm-r', 'e-open', 'hat'];
const without = (...x) => BASE.filter((l) => !x.includes(l));
const POSES = [
  BASE,                                                                  // default (WORKING) pose
  [...without('arm-r'), 'arm-up', 'fx-bang'],                            // NEEDS_YOU
  [...without('e-open'), 'e-happy', 'm-smile', 'blush'],                 // DONE
  [...without('e-open'), 'e-worry', 'm-frown', 'fx-sweat'],              // ERROR
];

function squares(layers, pose, out = []) {
  for (const [name, items] of layers) {
    if (Array.isArray(items[0]) && typeof items[0][0] === 'string') squares(items, pose, out);
    else if (pose.includes(name)) out.push(...items);
  }
  return out;
}
const rgba = (hex) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16)).concat(255);

// RGBA buffer {w, h, px}; a worker's cell drawn at x, y with square size s
const canvas = (w, h, fill) => {
  const px = Buffer.alloc(w * h * 4);
  if (fill) for (let i = 0; i < w * h; i++) px.set(rgba(fill), i * 4);
  return { w, h, px };
};
function draw(c, look, pose, x, y, s) {
  for (const [sx, sy, color] of squares(art.creatureLayers(look), POSES[pose]))
    for (let j = 0; j < s; j++) for (let i = 0; i < s; i++) c.px.set(rgba(color), ((y + sy * s + j) * c.w + x + sx * s + i) * 4);
  return c;
}
const worker = (size, fill, margin = 0) => draw(canvas(size, size, fill), DEFAULT_LOOK, 0, margin, margin, (size - 2 * margin) / 16);

function rows(c) {
  const raw = Buffer.alloc((c.w * 4 + 1) * c.h);
  for (let y = 0; y < c.h; y++) c.px.copy(raw, y * (c.w * 4 + 1) + 1, y * c.w * 4, (y + 1) * c.w * 4);
  return raw;
}
function png(c) {
  const chunk = (type, data) => {
    const body = Buffer.concat([Buffer.from(type), data]), len = Buffer.alloc(4), crc = Buffer.alloc(4);
    len.writeUInt32BE(data.length); crc.writeUInt32BE(zlib.crc32(body));
    return Buffer.concat([len, body, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(c.w, 0); ihdr.writeUInt32BE(c.h, 4); ihdr.set([8, 6, 0, 0, 0], 8);
  return Buffer.concat([Buffer.from('89504e470d0a1a0a', 'hex'), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(rows(c), { level: 9 })), chunk('IEND', Buffer.alloc(0))]);
}
function ico(images) {   // PNG entries, which every browser that reads .ico understands
  const head = Buffer.alloc(6 + 16 * images.length), data = images.map(png);
  head.writeUInt16LE(1, 2); head.writeUInt16LE(images.length, 4);
  let at = head.length;
  images.forEach((c, i) => {
    const e = 6 + 16 * i;
    head[e] = c.w; head[e + 1] = c.h; head.writeUInt16LE(1, e + 4); head.writeUInt16LE(32, e + 6);
    head.writeUInt32LE(data[i].length, e + 8); head.writeUInt32LE(at, e + 12);
    at += data[i].length;
  });
  return Buffer.concat([head, ...data]);
}

const sheet = canvas(HATS * 16, BODIES * POSES.length * 16);
POSES.forEach((_, p) => { for (let l = 0; l < HATS * BODIES; l++) draw(sheet, l, p, (l % HATS) * 16, (p * BODIES + Math.floor(l / HATS)) * 16, 1); });
const favicons = [16, 32, 48].map((s) => worker(s));
const pngs = { 'workers.png': sheet, 'worker-192.png': worker(192), 'apple-touch-icon.png': worker(180, '#141414', 10) };   // iOS paints transparency black

if (process.argv[2] === '--check') {
  // Pixels, not bytes: a different zlib build compresses the same image differently.
  const pixels = (b) => { const parts = []; for (let o = 8; o < b.length;) { const n = b.readUInt32BE(o); if (b.toString('latin1', o + 4, o + 8) === 'IDAT') parts.push(b.subarray(o + 8, o + 8 + n)); o += 12 + n; } return zlib.inflateSync(Buffer.concat(parts)); };
  const stale = Object.entries(pngs).filter(([f, c]) => !pixels(fs.readFileSync(path.join(src, f))).equals(rows(c))).map(([f]) => f);
  const icoFile = fs.readFileSync(path.join(src, 'favicon.ico'));
  favicons.forEach((c, i) => { const e = 6 + 16 * i; if (!pixels(icoFile.subarray(icoFile.readUInt32LE(e + 12), icoFile.readUInt32LE(e + 12) + icoFile.readUInt32LE(e + 8))).equals(rows(c))) stale.push(`favicon.ico ${c.w}px`); });
  if (stale.length) { console.log('out of date: ' + stale.join(', ')); process.exit(1); }
  console.log('ok');
} else {
  for (const [f, c] of Object.entries(pngs)) fs.writeFileSync(path.join(src, f), png(c));
  fs.writeFileSync(path.join(src, 'favicon.ico'), ico(favicons));
}
