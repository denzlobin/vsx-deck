// VSX Deck: Stream Deck keys (plain Node 20, no packages; Stream Deck's WebSocket is spoken by the tiny client below).
// They write the deck state in %LOCALAPPDATA%\VSX Deck\, which every VSX Deck Holder instance applies to its VSX.
//  - On/Off : shows what the LIVE VSX applied (status.txt) and toggles: ON = last chosen slot, OFF = dry.
//  - Preset N : shows preset N's room (picture if VSX Deck made one), green while active. Press = load it
//               (an empty preset becomes the Save target). Hold 1.5 s = clear it (recoverable).
//               A newly added key takes the next free number (highest in use + 1), empty.
//  - Save   : stores the open VSX window's settings into the selected preset (save.req).
//  - Bypass : VSX's own level-matched bypass (bypass.txt).
//  - App    : starts VSX Deck, or brings its window to the front if it is running (the app handles that).
// Run without Stream Deck args (`node plugin.js`) to execute the self-check.
'use strict';
const fs = require('fs'), path = require('path'), net = require('net'), crypto = require('crypto'), cp = require('child_process');

const DIR = process.env.VSX_DIR || path.join(process.env.LOCALAPPDATA || '', 'VSX Deck');   // per user
const APP = path.join(DIR, 'VSXDeck.exe');
const f = (n) => path.join(DIR, n);
const read = (n) => { try { return fs.readFileSync(f(n)); } catch { return null; } };
const mtime = (n) => { try { return fs.statSync(f(n)).mtimeMs; } catch { return 0; } };

// ---- state ------------------------------------------------------------------------------------
const isChunk = (b) => !!(b && b.length > 8 && b.toString('latin1', 0, 4) === 'VC2!');
const roomOf = (b) => (isChunk(b) && (b.toString('utf8').match(/m_currentStudioName="([^"]*)"/) || [])[1]) || '';
function wanted() { return isChunk(read('active.vsx')); }
function lastSlot() { return parseInt((read('lastslot.txt') || '1').toString(), 10) || 1; }
function applied() { // { on, room, bypass } as reported by the live instance, or null if it never reported
  const s = read('status.txt'); if (!s) return null;
  const [t, b = ''] = s.toString('utf8').trim().split(/\r?\n/);
  return t.startsWith('on') ? { on: true, room: t.slice(2).trim(), bypass: b.trim() === 'bypass 1' } : { on: false, room: '', bypass: false };
}
function bypassWanted() { const b = read('bypass.txt'); return !!b && b.toString().trim() === '1'; }
function toggleBypass() { fs.writeFileSync(f('bypass.txt'), bypassWanted() ? '0' : '1'); }
function writeActive(buf) {
  const tmp = f('active.tmp');
  fs.writeFileSync(tmp, buf || Buffer.alloc(0));     // fresh write = fresh timestamp; watchers key on it
  fs.renameSync(tmp, f('active.vsx'));               // atomic replace: the plugin never sees half a file
}
function toggle() {
  writeActive(wanted() ? null : read(`slot${lastSlot()}.vsx`) || read('slot1.vsx'));
}
function selectSlot(n) {                             // an empty slot only becomes the Save target; audio stays
  const preset = read(`slot${n}.vsx`);
  if (isChunk(preset)) writeActive(preset);
  fs.writeFileSync(f('lastslot.txt'), String(n));
  return isChunk(preset) ? 'loaded' : 'empty';
}
function openApp() {   // a second VSXDeck.exe only tells the running one to show its window
  if (!fs.existsSync(APP)) return false;
  cp.spawn(APP, [], { detached: true, stdio: 'ignore' }).unref();
  return true;
}
function requestSave() { const n = lastSlot(); fs.writeFileSync(f('save.req'), String(n)); return n; }
// Number for a newly added preset key: one past the highest used by a key or a saved preset.
const MAX_PRESET = 99;
function nextPreset(keyNumbers) {
  let files = [];
  try { files = fs.readdirSync(DIR).map((n) => +((n.match(/^slot(\d+)\.vsx$/) || [])[1] || 0)); } catch {}
  return Math.min(MAX_PRESET, Math.max(0, ...keyNumbers, ...files) + 1);
}
// Hold a slot key to clear it. Recoverable: the preset moves to cleared\slotN-<time>.vsx. Audio keeps playing
// whatever is active; the cleared slot becomes the Save target.
function clearSlot(n) {
  if (!isChunk(read(`slot${n}.vsx`))) return false;
  const stamp = new Date().toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
  fs.mkdirSync(f('cleared'), { recursive: true });
  fs.renameSync(f(`slot${n}.vsx`), path.join(DIR, 'cleared', `slot${n}-${stamp}.vsx`));
  for (const png of [`slot${n}.png`, `slot${n}-active.png`]) { try { fs.unlinkSync(f(png)); } catch {} }
  fs.writeFileSync(f('lastslot.txt'), String(n));
  return true;
}

// ---- key images ----------------------------------------------------------------------------------
const esc = (s) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
function twoLines(text, width) {
  const l1 = [], l2 = [];
  for (const w of (text || '').split(' ')) ((l1.join(' ') + ' ' + w).length <= width && !l2.length ? l1 : l2).push(w);
  return [l1.join(' '), l2.join(' ').slice(0, width + 2)];
}
// Palette ("B · Deck", green): charcoal keys, green = on/active, purple = bypassed.
const C = { tile: '#17181c', key: '#1f2024', dim: '#8e8f96', light: '#f4f3f1', green: '#35d07f', track: '#3d4049',
            purple: '#4a3b8c', save: '#2b2c31', off: '#1b1c20', faint: '#55565c' };
function key({ bg, fg, top = '', bar = '', big, bigSize = 38, sub = '', dashed = false, outline = '' }) {
  const [a, b] = twoLines(sub, 12);
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144">
<rect width="144" height="144" rx="18" fill="${bg}"/>
${dashed ? '<rect x="6" y="6" width="132" height="132" rx="14" fill="none" stroke="#f0b429" stroke-width="5" stroke-dasharray="12 8"/>' : ''}
${outline ? `<rect x="5" y="5" width="134" height="134" rx="15" fill="none" stroke="${outline}" stroke-width="6"/>` : ''}
${bar ? `<rect x="44" y="26" width="56" height="12" rx="6" fill="${bar}"/>` : ''}
<text x="72" y="44" font-family="Segoe UI,Arial" font-size="24" font-weight="600" fill="${fg}" text-anchor="middle">${esc(top)}</text>
<text x="72" y="86" font-family="Segoe UI,Arial" font-size="${bigSize}" font-weight="700" fill="${fg}" text-anchor="middle">${esc(big)}</text>
<text x="72" y="112" font-family="Segoe UI,Arial" font-size="15" fill="${fg}" text-anchor="middle">${esc(a)}</text>
<text x="72" y="130" font-family="Segoe UI,Arial" font-size="15" fill="${fg}" text-anchor="middle">${esc(b)}</text>
</svg>`;
  return 'data:image/svg+xml;base64,' + Buffer.from(svg).toString('base64');
}
// On/Off: the live instance's report when it matches the request; otherwise the request, outlined in
// amber (VSX Deck not running, so nothing has picked it up yet).
function view() {
  const w = wanted(), a = applied();
  const confirmed = !!a && a.on === w;
  return { on: w, room: confirmed ? a.room : '', confirmed };
}
function onOffImage() {
  const v = view(), a = applied();
  const sub = v.confirmed && a && a.bypass ? 'BYPASSED' : v.room;
  return v.on
    ? key({ bg: C.tile, fg: C.light, bar: C.green, outline: v.confirmed ? C.green : '', big: 'ON', sub, dashed: !v.confirmed })
    : key({ bg: C.key, fg: C.dim, bar: C.track, big: 'OFF', sub: 'dry', dashed: !v.confirmed });
}
// Bypass = VSX's own level-matched bypass (VSX keeps running). Purple when bypassed; dashed until the live
// instance reports the same state; dim while VSX is OFF (nothing to bypass).
function bypassImage() {
  const w = bypassWanted(), a = applied(), on = wanted();
  if (!on) return key({ bg: C.off, fg: C.faint, top: 'BYPASS', big: w ? 'ON' : 'OFF', bigSize: 32, sub: 'VSX is off' });
  const confirmed = !!a && a.on && a.bypass === w;
  return key({ bg: w ? C.purple : C.save, fg: w ? '#f1edff' : '#cfcfcf', top: 'BYPASS', big: w ? 'ON' : 'OFF', bigSize: 32,
               sub: 'level matched', dashed: !confirmed });
}
// Room picture keys: VSX Deck bakes slotN.png / slotN-active.png from the user's own VSX window (local only).
// Used while newer than the preset; otherwise the plain key.
function pictureKey(n, active) {
  const png = `slot${n}${active ? '-active' : ''}.png`;
  if (mtime(png) <= mtime(`slot${n}.vsx`)) return null;
  const b = read(png);
  return b ? 'data:image/png;base64,' + b.toString('base64') : null;
}
function slotImage(n) {
  const b = read(`slot${n}.vsx`), room = roomOf(b), active = wanted() && lastSlot() === n && !!room;
  const pic = room && pictureKey(n, active);
  if (pic) return pic;
  if (!room) return lastSlot() === n
    ? key({ bg: C.key, fg: C.light, top: 'PRESET', big: String(n), sub: 'save here', outline: C.dim })
    : key({ bg: C.off, fg: C.faint, top: 'PRESET', big: String(n), sub: 'empty' });
  return active
    ? key({ bg: C.tile, fg: C.light, top: 'PRESET', big: String(n), sub: room, outline: C.green })
    : key({ bg: C.key, fg: '#cfcfcf', top: 'PRESET', big: String(n), sub: room });
}
function saveImage() {
  const n = lastSlot(), room = roomOf(read(`slot${n}.vsx`));
  return key({ bg: C.save, fg: C.light, top: 'SAVE', big: `→ ${n}`, bigSize: 34, sub: room || 'empty preset' });
}

// ---- minimal WebSocket client (text frames, client-masked) -------------------------------------
function ws(port, onOpen, onText) {
  const sock = net.connect(+port, '127.0.0.1');
  let buf = Buffer.alloc(0), open = false;
  const send = (text, op = 1) => {
    const data = Buffer.from(text), mask = crypto.randomBytes(4), n = data.length;
    const head = n < 126 ? Buffer.from([0x80 | op, 0x80 | n])
      : n < 65536 ? Buffer.from([0x80 | op, 0x80 | 126, n >> 8, n & 255])
      : Buffer.concat([Buffer.from([0x80 | op, 0x80 | 127]), (() => { const b = Buffer.alloc(8); b.writeBigUInt64BE(BigInt(n)); return b; })()]);
    for (let i = 0; i < n; i++) data[i] ^= mask[i & 3];
    sock.write(Buffer.concat([head, mask, data]));
  };
  sock.on('connect', () => sock.write(`GET / HTTP/1.1\r\nHost: 127.0.0.1:${port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
    `Sec-WebSocket-Key: ${crypto.randomBytes(16).toString('base64')}\r\nSec-WebSocket-Version: 13\r\n\r\n`));
  sock.on('data', (chunk) => {
    buf = Buffer.concat([buf, chunk]);
    if (!open) {
      const end = buf.indexOf('\r\n\r\n'); if (end < 0) return;
      buf = buf.subarray(end + 4); open = true; onOpen(send);
    }
    for (;;) {
      if (buf.length < 2) return;
      const op = buf[0] & 15; let n = buf[1] & 127, p = 2;
      if (n === 126) { if (buf.length < 4) return; n = buf.readUInt16BE(2); p = 4; }
      else if (n === 127) { if (buf.length < 10) return; n = Number(buf.readBigUInt64BE(2)); p = 10; }
      if (buf.length < p + n) return;
      const payload = buf.subarray(p, p + n); buf = buf.subarray(p + n);
      if (op === 1) onText(payload.toString('utf8'));
      else if (op === 9) send(payload.toString('latin1'), 10);   // ping -> pong
      else if (op === 8) process.exit(0);                         // Stream Deck closed us
    }
  });
  sock.on('close', () => process.exit(0));
  sock.on('error', () => process.exit(1));
}

// ---- plugin -------------------------------------------------------------------------------------
const ACT = 'com.vsxdeck.';
const HOLD_MS = 1500;   // hold a slot key this long to clear it
function main() {
  const arg = (k) => process.argv[process.argv.indexOf(k) + 1];
  const keys = new Map(); // context -> { action, slot, last }
  let sendJson = () => {};
  const image = (k) => k.action === ACT + 'onoff' ? onOffImage() : k.action === ACT + 'save' ? saveImage()
    : k.action === ACT + 'bypass' ? bypassImage() : k.action === ACT + 'app' ? null : slotImage(k.slot);
  const paint = (force) => {
    for (const [context, k] of keys) {
      const img = image(k);                            // null: keep the manifest's image (the app key)
      if (!img || (!force && img === k.last)) continue;
      k.last = img;
      sendJson({ event: 'setImage', context, payload: { image: img, target: 0 } });
    }
  };
  const confirmSave = (context, n, before) => {           // ✓ once the slot file is rewritten, ⚠ otherwise
    const t0 = Date.now();
    const tick = () => {
      if (mtime(`slot${n}.vsx`) > before) return sendJson({ event: 'showOk', context });
      if (Date.now() - t0 > 1500) return sendJson({ event: 'showAlert', context });
      setTimeout(tick, 100);
    };
    tick();
  };
  ws(arg('-port'), (send) => {
    sendJson = (o) => send(JSON.stringify(o));
    sendJson({ event: arg('-registerEvent'), uuid: arg('-pluginUUID') });
    setInterval(() => paint(false), 250);
  }, (text) => {
    const m = JSON.parse(text);
    if (m.event === 'willAppear') {
      const s = (m.payload && m.payload.settings) || {};
      let slot = parseInt(s.slot, 10);
      if (m.action === ACT + 'slot' && !(slot >= 1)) {      // freshly added preset key: next number, stored on it
        slot = nextPreset([...keys.values()].filter((k) => k.action === ACT + 'slot').map((k) => k.slot));
        sendJson({ event: 'setSettings', context: m.context, payload: { slot } });
      }
      keys.set(m.context, { action: m.action, slot: Math.min(MAX_PRESET, Math.max(1, slot || 1)), last: '' });
      paint(true);
    } else if (m.event === 'willDisappear') keys.delete(m.context);
    else if (m.event === 'keyDown') {
      const k = keys.get(m.context); if (!k) return;
      try {
        if (k.action === ACT + 'onoff') toggle();
        else if (k.action === ACT + 'bypass') toggleBypass();
        else if (k.action === ACT + 'app') { if (!openApp()) sendJson({ event: 'showAlert', context: m.context }); }
        else if (k.action === ACT + 'save') { const n = lastSlot(), before = mtime(`slot${n}.vsx`); requestSave(); confirmSave(m.context, n, before); }
        else {                                   // slot: short press selects (on release), hold clears
          k.held = false;
          k.hold = setTimeout(() => {
            k.held = true;
            try { sendJson({ event: clearSlot(k.slot) ? 'showOk' : 'showAlert', context: m.context }); }
            catch { sendJson({ event: 'showAlert', context: m.context }); }
            paint(true);
          }, HOLD_MS);
          return;
        }
      } catch { sendJson({ event: 'showAlert', context: m.context }); }
      setTimeout(() => paint(true), 150);
    } else if (m.event === 'keyUp') {
      const k = keys.get(m.context); if (!k || k.action !== ACT + 'slot') return;
      clearTimeout(k.hold);
      if (k.held) return;
      try { selectSlot(k.slot); } catch { sendJson({ event: 'showAlert', context: m.context }); }
      setTimeout(() => paint(true), 150);
    }
  });
}

// ---- self-check (node plugin.js) ----------------------------------------------------------------
function selfTest() {
  const assert = require('assert'), os = require('os');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'vsxkey-'));
  process.env.VSX_DIR = dir;
  delete require.cache[require.resolve(__filename)];
  const m = require(__filename);
  const chunk = (room) => Buffer.from(`VC2!\0\0\0\0<VSXver1 m_currentStudioName="${room}"/>`);
  fs.writeFileSync(path.join(dir, 'slot1.vsx'), chunk('HUMAN LINEAR'));
  fs.writeFileSync(path.join(dir, 'slot4.vsx'), chunk('CELLPHONE'));
  assert.strictEqual(m.wanted(), false, 'no active.vsx = off');
  m.toggle(); assert.strictEqual(m.wanted(), true, 'toggle from off = on');
  assert.ok(fs.readFileSync(path.join(dir, 'active.vsx')).includes('HUMAN LINEAR'), 'default slot 1');
  m.toggle(); assert.strictEqual(m.wanted(), false, 'toggle from on = off');
  assert.strictEqual(m.selectSlot(4), 'loaded', 'select slot 4');
  assert.strictEqual(m.lastSlot(), 4); assert.ok(fs.readFileSync(path.join(dir, 'active.vsx')).includes('CELLPHONE'));
  m.toggle(); m.toggle(); assert.ok(fs.readFileSync(path.join(dir, 'active.vsx')).includes('CELLPHONE'), 'on returns to last slot');
  assert.strictEqual(m.selectSlot(3), 'empty', 'empty slot becomes the save target');
  assert.strictEqual(m.lastSlot(), 3, 'save target = 3');
  assert.ok(fs.readFileSync(path.join(dir, 'active.vsx')).includes('CELLPHONE'), 'audio unchanged by an empty slot');
  assert.ok(Buffer.from(m.slotImage(3).split(',')[1], 'base64').toString().includes('save here'), 'empty target is marked');
  assert.strictEqual(m.requestSave(), 3, 'save goes to the empty target');
  m.toggle(); m.toggle(); assert.ok(fs.readFileSync(path.join(dir, 'active.vsx')).includes('HUMAN LINEAR'), 'ON with empty target falls back to slot 1');
  m.selectSlot(4);
  assert.deepStrictEqual(m.view(), { on: true, room: '', confirmed: false }, 'unconfirmed until the live instance reports');
  fs.writeFileSync(path.join(dir, 'status.txt'), 'on CELLPHONE');
  assert.deepStrictEqual(m.view(), { on: true, room: 'CELLPHONE', confirmed: true }, 'confirmed by status');
  assert.strictEqual(m.requestSave(), 4); assert.strictEqual(fs.readFileSync(path.join(dir, 'save.req'), 'utf8'), '4', 'save asks for active slot');
  assert.strictEqual(m.roomOf(fs.readFileSync(path.join(dir, 'slot1.vsx'))), 'HUMAN LINEAR');
  assert.strictEqual(m.bypassWanted(), false, 'no bypass.txt = not bypassed');
  m.toggleBypass(); assert.strictEqual(m.bypassWanted(), true, 'bypass toggles on');
  fs.writeFileSync(path.join(dir, 'status.txt'), 'on CELLPHONE\nbypass 1');
  assert.deepStrictEqual(m.applied(), { on: true, room: 'CELLPHONE', bypass: true }, 'status second line = bypass');
  assert.ok(Buffer.from(m.onOffImage().split(',')[1], 'base64').toString().includes('BYPASSED'), 'ON key says bypassed');
  m.toggleBypass(); assert.strictEqual(m.bypassWanted(), false, 'bypass toggles off');
  assert.strictEqual(m.selectSlot(10), 'empty', 'slot 10 works'); assert.strictEqual(m.lastSlot(), 10);
  assert.strictEqual(m.nextPreset([1, 2, 3, 4, 5, 6, 7]), 8, 'with 7 keys, the new one is 8');
  assert.strictEqual(m.nextPreset([]), 5, 'no keys: one past the highest saved preset (slot4.vsx)');
  assert.strictEqual(m.nextPreset([1, 2, 12]), 13, 'gaps are not refilled: highest + 1');
  assert.strictEqual(m.nextPreset([99]), 99, 'capped at 99');
  assert.strictEqual(m.openApp(), false, 'app key: no VSXDeck.exe -> false (key shows an alert)');
  for (const img of [m.onOffImage(), m.slotImage(1), m.slotImage(3), m.saveImage(), m.bypassImage()]) assert.ok(img.startsWith('data:image/svg+xml;base64,'));
  const later = new Date(Date.now() + 5000);
  fs.writeFileSync(path.join(dir, 'slot1.png'), 'PNG1'); fs.utimesSync(path.join(dir, 'slot1.png'), later, later);
  fs.writeFileSync(path.join(dir, 'slot1-active.png'), 'PNG1A'); fs.utimesSync(path.join(dir, 'slot1-active.png'), later, later);
  m.selectSlot(4);
  assert.ok(m.slotImage(1).startsWith('data:image/png;base64,'), 'room picture used when newer than the preset');
  m.selectSlot(1);
  assert.strictEqual(Buffer.from(m.slotImage(1).split(',')[1], 'base64').toString(), 'PNG1A', 'active picture for the active slot');
  const older = new Date(Date.now() - 60000);
  fs.utimesSync(path.join(dir, 'slot1.png'), older, older); fs.utimesSync(path.join(dir, 'slot1-active.png'), older, older);
  assert.ok(m.slotImage(1).startsWith('data:image/svg+xml'), 'stale picture (preset re-saved) falls back to plain key');
  const activeBefore = fs.readFileSync(path.join(dir, 'active.vsx'));
  assert.strictEqual(m.clearSlot(1), true, 'clear slot 1');
  assert.ok(!fs.existsSync(path.join(dir, 'slot1.vsx')) && !fs.existsSync(path.join(dir, 'slot1.png')), 'slot and pictures gone');
  assert.strictEqual(fs.readdirSync(path.join(dir, 'cleared')).filter((n) => n.startsWith('slot1-')).length, 1, 'kept in cleared\\');
  assert.ok(fs.readFileSync(path.join(dir, 'active.vsx')).equals(activeBefore), 'audio unchanged');
  assert.strictEqual(m.lastSlot(), 1, 'cleared slot is the save target');
  assert.strictEqual(m.clearSlot(1), false, 'clearing an empty slot does nothing');
  fs.rmSync(dir, { recursive: true });
  console.log('self-check OK');
}

module.exports = { wanted, applied, toggle, selectSlot, requestSave, lastSlot, roomOf, view, onOffImage, slotImage, saveImage,
  bypassWanted, toggleBypass, bypassImage, clearSlot, nextPreset, openApp };
if (require.main === module) process.argv.includes('-port') ? main() : selfTest();
