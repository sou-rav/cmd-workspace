// Terminal page for CMD Workspace. Talks to C# with JSON messages. One xterm.js terminal per session.
// Terminal text is untrusted: links are only REPORTED to C#, which checks them and opens the browser.
const post = (o) => window.chrome.webview.postMessage(JSON.stringify(o));
const hostEl = document.getElementById('host');
const copyMenu = document.getElementById('copyMenu');
const pasteMenu = document.getElementById('pasteMenu');
const terms = new Map();   // id -> { term, fit, el }
let active = null;
let pasteOffer = null;     // { id } while the paste menu is shown
let copyMenuOn = true;
let pasteMenuOn = true;

// Settings from C#. Defaults only until the first "cfg" message arrives.
const cfg = {
  fontSize: 13, fontWeight: 400, fontWeightBold: 700, copyOnSelect: false, rightClick: 'Pastes', lineHeight: 1.22, cursorStyle: 'block', cursorBlink: true, scrollback: 5000,
  theme: { background: '#ffffff', foreground: '#1d1d21' }
};
const FONT = "'JetBrains Mono', 'Cascadia Mono', Consolas, monospace";
cfg.fontFamily = FONT;

function loadFont(name) {
  if (!name || !(document.fonts && document.fonts.load)) return Promise.resolve();
  return Promise.race([document.fonts.load("13px '" + name + "'"), new Promise((r) => setTimeout(r, 1500))]).catch(() => {});
}

function themeFrom(m) {
  const a = m.ansi;
  return {
    background: m.bg, foreground: m.fg, cursor: m.cursor, cursorAccent: m.bg, selectionBackground: m.sel,
    black: a[0], red: a[1], green: a[2], yellow: a[3], blue: a[4], magenta: a[5], cyan: a[6], white: a[7],
    brightBlack: a[8], brightRed: a[9], brightGreen: a[10], brightYellow: a[11], brightBlue: a[12], brightMagenta: a[13], brightCyan: a[14], brightWhite: a[15]
  };
}

// Floating scroll bar (Kero look). Shows when the user scrolls (wheel, keys, dragging) or points at it, fades out after 1 s idle.
// New output moving the view does NOT show it. Hidden when there is nothing to scroll.
function attachScrollbar(el, term) {
  const bar = document.createElement('div'); bar.className = 'sbar';
  const thumb = document.createElement('div'); thumb.className = 'sthumb';
  bar.appendChild(thumb); el.appendChild(bar);
  let timer = 0, userAt = 0, dragging = false, hovering = false, startY = 0, startLine = 0;
  const touch = () => { userAt = Date.now(); };
  const show = () => {
    bar.classList.add('on'); clearTimeout(timer);
    timer = setTimeout(() => { if (!dragging && !hovering) bar.classList.remove('on'); }, 1000);
  };
  const metrics = () => {
    const b = term.buffer.active, max = b.baseY, track = bar.clientHeight;
    const total = max + term.rows;
    const h = Math.min(track, Math.max(36, Math.round(track * term.rows / total)));
    return { b, max, track, h, travel: track - h };
  };
  const draw = () => {
    const b = term.buffer.active;
    if (b.baseY <= 0) { bar.style.display = 'none'; return; }
    bar.style.display = 'block';
    const m = metrics();
    thumb.style.height = m.h + 'px';
    thumb.style.transform = 'translateY(' + Math.round(m.travel * b.viewportY / m.max) + 'px)';
  };
  term.onScroll(() => { draw(); if (Date.now() - userAt < 700) show(); });
  const viewport = el.querySelector('.xterm-viewport');
  if (viewport) viewport.addEventListener('scroll', () => { draw(); if (Date.now() - userAt < 700) show(); }, { passive: true });
  term.onRender(draw);
  term.onWriteParsed(draw);
  term.onResize(draw);
  el.addEventListener('wheel', () => { touch(); show(); }, { passive: true });
  el.addEventListener('keydown', (e) => { if (['PageUp', 'PageDown', 'Home', 'End'].includes(e.key)) touch(); }, true);
  bar.addEventListener('mouseenter', () => { hovering = true; show(); });
  bar.addEventListener('mouseleave', () => { hovering = false; show(); });
  thumb.addEventListener('pointerdown', (e) => {
    if (e.button !== 0) return;
    dragging = true; startY = e.clientY; startLine = term.buffer.active.viewportY;
    thumb.setPointerCapture(e.pointerId); e.preventDefault(); e.stopPropagation();
  });
  thumb.addEventListener('pointermove', (e) => {
    if (!dragging) return;
    const m = metrics();
    if (m.travel <= 0) return;
    touch(); show();
    term.scrollToLine(Math.max(0, Math.min(m.max, Math.round(startLine + (e.clientY - startY) * m.max / m.travel))));
  });
  const stop = () => { dragging = false; show(); };
  thumb.addEventListener('pointerup', stop);
  thumb.addEventListener('pointercancel', stop);
  // A click on the strip above or below the thumb scrolls one page.
  bar.addEventListener('pointerdown', (e) => {
    if (e.target === thumb || e.button !== 0) return;
    const r = thumb.getBoundingClientRect();
    touch(); term.scrollPages(e.clientY < r.top ? -1 : 1); show(); e.preventDefault(); e.stopPropagation();
  });
  draw();
}

// Scroll past the end, like Command Prompt: an empty pad below the last line makes the viewport scroll further,
// and the screen is moved up by the extra distance so empty space shows below the last line.
// Any new output or typing snaps back to the bottom. xterm itself keeps showing the last line at the bottom (its buffer is unchanged).
function attachPastEnd(el, term) {
  const viewport = el.querySelector('.xterm-viewport');
  const screen = el.querySelector('.xterm-screen');
  if (!viewport || !screen) return () => {};
  const pad = document.createElement('div');
  pad.style.cssText = 'width:1px;height:0;visibility:hidden;';
  viewport.appendChild(pad);
  const rowH = () => (term.rows > 0 && screen.clientHeight > 0) ? screen.clientHeight / term.rows : 0;
  const reset = () => { screen.style.transform = ''; };
  const sync = () => {
    if (!cfg.scrollPastEnd) { reset(); return; }
    const h = rowH();
    if (h <= 0) return;
    const padH = Math.max(0, (term.rows - 1) * h);
    if (Math.abs(parseFloat(pad.style.height || '0') - padH) > 0.5) pad.style.height = padH + 'px';
    const b = term.buffer.active;
    const extra = Math.max(0, Math.min(padH, viewport.scrollTop - b.baseY * h));
    if (b.viewportY < b.baseY || extra < 1) { reset(); return; }
    screen.style.transform = 'translateY(' + (-Math.round(extra)) + 'px)';
  };
  const snap = () => {
    if (viewport.scrollTop > term.buffer.active.baseY * rowH() + 1) viewport.scrollTop = term.buffer.active.baseY * rowH();
    reset();
  };
  viewport.addEventListener('scroll', sync, { passive: true });
  term.onResize(sync);
  term.onWriteParsed(snap);
  term.onData(snap);
  const apply = () => {
    if (!cfg.scrollPastEnd) { pad.style.height = '0'; snap(); }
    else sync();
  };
  apply();
  return apply;
}

function create(id) {
  const el = document.createElement('div');
  el.className = 'term';
  hostEl.appendChild(el);
  const term = new Terminal({
    fontFamily: cfg.fontFamily,
    fontWeight: cfg.fontWeight,
    fontWeightBold: cfg.fontWeightBold,
    fontSize: cfg.fontSize,
    lineHeight: cfg.lineHeight,
    cursorStyle: cfg.cursorStyle,
    cursorBlink: cfg.cursorBlink,
    scrollback: cfg.scrollback,
    allowProposedApi: true,
    theme: cfg.theme
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.loadAddon(new WebLinksAddon.WebLinksAddon((event, uri) => post({ t: 'link', id, uri })));
  term.open(el);
  attachScrollbar(el, term);
  const pastEnd = attachPastEnd(el, term);

  term.onData((d) => { hideMenus(); post({ t: 'input', id, d }); });
  term.onResize((s) => post({ t: 'resize', id, cols: s.cols, rows: s.rows }));
  term.onSelectionChange(() => onSelection(id));
  // Ctrl+C copies only when text is selected, otherwise it goes to the program. Ctrl+V pastes through C# (so the multiline rule applies).
  term.attachCustomKeyEventHandler((e) => {
    if (e.type !== 'keydown') return true;
    if (e.ctrlKey && !e.shiftKey && !e.altKey && e.key.toLowerCase() === 'c' && term.hasSelection()) {
      e.preventDefault(); doCopy(id); return false;
    }
    if (e.ctrlKey && !e.altKey && (e.key === '=' || e.key === '+' || e.key === '-')) {
      e.preventDefault(); post({ t: 'zoom', id, d: e.key === '-' ? -1 : 1 }); return false;
    }
    if (e.ctrlKey && !e.altKey && e.key.toLowerCase() === 'v') {
      e.preventDefault(); post({ t: 'paste', id, source: 'key' }); return false;
    }
    return true;
  });
  // A click without a selection may offer Paste, only when C# says the clipboard has text.
  el.addEventListener('mouseup', (e) => {
    if (e.button !== 0 || !pasteMenuOn) return;
    setTimeout(() => {
      if (term.hasSelection()) return;
      // Only on the line where typing happens (the cursor row), not on old output.
      const screen = el.querySelector('.xterm-screen');
      if (!screen) return;
      const box = screen.getBoundingClientRect();
      const row = Math.floor((e.clientY - box.top) / (box.height / term.rows));
      if (row !== term.buffer.active.cursorY) return;
      post({ t: 'clickoffer', id, x: e.clientX, y: e.clientY });
    }, 0);
  });
  // Copy on select: copies when the mouse is released with a selection (quietly, no message).
  el.addEventListener('mouseup', (e) => {
    if (e.button !== 0 || !cfg.copyOnSelect) return;
    setTimeout(() => {
      if (!term.hasSelection()) return;
      const x = e.clientX, y = e.clientY;
      navigator.clipboard.writeText(term.getSelection())
        .then(() => showCopied(x, y, true))
        .catch(() => showCopied(x, y, false));
    }, 0);
  });
  // Right click: paste, show the Paste menu, or nothing (Settings > Terminal > Input).
  el.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    if (cfg.rightClick === 'Pastes') post({ t: 'paste', id, source: 'key' });
    else if (cfg.rightClick === 'Menu' && pasteMenuOn) post({ t: 'clickoffer', id, x: e.clientX, y: e.clientY });
  });
  terms.set(id, { term, fit, el, pastEnd });
  return terms.get(id);
}

function showTerm(id) {
  hideMenus();
  for (const [k, v] of terms) v.el.classList.toggle('active', k === id);
  active = id;
  const t = terms.get(id);
  if (!t) return;
  requestAnimationFrame(() => { try { t.fit.fit(); } catch (e) {} t.term.focus(); });
}

function removeTerm(id) {
  const t = terms.get(id);
  if (!t) return;
  t.term.dispose();
  t.el.remove();
  terms.delete(id);
  if (active === id) active = null;
}

function hideMenus() {
  copyMenu.style.display = 'none';
  pasteMenu.style.display = 'none';
  pasteOffer = null;
}

// Menus sit just ABOVE the point (user, 2026-10-23). Below it only when there is no room above.
function placeMenu(menu, centerX, topY, bottomY) {
  menu.style.display = 'block';
  const w = menu.offsetWidth, h = menu.offsetHeight;
  let top = topY - h - 6;
  if (top < 4) top = bottomY + 6;
  let left = centerX - w / 2;
  left = Math.max(4, Math.min(left, window.innerWidth - w - 4));
  top = Math.max(4, Math.min(top, window.innerHeight - h - 4));
  menu.style.left = left + 'px';
  menu.style.top = top + 'px';
}

function onSelection(id) {
  const t = terms.get(id);
  if (!t || id !== active) return;
  const text = t.term.getSelection();
  const pos = t.term.getSelectionPosition();
  pasteMenu.style.display = 'none';
  if (!copyMenuOn || !text || !pos) { copyMenu.style.display = 'none'; return; }
  const screen = t.el.querySelector('.xterm-screen').getBoundingClientRect();
  const cw = screen.width / t.term.cols, ch = screen.height / t.term.rows;
  const row = pos.end.y - t.term.buffer.active.viewportY;
  const x = screen.left + cw * pos.end.x;
  placeMenu(copyMenu, x, screen.top + ch * row, screen.top + ch * (row + 1));
}

// Small "Copied" note just above the mouse pointer (Copy on select). It sits above the Copy menu when that menu is shown too.
let tipTimer = 0;
function showCopied(x, y, ok) {
  const tip = document.getElementById('copiedTip');
  tip.textContent = ok ? 'Copied' : 'Could not copy';
  placeMenu(tip, x, y, y);
  if (copyMenu.style.display === 'block') {
    const top = copyMenu.offsetTop - tip.offsetHeight - 4;
    tip.style.top = Math.max(4, top) + 'px';
  }
  tip.classList.add('on');
  clearTimeout(tipTimer);
  tipTimer = setTimeout(() => {
    tip.classList.remove('on');
    tipTimer = setTimeout(() => { tip.style.display = 'none'; }, 200);
  }, 1200);
}

// x and y: where the mouse pointer is. Given when the Copy menu was clicked, so "Copied" shows near the pointer.
async function doCopy(id, x, y) {
  const t = terms.get(id);
  if (!t) return;
  const text = t.term.getSelection();
  if (!text) return;
  let ok = true;
  try { await navigator.clipboard.writeText(text); post({ t: 'copied', id, len: text.length, ok: true }); }
  catch (e) { ok = false; post({ t: 'copied', id, len: text.length, ok: false }); }
  hideMenus();
  if (x !== undefined) showCopied(x, y, ok);
  t.term.focus();
}

copyMenu.addEventListener('mousedown', (e) => e.preventDefault());
copyMenu.addEventListener('click', (e) => { if (active !== null) doCopy(active, e.clientX, e.clientY); });
pasteMenu.addEventListener('mousedown', (e) => e.preventDefault());
pasteMenu.addEventListener('click', () => {
  if (pasteOffer) post({ t: 'paste', id: pasteOffer.id, source: 'menu' });
  hideMenus();
});

// Every paste (Ctrl+V, Shift+Insert, menu) goes through C#, so the multiline rule and control-character removal always apply.
hostEl.addEventListener('paste', (ev) => {
  ev.preventDefault(); ev.stopPropagation();
  if (active !== null) post({ t: 'paste', id: active, source: 'event' });
}, true);

// While the card slides (bar hides or shows) the app sets frozen = true, so the terminal is resized only once at the end.
let frozen = false;
function fitActive() { const t = terms.get(active); if (t) { try { t.fit.fit(); } catch (e) {} } }
new ResizeObserver(() => { if (!frozen) fitActive(); }).observe(hostEl);

function applyConfig(m) {
  const family = m.fontFamily || FONT;
  const familyChanged = family !== cfg.fontFamily;
  cfg.fontFamily = family;
  cfg.fontSize = m.fontSize; cfg.lineHeight = m.lineHeight; cfg.cursorStyle = m.cursorStyle;
  cfg.cursorBlink = m.cursorBlink; cfg.scrollback = m.scrollback; cfg.theme = themeFrom(m);
  cfg.fontWeight = m.weight; cfg.fontWeightBold = m.weightBold;
  cfg.scrollPastEnd = m.scrollPastEnd !== false;
  cfg.copyOnSelect = !!m.copyOnSelect; cfg.rightClick = m.rightClick || 'Pastes';
  document.documentElement.classList.toggle('lig', !!m.ligatures);
  document.documentElement.classList.toggle('thick', !!m.thicken);
  copyMenuOn = !!m.copyMenuOn;
  pasteMenuOn = !!m.pasteMenuOn;
  if (!copyMenuOn || !pasteMenuOn) hideMenus();
  const root = document.documentElement.style;
  root.setProperty('--bg', m.bg); root.setProperty('--pop', m.pop); root.setProperty('--popEdge', m.popEdge);
  root.setProperty('--hover', m.hover); root.setProperty('--fg', m.fg);
  root.setProperty('--sb', m.dark ? 'rgba(255,255,255,.40)' : 'rgba(0,0,0,.30)');
  document.documentElement.classList.toggle('dark', !!m.dark);
  for (const v of terms.values()) {
    const o = v.term.options;
    o.theme = cfg.theme; o.fontSize = cfg.fontSize; o.lineHeight = cfg.lineHeight;
    o.fontWeight = cfg.fontWeight; o.fontWeightBold = cfg.fontWeightBold;
    o.cursorStyle = cfg.cursorStyle; o.cursorBlink = cfg.cursorBlink; o.scrollback = cfg.scrollback;
    try { v.fit.fit(); } catch (e) {}
    if (v.pastEnd) v.pastEnd();
  }
  if (familyChanged) {
    // Wait for the font file, then set the family again so xterm measures the real font.
    loadFont(m.fontName).then(() => {
      for (const v of terms.values()) {
        v.term.options.fontFamily = 'monospace';
        v.term.options.fontFamily = cfg.fontFamily;
        try { v.fit.fit(); } catch (e) {}
      }
    });
  }
}

window.chrome.webview.addEventListener('message', (e) => {
  const m = e.data;
  switch (m.t) {
    case 'freeze':
      if (m.on) frozen = true;
      else { frozen = false; fitActive(); setTimeout(fitActive, 60); }
      break;
    case 'cfg': applyConfig(m); break;
    case 'create': create(m.id); break;
    case 'show': showTerm(m.id); break;
    case 'remove': removeTerm(m.id); break;
    case 'out': { const t = terms.get(m.id); if (t) t.term.write(m.d); break; }
    case 'pastetext': { const t = terms.get(m.id); if (t) { t.term.paste(m.d); t.term.focus(); } break; }
    case 'showPaste':
      if (m.id === active && pasteMenuOn) { pasteOffer = { id: m.id }; placeMenu(pasteMenu, m.x, m.y, m.y); }
      break;
    case 'focus': { const t = terms.get(active); if (t) t.term.focus(); break; }
  }
});

// Wait (at most 1.5 s) for the terminal font, so xterm measures the real font and not a fallback.
const fontReady = (document.fonts && document.fonts.load)
  ? Promise.race([document.fonts.load("13px 'JetBrains Mono'"), new Promise((r) => setTimeout(r, 1500))]).catch(() => {})
  : Promise.resolve();
fontReady.then(() => post({ t: 'ready' }));
