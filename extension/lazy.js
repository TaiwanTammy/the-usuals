// A tab you haven't looked at yet. It shows the real page's title and icon in the tab bar and costs almost
// nothing; the moment you click the tab, it turns into the real page.
const params = new URLSearchParams(location.search);
const url = params.get('u') || '';
const title = params.get('t') || url;
document.title = title;
document.getElementById('icon').href = favicon(url);

// Chrome records this page in your history. Take it back out so history only shows real pages.
const forget = () => chrome.history.deleteUrl({ url: location.href }).catch(() => {});
setTimeout(forget, 1500);

function showCard(problem) {
  document.getElementById('title').textContent = title;
  document.getElementById('where').textContent = problem || url;
  document.getElementById('card').hidden = false;
}

function open() {
  forget();
  sessionStorage.setItem('opened', '1');
  // Replacing (not navigating) means Back never lands on this page.
  if (/^(https?|ftp):/i.test(url)) return location.replace(url);
  // chrome:// and file:// pages can only be opened through the tabs API.
  chrome.tabs.getCurrent((tab) =>
    chrome.tabs.update(tab.id, { url }, () => {
      if (!chrome.runtime.lastError) return;
      showCard(
        url.startsWith('file:')
          ? 'Chrome blocked this file. In chrome://extensions, open Details on Sessions and turn on "Allow access to file URLs".'
          : chrome.runtime.lastError.message
      );
    })
  );
}

function whenVisible() {
  if (document.visibilityState !== 'visible') return;
  document.removeEventListener('visibilitychange', whenVisible);
  open();
}

document.getElementById('open').addEventListener('click', open);

if (!url || /^javascript:/i.test(url)) showCard();
else if (sessionStorage.getItem('opened')) showCard(); // you came back here with the Back button
else {
  document.addEventListener('visibilitychange', whenVisible);
  whenVisible();
}
