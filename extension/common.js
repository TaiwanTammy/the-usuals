// Shared by the background, the menu and the placeholder page.

const EXT = chrome.runtime.getURL('');
const LAZY = chrome.runtime.getURL('lazy.html');
const BLANK = /^(chrome:\/\/newtab|chrome:\/\/new-tab-page|chrome-search:\/\/local-ntp|about:blank|edge:\/\/newtab)/;

// A placeholder tab: shows the page's title and icon, loads the real page when you click the tab.
function lazyUrl(t) {
  return LAZY + '?' + new URLSearchParams({ u: t.url, t: t.title || '' });
}

function isLazy(t) {
  return (t.pendingUrl || t.url || '').startsWith(LAZY);
}

// The page a tab stands for: a placeholder reports the page it will load, not itself.
function realTab(t) {
  const u = t.pendingUrl || t.url || '';
  if (u.startsWith(LAZY)) {
    const p = new URL(u).searchParams;
    const url = p.get('u') || '';
    return { url, title: p.get('t') || url };
  }
  return { url: u, title: t.title || u };
}

// New tab pages and the extension's own pages never go into a session.
const worthSaving = (u) => !!u && !BLANK.test(u) && !u.startsWith(EXT);

function hostOf(u) {
  try {
    return new URL(u).host || u;
  } catch {
    return u;
  }
}

function favicon(u) {
  return chrome.runtime.getURL(`/_favicon/?pageUrl=${encodeURIComponent(u)}&size=32`);
}
