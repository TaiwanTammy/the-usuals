// The menu under the toolbar icon. It reads saved sessions straight from storage so it opens instantly, and
// every change shows on screen right away while the background does the work.
const $ = (s) => document.querySelector(s);
const send = (msg) => chrome.runtime.sendMessage({ windowId: myWindow, ...msg });

const ICON = {
  edit: '<svg viewBox="0 0 20 20"><path d="M4 16h3l8.5-8.5-3-3L4 13z"/><path d="M11 6l3 3"/></svg>',
  trash: '<svg viewBox="0 0 20 20"><path d="M4 6h12M8 6V4h4v2M6 6l1 10h6l1-10"/></svg>',
  check: '<svg viewBox="0 0 20 20"><path d="M5 10.5l3.2 3L15 7"/></svg>',
  chevron: '<svg viewBox="0 0 20 20"><path d="M8 5l5 5-5 5"/></svg>',
};

let myWindow = null;
let data = { sessions: [], pinned: [], currentId: null, windowId: null, tabs: [], here: true, otherWindow: false };

// Session list state
let sel = 0; // which actionable row Enter will open
let editing = null; // session id being renamed
let confirming = null; // session id waiting on "Delete?"
const expanded = new Set(); // sessions opened up to show their tabs

// Move tabs state
let picked = new Set();
let lastClicked = null;
let dsel = 0;
let naming = false; // opened from right-click "New session…": the destination is a new session

const plural = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;
const esc = (s) =>
  String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const newId = () => Date.now().toString(36) + Math.random().toString(36).slice(2, 7);

function ago(ms) {
  if (!ms) return 'not opened yet';
  const m = Math.round((Date.now() - ms) / 60000);
  if (m < 1) return 'just now';
  if (m < 60) return `${m} min ago`;
  const h = Math.round(m / 60);
  if (h < 24) return `${h} h ago`;
  const d = Math.round(h / 24);
  return d === 1 ? 'yesterday' : `${d} days ago`;
}

// The same, short enough for a narrow column: "now", "40m", "5h", "12d".
function shortAgo(ms) {
  if (!ms) return '–';
  const m = Math.round((Date.now() - ms) / 60000);
  if (m < 1) return 'now';
  if (m < 60) return `${m}m`;
  const h = Math.round(m / 60);
  return h < 24 ? `${h}h` : `${Math.round(h / 24)}d`;
}

async function loadData() {
  const [all, tabs, groups] = await Promise.all([
    chrome.storage.local.get(null),
    chrome.tabs.query({ windowId: myWindow }),
    // Not there while an older version is still loaded, so ask only if it exists.
    chrome.tabGroups ? chrome.tabGroups.query({ windowId: myWindow }).catch(() => []) : [],
  ]);
  data.groups = new Map(groups.map((g) => [g.id, g]));
  const byId = new Map();
  for (const s of Object.values(all.sessions || {})) byId.set(s.id, s); // saved by version 1, before it's converted
  for (const [k, v] of Object.entries(all)) if (k.startsWith('s:')) byId.set(v.id, v);
  data.sessions = [...byId.values()];
  data.currentId = all.currentId || null;
  data.pinned = all.pinned || [];
  data.usuals = (await chrome.storage.session.get('usuals')).usuals || null; // The Usuals sidebar's name, while it's running
  data.windowId = all.windowId ?? null;
  data.tabs = tabs
    .filter((t) => !t.pinned)
    .map((t) => ({ id: t.id, highlighted: t.highlighted, groupId: t.groupId, ...realTab(t) }))
    .filter((t) => worthSaving(t.url));
  data.here = data.windowId === myWindow;
  data.otherWindow =
    data.windowId != null && !data.here && (await chrome.windows.get(data.windowId).then(() => true, () => false));
}

const ordered = () =>
  [...data.sessions].sort((a, b) =>
    a.id === data.currentId ? -1 : b.id === data.currentId ? 1 : (b.openedAt || 0) - (a.openedAt || 0)
  );

// The open session's tabs are read live from the window; the others from what was saved.
const tabsOf = (s) => (s.id === data.currentId && data.here ? data.tabs : s.tabs);

// Chrome's own tab group colors, so a group looks the same here as in the tab bar.
const GROUP_CSS = {
  grey: '#5f6368', blue: '#1a73e8', red: '#d93025', yellow: '#f9ab00', green: '#1e8e3e',
  pink: '#d01884', purple: '#a142f4', cyan: '#007b83', orange: '#fa903e',
};

function groupLabel(t) {
  const g = t.groupId >= 0 && data.groups?.get(t.groupId);
  if (!g) return '';
  return ` · <span class="group"><i style="background:${GROUP_CSS[g.color] || GROUP_CSS.grey}"></i>${esc(g.title || 'Group')}</span>`;
}

// One tiny icon per tab, so you can see at a glance how full a session is and what's in it.
const STRIP_MAX = 11;
function stripHtml(tabs) {
  const shown = tabs.length > STRIP_MAX ? STRIP_MAX - 1 : tabs.length;
  const imgs = tabs.slice(0, shown).map((t) => `<img src="${favicon(t.url)}" alt="">`).join('');
  const more = tabs.length > shown ? `<span class="more">+${tabs.length - shown}</span>` : '';
  return `<span class="strip" title="${plural(tabs.length, 'tab')}">${imgs}${more}</span>`;
}

function iconsHtml(tabs) {
  const oneEach = [...new Map(tabs.map((t) => [hostOf(t.url), t.url])).values()].slice(0, 3);
  return oneEach.map((u) => `<img src="${favicon(u)}" alt="">`).join('') || '<span class="plus">·</span>';
}

function toast(text) {
  const el = $('#toast');
  el.textContent = text;
  el.hidden = false;
  clearTimeout(toast.t);
  toast.t = setTimeout(() => (el.hidden = true), 2500);
}

// ---------- session list ----------

// Sessions whose name matches, then sessions holding a matching tab, then "Start <name>" if the name is new.
function rows() {
  const q = $('#q').value.trim();
  const low = q.toLowerCase();
  const named = [];
  const hits = [];
  for (const s of ordered()) {
    if (!low || s.name.toLowerCase().includes(low)) {
      named.push({ kind: 'session', s });
      continue;
    }
    const hit = tabsOf(s).find((t) => (t.title || '').toLowerCase().includes(low) || t.url.toLowerCase().includes(low));
    if (hit) hits.push({ kind: 'session', s, hit });
  }
  const out = [...named, ...hits];
  if (q && !data.sessions.some((s) => s.name.toLowerCase() === low)) out.push({ kind: 'start', name: q });
  return out;
}
const actionable = (r) => r.kind === 'start' || r.s.id !== data.currentId || !!r.hit;

function render() {
  const list = rows();
  const acts = list.filter(actionable);
  sel = Math.min(sel, Math.max(0, acts.length - 1));
  const selected = acts[sel];

  $('#list').innerHTML = list.length
    ? list.map((r) => rowHtml(r, r === selected)).join('')
    : '<li class="empty">Nothing matches.</li>';

  $('#note').hidden = !data.otherWindow;
  $('#note').textContent = 'Your sessions live in your other Chrome window. Switching will happen there.';

  // With The Usuals running, everyday sites belong in its dock: no pinning here, just a way to send old pins over.
  $('.pinned').hidden = !!data.usuals && !data.pinned.length;
  $('#pin').textContent = data.usuals ? `Move to ${data.usuals}` : 'Pin this tab';
  $('#pins').innerHTML = data.pinned.length
    ? data.pinned
        .map(
          (p) =>
            `<div class="pin" title="${esc(p.title)}"><img src="${favicon(p.url)}" alt=""><button class="x" data-unpin="${esc(p.url)}" title="Stop pinning">×</button></div>`
        )
        .join('')
    : '<span class="hint">Right-click any tab and choose Pin. It stays put when you switch sessions.</span>';

  fitList();

  const input = $('#list .rename');
  if (input) {
    input.focus();
    input.select();
  }
}

function rowHtml(r, isSel) {
  if (r.kind === 'start') {
    return `<li class="row${isSel ? ' sel' : ''}" data-start="1">
      <span class="exp"></span>
      <div class="text"><div class="name">Start “${esc(r.name)}”</div><div class="meta">New session with a blank tab</div></div>
    </li>`;
  }
  const s = r.s;
  const current = s.id === data.currentId;
  const tabs = tabsOf(s);

  // The open session's name is the rename box: click it and type. Other sessions: right-click or F2.
  const name =
    editing === s.id
      ? `<input class="rename" data-rename="${s.id}" value="${esc(s.name)}">`
      : `<div class="name"${current ? ` data-edit="${s.id}" title="Click to rename"` : ''}>${esc(s.name)}${
          r.hit ? `<span class="hit"> · ${esc(r.hit.title)}</span>` : ''
        }</div>`;

  const right =
    confirming === s.id
      ? `<div class="confirm">Delete? <button class="yes" data-yes="${s.id}">Yes</button><button data-no="1">No</button></div>`
      : `${stripHtml(tabs)}
         <span class="when">${current ? '<span class="badge">Open</span>' : shortAgo(s.openedAt)}</span>
         ${current ? '<span class="tool"></span>' : `<button class="tool del" data-del="${s.id}" title="Delete">${ICON.trash}</button>`}`;

  const open = expanded.has(s.id);
  const toggle = tabs.length
    ? `<button class="exp${open ? ' open' : ''}" data-expand="${s.id}" title="${open ? 'Hide' : 'Show'} its tabs">${ICON.chevron}</button>`
    : '<span class="exp"></span>';
  return `<li class="row${isSel ? ' sel' : ''}${current && !r.hit ? ' current' : ''}" data-id="${s.id}">
    ${toggle}
    <div class="text">${name}</div>
    ${right}
  </li>${open ? tabListHtml(s, tabs) : ''}`;
}

// A session opened up: every tab in it, with its site and group. Clicking one opens the session on that tab.
function tabListHtml(s, tabs) {
  const groupOf = (t) =>
    t.groupId != null
      ? data.groups?.get(t.groupId) // a live tab in the open window
      : t.g != null
        ? s.groups?.[t.g] // a saved tab
        : null;
  const items = tabs
    .map((t, i) => {
      const g = groupOf(t);
      const dot = g
        ? ` · <span class="group"><i style="background:${GROUP_CSS[g.color] || GROUP_CSS.grey}"></i>${esc(g.title || 'Group')}</span>`
        : '';
      return `<li class="tab-row" draggable="true" data-sid="${s.id}" data-i="${i}" data-url="${esc(t.url)}" title="${esc(t.url)}">
        <img src="${favicon(t.url)}" alt="">
        <div class="text"><div class="name">${esc(t.title || t.url)}</div><div class="meta">${esc(hostOf(t.url))}${dot}</div></div>
        <button class="tool del" data-deltab="1" title="Delete tab">${ICON.trash}</button>
      </li>`;
    })
    .join('');
  return `<li class="tab-list" data-sid="${s.id}"><ul>${items}</ul></li>`;
}

// Chrome caps this menu at 600px tall. Give the session list every pixel the rest of the menu leaves,
// so it only scrolls when the sessions truly don't fit.
function fitList() {
  const list = $('#list');
  const rest = $('#sessionsView').offsetHeight - list.offsetHeight;
  list.style.maxHeight = 600 - rest + 'px';
}

// ---------- one tab inside a session: delete, drag to another session, right-click ----------

// The tab a row in an opened-up session stands for.
function tabRef(el) {
  const s = data.sessions.find((x) => x.id === el.dataset.sid);
  if (!s) return null;
  const list = tabsOf(s);
  const i = Number(el.dataset.i);
  return list[i] ? { s, i, t: list[i], list } : null;
}

function deleteTab(ref) {
  send({ type: 'removeTab', sid: ref.s.id, i: ref.i, url: ref.t.url, tabId: ref.t.id });
  ref.list.splice(ref.i, 1);
  render();
}

function moveTabTo(ref, target) {
  if (!target || target.id === ref.s.id) return;
  send({ type: 'moveTab', sid: ref.s.id, i: ref.i, url: ref.t.url, tabId: ref.t.id, to: target.id });
  ref.list.splice(ref.i, 1);
  const into = tabsOf(target);
  if (!into.some((t) => t.url === ref.t.url)) into.push({ url: ref.t.url, title: ref.t.title });
  toast(`Moved to ${target.name}`);
  render();
}

// Which session a spot in the list belongs to: its row, or a tab inside it.
const sessionAt = (el) => {
  const id = el?.closest('[data-id], [data-sid]');
  const sid = id?.dataset.id || id?.dataset.sid;
  return sid ? data.sessions.find((s) => s.id === sid) : null;
};

let dragging = null;
const clearDrop = () => document.querySelectorAll('#list .drop').forEach((x) => x.classList.remove('drop'));

$('#list').addEventListener('dragstart', (e) => {
  const row = e.target.closest?.('.tab-row');
  dragging = row && tabRef(row);
  if (!dragging) return e.preventDefault();
  e.dataTransfer.effectAllowed = 'move';
  e.dataTransfer.setData('text/plain', dragging.t.url);
  hideCtx();
});

$('#list').addEventListener('dragover', (e) => {
  if (!dragging) return;
  const s = sessionAt(e.target);
  clearDrop();
  if (!s || s.id === dragging.s.id) return;
  e.preventDefault();
  e.dataTransfer.dropEffect = 'move';
  $(`#list li.row[data-id="${s.id}"]`)?.classList.add('drop');
});

$('#list').addEventListener('drop', (e) => {
  e.preventDefault();
  clearDrop();
  if (dragging) moveTabTo(dragging, sessionAt(e.target));
  dragging = null;
});

$('#list').addEventListener('dragend', () => {
  clearDrop();
  dragging = null;
});

let ctxRef = null;

function hideCtx() {
  $('#ctx').hidden = true;
  document.body.style.minHeight = '';
  ctxRef = null;
  ctxSession = null;
}

function showCtx(e, html) {
  e.preventDefault();
  const m = $('#ctx');
  m.innerHTML = html;
  m.hidden = false;
  m.style.maxHeight = '';
  document.body.style.minHeight = Math.min(600, m.scrollHeight + 8) + 'px';
  const w = document.documentElement.clientWidth;
  const h = Math.max(document.documentElement.clientHeight, parseInt(document.body.style.minHeight));
  m.style.maxHeight = h - 8 + 'px';
  m.style.left = Math.max(4, Math.min(e.clientX, w - m.offsetWidth - 4)) + 'px';
  m.style.top = Math.max(4, Math.min(e.clientY, h - m.offsetHeight - 4)) + 'px';
}

// Right-click a session: Rename, Move tabs (the open one), Delete (the others).
let ctxSession = null;
$('#list').addEventListener('contextmenu', (e) => {
  const row = e.target.closest('li.row[data-id]');
  if (!row) return;
  const s = data.sessions.find((x) => x.id === row.dataset.id);
  if (!s) return;
  hideCtx();
  ctxSession = s;
  const current = s.id === data.currentId;
  showCtx(
    e,
    `<button data-c="rename">Rename</button>
     ${current && data.tabs.length ? '<button data-c="sort">Move tabs…</button>' : ''}
     ${current ? '' : '<div class="ctx-line"></div><button data-c="del" class="danger">Delete session</button>'}`
  );
});

function startRename(id) {
  editing = id;
  confirming = null;
  render();
}

$('#list').addEventListener('contextmenu', (e) => {
  const row = e.target.closest('.tab-row');
  if (!row) return;
  e.preventDefault();
  ctxRef = tabRef(row);
  if (!ctxRef) return;
  const others = ordered().filter((s) => s.id !== ctxRef.s.id);
  if (!others.length) return hideCtx();
  showCtx(
    e,
    `<div class="ctx-label">Move to</div>${others.map((s) => `<button data-to="${s.id}">${esc(s.name)}</button>`).join('')}`
  );
});

$('#ctx').addEventListener('click', (e) => {
  const b = e.target.closest('button');
  const ref = ctxRef;
  const s = ctxSession;
  if (!b || (!ref && !s)) return;
  hideCtx();
  if (s) {
    if (b.dataset.c === 'rename') startRename(s.id);
    else if (b.dataset.c === 'sort') openSort();
    else if (b.dataset.c === 'del') {
      confirming = s.id;
      render();
    }
    return;
  }
  if (b.dataset.to) moveTabTo(ref, data.sessions.find((s) => s.id === b.dataset.to));
});

document.addEventListener('mousedown', (e) => {
  if (!$('#ctx').hidden && !e.target.closest('#ctx')) hideCtx();
});
$('#list').addEventListener('scroll', hideCtx);

async function act(r) {
  if (!r) return;
  if (r.kind === 'start') await send({ type: 'start', name: r.name });
  else await send({ type: 'switch', id: r.s.id, focusUrl: r.hit?.url });
  window.close();
}

async function finishRename(input) {
  const name = input.value.trim();
  const s = data.sessions.find((x) => x.id === input.dataset.rename);
  editing = null;
  if (name && s && name !== s.name) {
    s.name = name;
    send({ type: 'rename', id: s.id, name });
  }
  render();
  $('#q').focus();
}

$('#q').addEventListener('input', () => {
  sel = 0;
  render();
});

$('#list').addEventListener('focusout', (e) => {
  if (e.target.classList.contains('rename') && editing) finishRename(e.target);
});

$('#list').addEventListener('click', (e) => {
  const t = e.target.closest('button, li');
  if (!t) return;
  if (t.dataset.expand) {
    if (expanded.has(t.dataset.expand)) expanded.delete(t.dataset.expand);
    else expanded.add(t.dataset.expand);
    return render();
  }
  if (t.dataset.deltab) {
    const ref = tabRef(t.closest('.tab-row'));
    if (ref) deleteTab(ref);
    return;
  }
  if (t.classList.contains('tab-row')) {
    const s = data.sessions.find((x) => x.id === t.dataset.sid);
    if (s) act({ kind: 'session', s, hit: { url: t.dataset.url } });
    return;
  }
  if (t.dataset.sort) return openSort();
  const nameEdit = e.target.closest('[data-edit]');
  if (nameEdit) return startRename(nameEdit.dataset.edit);
  if (t.dataset.del) {
    confirming = t.dataset.del;
    return render();
  }
  if (t.dataset.no) {
    confirming = null;
    return render();
  }
  if (t.dataset.yes) {
    confirming = null;
    data.sessions = data.sessions.filter((s) => s.id !== t.dataset.yes);
    send({ type: 'delete', id: t.dataset.yes });
    return render();
  }
  if (t.tagName === 'LI' && !editing && !confirming) {
    if (t.dataset.start) return act(rows().find((r) => r.kind === 'start'));
    const r = rows().find((x) => x.kind === 'session' && x.s.id === t.dataset.id);
    if (r && actionable(r)) act(r);
  }
});

$('#pins').addEventListener('click', (e) => {
  const url = e.target.dataset.unpin;
  if (!url) return;
  data.pinned = data.pinned.filter((p) => p.url !== url);
  send({ type: 'unpin', url });
  render();
});

$('#pin').addEventListener('click', async () => {
  if (data.usuals) {
    send({ type: 'pinsToUsuals' });
    data.pinned = [];
    return render();
  }
  const [tab] = await chrome.tabs.query({ windowId: myWindow, active: true });
  if (tab && !tab.pinned && worthSaving(realTab(tab).url)) {
    data.pinned.push({ url: realTab(tab).url, title: realTab(tab).title });
    data.tabs = data.tabs.filter((t) => t.id !== tab.id);
  }
  send({ type: 'pin' });
  render();
});

$('#backup').addEventListener('click', () => {
  chrome.runtime.openOptionsPage();
  window.close();
});

// ---------- move tabs ----------

function openSort(preselect = [], isNew = false) {
  picked = new Set(preselect);
  naming = isNew;
  lastClicked = null;
  dsel = 0;
  // Tabs you already Ctrl-clicked in the tab bar come pre-ticked.
  if (!picked.size) {
    const hl = data.tabs.filter((t) => t.highlighted);
    if (hl.length > 1) hl.forEach((t) => picked.add(t.id));
  }
  $('#sessionsView').hidden = true;
  $('#sortView').hidden = false;
  $('#dq').value = '';
  $('#dq').placeholder = naming ? 'Name the new session' : 'Type a session name';
  renderSort();
  $('#tabs .picked')?.scrollIntoView({ block: 'nearest' });
  $('#dq').focus();
}

function closeSort() {
  $('#sortView').hidden = true;
  $('#sessionsView').hidden = false;
  naming = false;
  render();
  $('#q').focus();
}

function destRows() {
  const q = $('#dq').value.trim();
  const low = q.toLowerCase();
  const exact = data.sessions.find((s) => s.name.toLowerCase() === low && s.id !== data.currentId);
  if (naming) return exact ? [{ kind: 'session', s: exact }] : q ? [{ kind: 'start', name: q }] : [];
  const out = ordered()
    .filter((s) => s.id !== data.currentId && (!low || s.name.toLowerCase().includes(low)))
    .map((s) => ({ kind: 'session', s }));
  if (q && !data.sessions.some((s) => s.name.toLowerCase() === low)) out.push({ kind: 'start', name: q });
  return out;
}

function renderSort() {
  const cur = data.sessions.find((s) => s.id === data.currentId);
  $('#sortName').textContent = 'Move tabs';
  $('#sortMeta').textContent = data.tabs.length
    ? `${plural(data.tabs.length, 'tab')} in ${data.here && cur ? cur.name : 'this window'}`
    : 'No tabs left here';
  $('#all').hidden = !data.tabs.length;
  $('#all').textContent = data.tabs.length && picked.size === data.tabs.length ? 'Clear' : 'Select all';

  $('#tabs').innerHTML =
    data.tabs
      .map(
        (t) => `<li class="row${picked.has(t.id) ? ' picked' : ''}" data-tab="${t.id}" title="${esc(t.url)}">
        <span class="check">${ICON.check}</span><img src="${favicon(t.url)}" alt="">
        <div class="text"><div class="name">${esc(t.title)}</div><div class="meta">${esc(hostOf(t.url))}${groupLabel(t)}</div></div>
      </li>`
      )
      .join('') || '<li class="empty">Every tab has been moved.</li>';

  const n = picked.size;
  $('.dest').classList.toggle('off', !n);
  $('#destLabel').textContent = !n
    ? 'Tick the tabs to move'
    : naming
      ? `New session for ${plural(n, 'tab')}`
      : `Move ${plural(n, 'tab')} to`;
  const list = destRows();
  dsel = Math.min(dsel, Math.max(0, list.length - 1));
  $('#dests').innerHTML = list
    .map((r, i) =>
      r.kind === 'start'
        ? `<li class="row${i === dsel ? ' sel' : ''}" data-dest="${i}"><div class="icons"><span class="plus">+</span></div>
           <div class="text"><div class="name">Start “${esc(r.name)}”</div><div class="meta">New session</div></div></li>`
        : `<li class="row${i === dsel ? ' sel' : ''}" data-dest="${i}"><div class="icons">${iconsHtml(r.s.tabs)}</div>
           <div class="text"><div class="name">${esc(r.s.name)}</div><div class="meta">${plural(r.s.tabs.length, 'tab')}</div></div></li>`
    )
    .join('');
}

function doMove(r) {
  if (!r || !picked.size) return;
  const moving = data.tabs.filter((t) => picked.has(t.id));
  let target = r.kind === 'session' ? r.s : null;
  if (!target) {
    // Make the id here so this menu can keep moving tabs into the new session before the background catches up.
    target = { id: newId(), name: r.name, tabs: [], openedAt: 0 };
    data.sessions.push(target);
  }
  send({ type: 'move', tabIds: moving.map((t) => t.id), to: target.id, name: target.name });

  const have = new Set(target.tabs.map((t) => t.url));
  target.tabs = [...target.tabs, ...moving.filter((t) => !have.has(t.url)).map((t) => ({ url: t.url, title: t.title }))];
  data.tabs = data.tabs.filter((t) => !picked.has(t.id));
  picked.clear();
  naming = false;
  $('#dq').value = '';
  $('#dq').placeholder = 'Type a session name';
  dsel = 0;
  toast(`Moved ${plural(moving.length, 'tab')} to ${target.name}`);
  renderSort();
  $('#dq').focus();
}

$('#tabs').addEventListener('click', (e) => {
  const li = e.target.closest('li[data-tab]');
  if (!li) return;
  const id = Number(li.dataset.tab);
  const on = !picked.has(id);
  // Shift-click ticks or unticks everything between this tab and the last one you clicked.
  const ids = data.tabs.map((t) => t.id);
  const from = e.shiftKey && lastClicked != null ? ids.indexOf(lastClicked) : -1;
  const range = from >= 0 ? ids.slice(Math.min(from, ids.indexOf(id)), Math.max(from, ids.indexOf(id)) + 1) : [id];
  range.forEach((x) => (on ? picked.add(x) : picked.delete(x)));
  lastClicked = id;
  renderSort();
  $('#dq').focus();
});

$('#all').addEventListener('click', () => {
  if (picked.size === data.tabs.length) picked.clear();
  else data.tabs.forEach((t) => picked.add(t.id));
  renderSort();
  $('#dq').focus();
});

$('#dests').addEventListener('click', (e) => {
  const li = e.target.closest('li[data-dest]');
  if (li) doMove(destRows()[Number(li.dataset.dest)]);
});

$('#dq').addEventListener('input', () => {
  dsel = 0;
  renderSort();
});

$('#back').addEventListener('click', closeSort);

// ---------- keyboard ----------

document.addEventListener('keydown', (e) => {
  if (!$('#ctx').hidden) {
    if (e.key === 'Escape') {
      e.preventDefault();
      hideCtx();
    }
    return;
  }
  if (e.target.classList?.contains('rename')) {
    if (e.key === 'Enter') finishRename(e.target);
    if (e.key === 'Escape') {
      e.preventDefault();
      editing = null;
      render();
      $('#q').focus();
    }
    return;
  }
  const sorting = !$('#sortView').hidden;
  const list = sorting ? destRows() : rows().filter(actionable);
  if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
    e.preventDefault();
    const step = e.key === 'ArrowDown' ? 1 : -1;
    if (sorting) {
      dsel = Math.max(0, Math.min(dsel + step, list.length - 1));
      renderSort();
    } else {
      sel = Math.max(0, Math.min(sel + step, list.length - 1));
      render();
    }
  } else if (e.key === 'F2' && !sorting && list[sel]?.kind === 'session') {
    e.preventDefault();
    startRename(list[sel].s.id);
  } else if (e.key === 'Enter') {
    e.preventDefault();
    if (sorting) doMove(list[dsel]);
    else act(list[sel]);
  }
});

// ---------- start ----------

// If a newer version has been saved to disk than the one Chrome is running, reload into it.
async function updateIfNeeded() {
  try {
    const res = await fetch(chrome.runtime.getURL('manifest.json'), { cache: 'no-store' });
    const onDisk = (await res.json()).version;
    if (!onDisk || onDisk === chrome.runtime.getManifest().version) return false;
    document.body.innerHTML = '<p class="updating">Updating Sessions… click the icon again in a moment.</p>';
    setTimeout(() => chrome.runtime.reload(), 600);
    return true;
  } catch {
    return false;
  }
}

(async () => {
  if (await updateIfNeeded()) return;
  myWindow = (await chrome.windows.getCurrent()).id;
  chrome.runtime.sendMessage({ type: 'connectUsuals' }).catch(() => {});
  await loadData();

  // Opened by right-click "New session…": go straight to naming it, with those tabs ticked.
  const { pendingMove } = await chrome.storage.session.get('pendingMove');
  if (pendingMove && Date.now() - pendingMove.at < 15000) {
    chrome.storage.session.remove('pendingMove');
    const clicked = data.tabs.find((t) => t.id === pendingMove.tabId);
    if (clicked) {
      const ids = clicked.highlighted ? data.tabs.filter((t) => t.highlighted).map((t) => t.id) : [clicked.id];
      return openSort(ids, true);
    }
  }
  render();
  $('#q').focus();
})();
