// Sessions: one project's tabs open at a time. Pinned tabs belong to no session and are never touched by a switch.
//
// chrome.storage.local:
//   s:<id>       one session: { id, name, tabs: [{ url, title, active }], createdAt, updatedAt, openedAt }
//   currentId    the session whose tabs are in the bound window right now
//   windowId     the one Chrome window sessions live in
//   pinned       [{ url, title }] pinned sites to bring back if Chrome forgets them after a restart
// chrome.storage.session (cleared when Chrome quits or the extension reloads):
//   ready        true once the bound window is set up; autosave waits for it, so a half-restored window is never saved
//   pendingMove  a tab right-clicked with "New session…", waiting for the menu to name the session
//   menuSig      what the right-click menus were last built from
//
// Tabs you haven't looked at yet are placeholders (lazy.html) that load the real page when clicked, so a
// 25-tab session appears at once instead of loading 25 pages.

importScripts('common.js');

const K = (id) => 's:' + id;
let saveTimer = null;
let attachTimer = null;
let menuTimer = null;
let badgeTimer = null;

// ---------- storage ----------

// Version 1 kept every session under one "sessions" key; now each has its own key so autosave writes one small session.
async function migrateV1() {
  const { sessions } = await chrome.storage.local.get('sessions');
  if (!sessions) return;
  const out = {};
  for (const s of Object.values(sessions)) out[K(s.id)] = s;
  await chrome.storage.local.set(out);
  await chrome.storage.local.remove('sessions');
}
const migrated = migrateV1();

// Everything that reads and rewrites sessions or moves tabs runs one at a time, so a switch, a move and an
// autosave can never interleave and overwrite each other. If one ever gets stuck (a busy or overloaded Chrome
// that never answers), the line moves on after 15 seconds instead of freezing autosave for the rest of the day.
let chain = migrated.catch(console.error);
function serial(fn) {
  const run = chain.then(() =>
    Promise.race([fn(), new Promise((_, stop) => setTimeout(() => stop(new Error('Sessions: step timed out')), 15000))])
  );
  chain = run.catch(console.error);
  return run;
}

async function meta() {
  const m = await chrome.storage.local.get(['currentId', 'windowId', 'pinned']);
  return { currentId: m.currentId || null, windowId: m.windowId ?? null, pinned: m.pinned || [] };
}

async function getSession(id) {
  if (!id) return null;
  return (await chrome.storage.local.get(K(id)))[K(id)] || null;
}

const putSession = (s) => chrome.storage.local.set({ [K(s.id)]: s });

async function allSessions() {
  const all = await chrome.storage.local.get(null);
  return Object.keys(all)
    .filter((k) => k.startsWith('s:'))
    .map((k) => all[k]);
}

const newId = () => Date.now().toString(36) + Math.random().toString(36).slice(2, 7);

function newSession(name) {
  const now = Date.now();
  return { id: newId(), name, tabs: [], createdAt: now, updatedAt: now, openedAt: now };
}

async function isReady() {
  return !!(await chrome.storage.session.get('ready')).ready;
}

async function windowExists(id) {
  if (id == null) return false;
  try {
    await chrome.windows.get(id);
    return true;
  } catch {
    return false;
  }
}

// Gmail u/0 and u/1 are different pinned sites, so match on host plus the first path pieces.
function pinKey(u) {
  try {
    const x = new URL(u);
    return x.host + '/' + x.pathname.split('/').filter(Boolean).slice(0, 3).join('/');
  } catch {
    return u;
  }
}

// ---------- autosave ----------

async function snapshot() {
  if (!(await isReady())) return;
  const m = await meta();
  const cur = await getSession(m.currentId);
  if (!cur || !(await windowExists(m.windowId))) return;

  const tabs = await chrome.tabs.query({ windowId: m.windowId });
  const before = new Map(cur.tabs.map((t) => [t.url, t.title]));

  // Tab groups: remember each group's name, color and folded state; each tab points at its group by number.
  const groups = [];
  const groupAt = new Map();
  for (const t of tabs) {
    if (!chrome.tabGroups || t.pinned || t.groupId < 0 || groupAt.has(t.groupId)) continue;
    const g = await chrome.tabGroups.get(t.groupId).catch(() => null);
    if (!g) continue;
    groupAt.set(t.groupId, groups.length);
    groups.push({ title: g.title || '', color: g.color, collapsed: g.collapsed });
  }

  const loose = [];
  for (const t of tabs) {
    if (t.pinned) continue;
    const r = realTab(t);
    if (!worthSaving(r.url)) continue;
    // While a page is still loading its title is just the address, so keep the title we already had.
    const fresh = isLazy(t) || (t.status === 'complete' && r.title && r.title !== r.url);
    const entry = { url: r.url, title: fresh ? r.title : before.get(r.url) || r.title || r.url, active: t.active };
    if (groupAt.has(t.groupId)) entry.g = groupAt.get(t.groupId);
    loose.push(entry);
  }
  if (JSON.stringify([loose, groups]) !== JSON.stringify([cur.tabs, cur.groups || []])) {
    cur.tabs = loose;
    cur.groups = groups;
    cur.updatedAt = Date.now();
    await putSession(cur);
  }

  // Pinned sites keep the address they were first pinned at, so a restart reopens the inbox, not one email.
  const known = new Map(m.pinned.map((p) => [pinKey(p.url), p]));
  const pinned = tabs
    .filter((t) => t.pinned)
    .map(realTab)
    .filter((r) => worthSaving(r.url))
    .map((r) => known.get(pinKey(r.url)) || { url: r.url, title: r.title });
  if (JSON.stringify(pinned) !== JSON.stringify(m.pinned)) await chrome.storage.local.set({ pinned });
}

function saveSoon() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => serial(snapshot), 400);
}

// ---------- putting tabs in the window ----------

// One request per tab: Chrome refuses a whole batch if any one tab in it has already closed.
function closeTabs(ids) {
  for (const id of ids) chrome.tabs.remove(id).catch(() => {});
}

// Chrome keeps a saved copy of every tab group in its Tab Groups menu. Closing a group's tabs leaves that copy
// behind, so a session that rebuilds its groups each time it opens would pile up duplicates there. Taking the
// tabs out of their group first makes Chrome delete its copy; the session keeps its own and rebuilds the group.
// Call this right before closeTabs: Chrome handles the two in the order they're sent.
function ungroupTabs(tabs) {
  for (const t of tabs) if (t.groupId >= 0) chrome.tabs.ungroup(t.id).catch(() => {});
}

const GROUP_COLORS = ['grey', 'blue', 'red', 'yellow', 'green', 'pink', 'purple', 'cyan', 'orange'];

// Put tabs back into their groups with the same name, color and folded state. `placed` pairs each tab Chrome
// made with the saved tab it stands for. A group already in the window with the same name and color is reused,
// so a tab that comes back on its own (after an update) rejoins its group instead of starting a second one.
async function regroup(windowId, placed, groups) {
  if (!groups?.length || !chrome.tabGroups) return;
  const existing = await chrome.tabGroups.query({ windowId }).catch(() => []);
  for (let gi = 0; gi < groups.length; gi++) {
    const members = placed.filter((p) => p.tab && p.saved.g === gi);
    if (!members.length) continue;
    const g = groups[gi];
    const color = GROUP_COLORS.includes(g.color) ? g.color : 'grey';
    const same = existing.find((x) => (x.title || '') === (g.title || '') && x.color === color);
    try {
      const tabIds = members.map((p) => p.tab.id);
      if (same) {
        await chrome.tabs.group({ groupId: same.id, tabIds });
      } else {
        const groupId = await chrome.tabs.group({ tabIds, createProperties: { windowId } });
        // A folded group can't hold the tab you're looking at.
        const holdsActive = members.some((p) => p.tab.active);
        await chrome.tabGroups.update(groupId, { title: g.title || '', color, collapsed: !!g.collapsed && !holdsActive });
      }
    } catch (e) {
      console.warn('Could not rebuild group', g.title, e);
    }
  }
}

function createTab(windowId, t, { index, active = false } = {}) {
  // Only the tab you'll look at loads for real; chrome:// and file:// pages go through the placeholder,
  // which knows how to open them.
  const real = active && /^https?:/i.test(t.url);
  return chrome.tabs.create({ windowId, index, active, url: real ? t.url : lazyUrl(t) }).catch((e) => {
    console.warn('Could not open', t.url, e);
    return null;
  });
}

// Lay a session out in the window: the tab you'll look at appears first, the old tabs go in the same
// moment, then the rest fill in around it. Chrome handles these requests in the order they're sent, so
// nothing here waits for Chrome's replies: the next step already sees the new tabs.
async function layOut(windowId, list, { closing = [], focusUrl, base = 0, groups = [] }) {
  let a = focusUrl ? list.findIndex((t) => t.url === focusUrl) : -1;
  if (a < 0) a = Math.max(0, list.findIndex((t) => t.active));
  const made = new Array(list.length);
  const first = list.length ? (made[a] = createTab(windowId, list[a], { active: true })) : chrome.tabs.create({ windowId, active: true });
  // Not waited on either: a page asking "Leave site?" must not hold up the switch.
  ungroupTabs(closing);
  closeTabs(closing.map((t) => t.id));
  list.forEach((t, i) => {
    if (i !== a) made[i] = createTab(windowId, t, { index: base + i });
  });
  await first;
  // Only a session with groups waits for its tabs: grouping needs them to exist.
  if (groups.length && list.some((t) => t.g != null)) {
    const tabs = await Promise.all(made);
    await regroup(windowId, list.map((saved, i) => ({ saved, tab: tabs[i] })), groups);
  }
}

// Tie sessions to a window: bring back pinned sites Chrome forgot, and any of the session's tabs that
// didn't come back (after a restart, or after an update, which closes every placeholder tab).
async function _attach(windowId) {
  try {
    const m = await meta();
    let cur = await getSession(m.currentId);
    if (!cur) {
      // First run: whatever is open right now becomes the first session.
      cur = newSession('First session');
      await putSession(cur);
    }
    await chrome.storage.local.set({ currentId: cur.id, windowId });

    const tabs = await chrome.tabs.query({ windowId });
    const havePinned = new Set(tabs.filter((t) => t.pinned).map((t) => pinKey(realTab(t).url)));
    for (const p of m.pinned) {
      if (!havePinned.has(pinKey(p.url))) {
        await chrome.tabs.create({ windowId, url: p.url, pinned: true, active: false }).catch(() => {});
      }
    }

    const now = await chrome.tabs.query({ windowId });
    const base = now.filter((t) => t.pinned).length;
    const loose = now.filter((t) => !t.pinned);
    const open = loose.map(realTab).filter((r) => worthSaving(r.url));
    if (!cur.tabs.length) return;
    if (!open.length) return await layOut(windowId, cur.tabs, { closing: loose, base, groups: cur.groups });

    const saved = new Set(cur.tabs.map((t) => t.url));
    if (open.filter((r) => saved.has(r.url)).length < open.length / 2) {
      // Some other window's tabs: they become their own session instead of overwriting this one.
      const when = new Date().toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
      const s = newSession(`Tabs from ${when}`);
      await putSession(s);
      await chrome.storage.local.set({ currentId: s.id });
      return;
    }

    // Same session: put back the tabs that are missing, in their old places and their old groups.
    const left = new Map();
    for (const r of open) left.set(r.url, (left.get(r.url) || 0) + 1);
    const placed = [];
    cur.tabs.forEach((t, i) => {
      const n = left.get(t.url) || 0;
      if (n) left.set(t.url, n - 1);
      else placed.push({ saved: t, tab: createTab(windowId, t, { index: base + i }) });
    });
    if (placed.some((p) => p.saved.g != null)) {
      for (const p of placed) p.tab = await p.tab;
      await regroup(windowId, placed, cur.groups);
    }
  } finally {
    await chrome.storage.session.set({ ready: true });
    saveSoon();
  }
}

// Chrome brings tabs back a moment after it starts, so wait before deciding what's missing.
function attachSoon(delay = 2500) {
  clearTimeout(attachTimer);
  attachTimer = setTimeout(
    () =>
      serial(async () => {
        const m = await meta();
        const exists = await windowExists(m.windowId);
        if (exists && (await isReady())) return;
        let wid = exists ? m.windowId : null;
        if (wid == null) {
          const wins = await chrome.windows.getAll({ windowTypes: ['normal'] });
          wid = (wins.find((w) => !w.incognito && w.focused) || wins.find((w) => !w.incognito))?.id;
        }
        if (wid != null) await _attach(wid);
      }),
    delay
  );
}

// The window sessions live in. If it's gone, the window you're using takes over.
async function boundWindow(fromWindowId) {
  const m = await meta();
  const exists = await windowExists(m.windowId);
  if (exists && (await isReady())) return m.windowId;
  const wid = exists ? m.windowId : fromWindowId ?? (await chrome.windows.getLastFocused()).id;
  await _attach(wid);
  return wid;
}

// ---------- actions ----------

async function _switchTo(id, fromWindowId, focusUrl) {
  const windowId = await boundWindow(fromWindowId);
  await snapshot();
  const m = await meta();
  if (id === m.currentId) return focusUrl && focusTab(windowId, focusUrl);
  const target = await getSession(id);
  if (!target) return;

  target.openedAt = Date.now();
  await chrome.storage.local.set({ [K(id)]: target, currentId: id, windowId });
  const tabs = await chrome.tabs.query({ windowId });
  const old = tabs.filter((t) => !t.pinned);
  await layOut(windowId, target.tabs, { closing: old, focusUrl, base: tabs.length - old.length, groups: target.groups });
  if (fromWindowId != null && windowId !== fromWindowId) chrome.windows.update(windowId, { focused: true }).catch(() => {});
}

async function focusTab(windowId, url) {
  const tab = (await chrome.tabs.query({ windowId })).find((t) => !t.pinned && realTab(t).url === url);
  if (tab) await chrome.tabs.update(tab.id, { active: true });
}

async function _startSession(name, fromWindowId) {
  const s = newSession(String(name || '').trim() || 'Untitled');
  s.openedAt = 0;
  await putSession(s);
  await _switchTo(s.id, fromWindowId);
}

async function _switchBack(fromWindowId) {
  const m = await meta();
  const prev = (await allSessions())
    .filter((s) => s.id !== m.currentId)
    .sort((a, b) => (b.openedAt || 0) - (a.openedAt || 0))[0];
  if (prev) await _switchTo(prev.id, fromWindowId);
}

// Move tabs out of the window into another session (an existing one, or a new one called `name`).
async function _move(tabIds, toId, name) {
  await snapshot();
  const tabs = (await Promise.all((tabIds || []).map((id) => chrome.tabs.get(id).catch(() => null)))).filter(
    (t) => t && worthSaving(realTab(t).url)
  );
  if (!tabs.length) return;
  const m = await meta();
  let target = toId ? await getSession(toId) : null;
  if (!target) {
    target = newSession(String(name || '').trim() || 'New session');
    if (toId) target.id = toId; // the menu picked the id so it can keep adding to this session straight away
    target.openedAt = 0;
  }
  if (target.id === m.currentId) return;

  // Tabs that were in a group arrive in the same group (same name and color) in the other session.
  target.groups = target.groups || [];
  const groupAt = new Map();
  for (const t of tabs) {
    if (!chrome.tabGroups || t.groupId < 0 || groupAt.has(t.groupId)) continue;
    const g = await chrome.tabGroups.get(t.groupId).catch(() => null);
    if (!g) continue;
    let at = target.groups.findIndex((x) => (x.title || '') === (g.title || '') && x.color === g.color);
    if (at < 0) {
      at = target.groups.length;
      target.groups.push({ title: g.title || '', color: g.color, collapsed: false });
    }
    groupAt.set(t.groupId, at);
  }

  const have = new Set(target.tabs.map((t) => t.url));
  for (const t of tabs) {
    const r = realTab(t);
    if (have.has(r.url)) continue;
    have.add(r.url);
    const entry = { url: r.url, title: r.title, active: false };
    if (groupAt.has(t.groupId)) entry.g = groupAt.get(t.groupId);
    target.tabs.push(entry);
  }
  target.updatedAt = Date.now();
  await putSession(target);

  // Never leave a window with nothing but pinned tabs in it.
  const moving = new Set(tabs.map((t) => t.id));
  for (const wid of new Set(tabs.map((t) => t.windowId))) {
    const rest = (await chrome.tabs.query({ windowId: wid })).filter((t) => !t.pinned && !moving.has(t.id));
    if (!rest.length) await chrome.tabs.create({ windowId: wid, active: true });
  }
  ungroupTabs(tabs);
  closeTabs([...moving]);
  flashBadge('+' + tabs.length);
}

// ---------- one tab inside a session, from the menu's tab list ----------

// The real tab in the sessions window that a row in the menu stands for.
async function liveTab(m, tabId, url) {
  if (!(await windowExists(m.windowId))) return null;
  const tabs = (await chrome.tabs.query({ windowId: m.windowId })).filter((t) => !t.pinned);
  return tabs.find((t) => t.id === tabId) || tabs.find((t) => realTab(t).url === url) || null;
}

// Take one tab out of a saved session. `i` is where the menu saw it; the address double-checks it.
function takeSaved(s, i, url) {
  if (s.tabs[i]?.url !== url) i = s.tabs.findIndex((t) => t.url === url);
  if (i < 0) return null;
  const [t] = s.tabs.splice(i, 1);
  return { t, group: t.g != null ? s.groups?.[t.g] || null : null };
}

async function closeLive(tab) {
  const rest = (await chrome.tabs.query({ windowId: tab.windowId })).filter((t) => !t.pinned && t.id !== tab.id);
  if (!rest.length) await chrome.tabs.create({ windowId: tab.windowId, active: true });
  ungroupTabs([tab]);
  closeTabs([tab.id]);
}

async function _removeTab(sid, i, url, tabId) {
  const m = await meta();
  if (sid === m.currentId) {
    const tab = await liveTab(m, tabId, url);
    if (tab) return closeLive(tab); // autosave records it
  }
  const s = await getSession(sid);
  if (s && takeSaved(s, i, url)) {
    s.updatedAt = Date.now();
    await putSession(s);
  }
}

async function _moveTab(from, i, url, tabId, to) {
  if (!to || from === to) return;
  const m = await meta();
  if (from === m.currentId) {
    const tab = await liveTab(m, tabId, url);
    if (tab) return _move([tab.id], to);
  }
  const src = await getSession(from);
  const dest = await getSession(to);
  if (!src || !dest) return;
  const took = takeSaved(src, i, url);
  if (!took) return;
  src.updatedAt = Date.now();
  await putSession(src);
  const entry = { url: took.t.url, title: took.t.title, active: false };

  // Into the session that's open: it shows up as a tab in the window, in its group, and autosave records it.
  if (to === m.currentId && (await windowExists(m.windowId))) {
    if (await liveTab(m, null, entry.url)) return;
    const tab = await createTab(m.windowId, entry);
    if (tab && took.group) await regroup(m.windowId, [{ saved: { g: 0 }, tab }], [took.group]);
    return;
  }

  if (dest.tabs.some((t) => t.url === entry.url)) return;
  if (took.group) {
    dest.groups = dest.groups || [];
    const g = took.group;
    let at = dest.groups.findIndex((x) => (x.title || '') === (g.title || '') && x.color === g.color);
    if (at < 0) {
      at = dest.groups.length;
      dest.groups.push({ title: g.title || '', color: g.color, collapsed: false });
    }
    entry.g = at;
  }
  dest.tabs.push(entry);
  dest.updatedAt = Date.now();
  await putSession(dest);
}

// The right-clicked tab, or every selected tab if you right-clicked one of a Ctrl-click selection (same as Chrome's own menu).
async function pickedTabs(tab) {
  if (!tab) return [];
  const selected = await chrome.tabs.query({ windowId: tab.windowId, highlighted: true });
  return selected.some((t) => t.id === tab.id) ? selected : [tab];
}

async function _rename(id, name) {
  const s = await getSession(id);
  if (!s || !String(name || '').trim()) return;
  s.name = String(name).trim();
  await putSession(s);
}

async function _remove(id) {
  const m = await meta();
  if (id === m.currentId) return; // switch away first; the menu doesn't offer this
  await chrome.storage.local.remove(K(id));
}

async function _unpin(url) {
  const m = await meta();
  const key = pinKey(url);
  await chrome.storage.local.set({ pinned: m.pinned.filter((p) => pinKey(p.url) !== key) });
  if (await windowExists(m.windowId)) {
    const tabs = await chrome.tabs.query({ windowId: m.windowId, pinned: true });
    closeTabs(tabs.filter((t) => pinKey(realTab(t).url) === key).map((t) => t.id));
  }
}

async function _pinActive(windowId) {
  const [tab] = await chrome.tabs.query({ windowId, active: true });
  if (tab && !tab.pinned) await chrome.tabs.update(tab.id, { pinned: true });
  await snapshot();
}

// Backup page: add sessions from a file, never overwriting existing ones.
async function _import(list) {
  const names = new Set((await allSessions()).map((s) => s.name));
  const out = {};
  for (const s of list || []) {
    if (!s || !Array.isArray(s.tabs)) continue;
    let name = String(s.name || 'Imported');
    if (names.has(name)) name += ' (imported)';
    names.add(name);
    const n = newSession(name);
    n.openedAt = 0;
    n.groups = (Array.isArray(s.groups) ? s.groups : []).map((g) => ({
      title: String(g?.title || ''),
      color: GROUP_COLORS.includes(g?.color) ? g.color : 'grey',
      collapsed: !!g?.collapsed,
    }));
    n.tabs = s.tabs
      .filter((t) => t && t.url)
      .map((t) => {
        const entry = { url: String(t.url), title: String(t.title || t.url), active: false };
        if (Number.isInteger(t.g) && t.g >= 0 && t.g < n.groups.length) entry.g = t.g;
        return entry;
      });
    out[K(n.id)] = n;
  }
  await chrome.storage.local.set(out);
  return Object.keys(out).length;
}

async function _export() {
  return { sessions: await allSessions(), pinned: (await meta()).pinned };
}

function flashBadge(text) {
  chrome.action.setBadgeBackgroundColor({ color: '#2563eb' });
  chrome.action.setBadgeTextColor?.({ color: '#ffffff' });
  chrome.action.setBadgeText({ text });
  clearTimeout(badgeTimer);
  badgeTimer = setTimeout(() => chrome.action.setBadgeText({ text: '' }), 2000);
}

// ---------- The Usuals: the sidebar app on the desktop ----------
//
// If The Usuals is installed, Chrome starts its small relay program and keeps a line open to it. Over that line
// the sidebar gets the session list (to show and switch from the sidebar) and receives tabs moved to it.

const USUALS = 'com.theusuals.bridge';
let usuals = null; // the open line, or null
let usualsName = null; // set while the sidebar is running: it tells us what it's called right now
let usualsRetry = null;
let stateTimer = null;
const sentIcons = new Set(); // sites whose icon the sidebar already has

function connectUsuals() {
  clearTimeout(usualsRetry);
  if (usuals) return;
  let port;
  try {
    port = chrome.runtime.connectNative(USUALS);
  } catch {
    usualsRetry = setTimeout(connectUsuals, 15000);
    return;
  }
  usuals = port;
  chrome.storage.session.set({ usuals: null }); // until the sidebar says hello
  port.onMessage.addListener(onUsualsMessage);
  port.onDisconnect.addListener(() => {
    void chrome.runtime.lastError; // "host not found" just means The Usuals isn't installed
    usuals = null;
    usualsName = null;
    sentIcons.clear();
    chrome.storage.session.set({ usuals: null });
    menusSoon();
    usualsRetry = setTimeout(connectUsuals, 15000);
  });
}

function onUsualsMessage(msg) {
  if (msg?.type === 'hello') {
    usualsName = String(msg.name || 'The Usuals');
    sentIcons.clear();
    chrome.storage.session.set({ usuals: usualsName });
    menusSoon();
    sendStateSoon(0);
  } else if (msg?.type === 'bye') {
    // The relay is still there but the sidebar quit.
    usualsName = null;
    chrome.storage.session.set({ usuals: null });
    menusSoon();
  } else if (msg?.type === 'switch' && msg.id) {
    serial(async () => {
      await _switchTo(msg.id);
      const { windowId } = await meta();
      if (windowId != null) await chrome.windows.update(windowId, { focused: true }).catch(() => {});
    });
  }
}

function sendStateSoon(delay = 600) {
  if (!usualsName) return;
  clearTimeout(stateTimer);
  stateTimer = setTimeout(sendState, delay);
}

async function iconData(url, size = 16) {
  try {
    const res = await fetch(chrome.runtime.getURL(`/_favicon/?pageUrl=${encodeURIComponent(url)}&size=${size}`));
    const bytes = new Uint8Array(await res.arrayBuffer());
    let bin = '';
    for (const b of bytes) bin += String.fromCharCode(b);
    return `data:${res.headers.get('content-type') || 'image/png'};base64,${btoa(bin)}`;
  } catch {
    return null;
  }
}

async function sendState() {
  if (!usuals) return;
  const m = await meta();
  const sessions = (await allSessions()).sort((a, b) =>
    a.id === m.currentId ? -1 : b.id === m.currentId ? 1 : (b.openedAt || 0) - (a.openedAt || 0)
  );
  const icons = {};
  for (const s of sessions)
    for (const t of s.tabs) {
      const h = hostOf(t.url);
      if (sentIcons.has(h) || h in icons) continue;
      icons[h] = await iconData(t.url, 32);
    }
  if (!usuals) return;
  usuals.postMessage({
    type: 'sessions',
    currentId: m.currentId,
    sessions: sessions.map((s) => ({ id: s.id, name: s.name, openedAt: s.openedAt || 0, hosts: s.tabs.map((t) => hostOf(t.url)) })),
    icons,
  });
  for (const h of Object.keys(icons)) sentIcons.add(h);
}

// "Inbox (3) - you@gmail.com - Gmail" becomes "Gmail": the site's own name is usually the last piece of the title.
function appName(title, url) {
  const parts = String(title || '').split(/\s+[-|–·]\s+/).filter(Boolean);
  return (parts.length > 1 ? parts[parts.length - 1] : parts[0] || hostOf(url)).slice(0, 48);
}

// Hand a tab to The Usuals as a sidebar app, then close it here (and stop pinning it, if it was pinned).
async function _moveToUsuals(tab) {
  if (!usualsName || !tab) return;
  const r = realTab(tab);
  if (!worthSaving(r.url)) return;
  usuals.postMessage({ type: 'addApp', url: r.url, name: appName(r.title, r.url), icon: await iconData(r.url, 64) });
  if (tab.pinned) return _unpin(r.url);
  await closeLive(tab);
}

async function _pinsToUsuals() {
  if (!usualsName) return;
  for (const p of (await meta()).pinned) {
    usuals.postMessage({ type: 'addApp', url: p.url, name: appName(p.title, p.url), icon: await iconData(p.url, 64) });
    await _unpin(p.url);
  }
}

// ---------- right-click menus: on a tab, and on the toolbar icon ----------

function menusSoon() {
  clearTimeout(menuTimer);
  menuTimer = setTimeout(() => serial(buildMenus), 300);
}

async function buildMenus() {
  const m = await meta();
  const others = (await allSessions())
    .filter((s) => s.id !== m.currentId)
    .sort((a, b) => (b.openedAt || 0) - (a.openedAt || 0));
  const sig = JSON.stringify([3, usualsName, others.map((s) => [s.id, s.name])]); // bump the first number when the menu's layout changes
  if ((await chrome.storage.session.get('menuSig')).menuSig === sig) return;
  await chrome.storage.session.set({ menuSig: sig });

  await chrome.contextMenus.removeAll();
  const label = (s) => s.replace(/&/g, '&&'); // a lone & would turn into a keyboard shortcut marker
  const add = (props) => {
    try {
      chrome.contextMenus.create(props, () => void chrome.runtime.lastError);
    } catch (e) {
      console.warn('Menu item not added', props.id, e.message);
    }
  };
  for (const ctx of ['tab', 'action']) {
    const root = `root|${ctx}`;
    // Move tab to ▸ the other sessions, New session…, a line, then Pin to sidebar last: it sends the tab out of
    // Chrome entirely, so it sits apart from the session choices.
    add({ id: root, title: 'Move tab to', contexts: [ctx] });
    for (const s of others) add({ id: `to|${ctx}|${s.id}`, parentId: root, title: label(s.name), contexts: [ctx] });
    add({ id: `new|${ctx}`, parentId: root, title: 'New session…', contexts: [ctx] });
    if (usualsName) {
      // Chrome leaves real separators out of this tab submenu, so the divider is a greyed-out line item instead.
      add({ id: `sepu|${ctx}`, parentId: root, title: '──────────', enabled: false, contexts: [ctx] });
      add({ id: `usuals|${ctx}`, parentId: root, title: 'Pin to sidebar', contexts: [ctx] });
    }
  }
  if (others[0]) add({ id: 'back', title: `Back to ${label(others[0].name)}`, contexts: ['action'] });
}

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const [kind, , id] = String(info.menuItemId).split('|');
  if (kind === 'back') return serial(() => _switchBack(tab?.windowId));
  // If Chrome doesn't say which tab was right-clicked, use the tab you're on.
  tab = tab?.id >= 0 ? tab : (await chrome.tabs.query({ active: true, lastFocusedWindow: true }))[0];
  if (!tab) return;
  if (kind === 'usuals') return serial(() => _moveToUsuals(tab));
  if (kind === 'to') return serial(async () => _move((await pickedTabs(tab)).map((t) => t.id), id));
  if (kind === 'new') {
    // Open the menu so you can type the new session's name; if Chrome won't open it, name it after the tab.
    chrome.storage.session.set({ pendingMove: { tabId: tab.id, at: Date.now() } });
    chrome.action.openPopup({ windowId: tab.windowId }).catch(() =>
      serial(async () => {
        await chrome.storage.session.remove('pendingMove');
        const tabs = await pickedTabs(tab);
        await _move(tabs.map((t) => t.id), null, realTab(tab).title.slice(0, 40));
      })
    );
  }
});

chrome.commands.onCommand.addListener((cmd, tab) => {
  if (cmd === 'previous-session') serial(() => _switchBack(tab?.windowId));
});

// ---------- messages from the menu and the backup page ----------

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg.type === 'connectUsuals') {
    connectUsuals(); // opening the menu tries again right away, so a just-started sidebar shows up at once
    return false;
  }
  const w = msg.windowId;
  const jobs = {
    switch: () => _switchTo(msg.id, w, msg.focusUrl),
    start: () => _startSession(msg.name, w),
    move: () => _move(msg.tabIds, msg.to, msg.name),
    removeTab: () => _removeTab(msg.sid, msg.i, msg.url, msg.tabId),
    moveTab: () => _moveTab(msg.sid, msg.i, msg.url, msg.tabId, msg.to),
    rename: () => _rename(msg.id, msg.name),
    delete: () => _remove(msg.id),
    unpin: () => _unpin(msg.url),
    pin: () => _pinActive(w),
    pinsToUsuals: _pinsToUsuals,
  };
  if (jobs[msg.type]) {
    serial(jobs[msg.type]);
    reply({ ok: true }); // answer at once: the menu has already updated itself and shouldn't wait
    return false;
  }
  const answers = { import: () => _import(msg.sessions), export: _export };
  if (!answers[msg.type]) return false;
  serial(answers[msg.type]).then(reply, (e) => reply({ error: String(e) }));
  return true;
});

// ---------- keep the saved copy current ----------

chrome.tabs.onCreated.addListener(saveSoon);
chrome.tabs.onMoved.addListener(saveSoon);
chrome.tabs.onActivated.addListener(saveSoon);
chrome.tabs.onAttached.addListener(saveSoon);
chrome.tabs.onDetached.addListener(saveSoon);
chrome.tabs.onUpdated.addListener((_id, change) => {
  if ('url' in change || 'title' in change || 'pinned' in change || 'groupId' in change || change.status === 'complete') saveSoon();
});
if (chrome.tabGroups) {
  chrome.tabGroups.onCreated.addListener(saveSoon);
  chrome.tabGroups.onUpdated.addListener(saveSoon);
  chrome.tabGroups.onRemoved.addListener(saveSoon);
  chrome.tabGroups.onMoved.addListener(saveSoon);
}
chrome.tabs.onRemoved.addListener((_id, info) => {
  // Closing the whole window (or quitting Chrome) must not save the session as empty.
  if (!info.isWindowClosing) saveSoon();
});

chrome.windows.onRemoved.addListener((id) =>
  serial(async () => {
    if (id === (await meta()).windowId) await chrome.storage.local.set({ windowId: null });
  })
);
chrome.windows.onCreated.addListener((w) => {
  if (w.type !== 'normal' || w.incognito) return;
  serial(async () => {
    if (!(await windowExists((await meta()).windowId))) attachSoon();
  });
});

// Session added, renamed, deleted or switched: the "Move tab to" lists change.
chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== 'local') return;
  const relevant = Object.entries(changes).some(
    ([k, v]) =>
      k === 'currentId' ||
      (k.startsWith('s:') && (!v.oldValue || !v.newValue || v.oldValue.name !== v.newValue.name || v.oldValue.openedAt !== v.newValue.openedAt))
  );
  if (relevant) menusSoon();
  if (Object.keys(changes).some((k) => k === 'currentId' || k.startsWith('s:'))) sendStateSoon();
});

chrome.runtime.onStartup.addListener(() => attachSoon());
chrome.runtime.onInstalled.addListener(() => attachSoon());

// Updates install themselves: if the files on disk are a newer version than the one running, reload.
// (The version number in manifest.json is changed last in every update, after all the other files.)
async function reloadIfUpdated() {
  try {
    const res = await fetch(chrome.runtime.getURL('manifest.json'), { cache: 'no-store' });
    const onDisk = (await res.json()).version;
    if (onDisk && onDisk !== chrome.runtime.getManifest().version) chrome.runtime.reload();
  } catch {
    // A manifest caught half-written: the next wake-up checks again.
  }
}

// A backup file named import.json in the extension's folder gets its sessions added, once per version of the file.
async function importFromFolder() {
  try {
    const res = await fetch(chrome.runtime.getURL('import.json'), { cache: 'no-store' });
    if (!res.ok) return;
    const text = await res.text();
    let h = 5381;
    for (let i = 0; i < text.length; i++) h = ((h * 33) ^ text.charCodeAt(i)) >>> 0;
    const key = 'imported:' + h.toString(36);
    if ((await chrome.storage.local.get(key))[key]) return;
    const json = JSON.parse(text);
    await serial(() => _import(Array.isArray(json) ? json : json.sessions));
    await chrome.storage.local.set({ [key]: Date.now() });
  } catch {
    // No file there (the usual case), or a file caught half-written: the next wake-up checks again.
  }
}

// A file named remove.json in the extension's folder ({ remove: [{ id, url }] }) takes those saved tabs out of
// those sessions, once per version of the file. Skips the session that's open, so a live tab is never pulled.
async function removeFromFolder() {
  try {
    const res = await fetch(chrome.runtime.getURL('remove.json'), { cache: 'no-store' });
    if (!res.ok) return;
    const text = await res.text();
    let h = 5381;
    for (let i = 0; i < text.length; i++) h = ((h * 33) ^ text.charCodeAt(i)) >>> 0;
    const key = 'removed:' + h.toString(36);
    if ((await chrome.storage.local.get(key))[key]) return;
    const list = JSON.parse(text).remove || [];
    await serial(async () => {
      const m = await meta();
      const byId = {};
      for (const r of list) (byId[r.id] ||= []).push(r.url);
      for (const [id, urls] of Object.entries(byId)) {
        if (id === m.currentId) continue;
        const s = await getSession(id);
        if (!s) continue;
        for (const url of urls) takeSaved(s, -1, url);
        s.updatedAt = Date.now();
        await putSession(s);
      }
    });
    await chrome.storage.local.set({ [key]: Date.now() });
  } catch {
    // No file there (the usual case), or a file caught half-written: the next wake-up checks again.
  }
}

// Every time the background wakes: if this browser run hasn't been set up yet (fresh start, update,
// re-enable), set it up. Menus rebuild only if the session list changed.
isReady().then((ready) => ready || attachSoon());
menusSoon();
connectUsuals();
setTimeout(reloadIfUpdated, 3000);
setTimeout(importFromFolder, 4000);
setTimeout(removeFromFolder, 5000);
