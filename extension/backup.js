const status = (t) => (document.getElementById('status').textContent = t);

document.getElementById('export').addEventListener('click', async () => {
  const st = await chrome.runtime.sendMessage({ type: 'export' });
  const file = {
    exportedAt: new Date().toISOString(),
    sessions: st.sessions.map(({ name, tabs, groups }) => ({
      name,
      groups: groups || [],
      tabs: tabs.map(({ url, title, g }) => (g == null ? { url, title } : { url, title, g })),
    })),
    pinned: st.pinned,
  };
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([JSON.stringify(file, null, 2)], { type: 'application/json' }));
  a.download = `sessions-backup-${new Date().toISOString().slice(0, 10)}.json`;
  a.click();
  status(`Downloaded ${file.sessions.length} sessions.`);
});

document.getElementById('file').addEventListener('change', async (e) => {
  const f = e.target.files[0];
  if (!f) return;
  try {
    const json = JSON.parse(await f.text());
    const list = Array.isArray(json) ? json : json.sessions;
    const added = await chrome.runtime.sendMessage({ type: 'import', sessions: list || [] });
    status(`Added ${added} session${added === 1 ? '' : 's'}.`);
  } catch {
    status("That file isn't a Sessions backup.");
  }
  e.target.value = '';
});
