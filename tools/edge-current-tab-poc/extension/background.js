const HOST_NAME = 'com.personalassistant.edgetab';

async function getCurrentTabInfo() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });

  if (!tab || !tab.id) {
    return {
      success: false,
      error: 'No active tab found.'
    };
  }

  const result = await chrome.scripting.executeScript({
    target: { tabId: tab.id },
    func: () => {
      const getText = (selector) => Array.from(document.querySelectorAll(selector)).map(node => node.textContent?.trim()).filter(Boolean).slice(0, 20);

      const headings = Array.from(document.querySelectorAll('h1, h2, h3, h4, h5, h6')).map(node => node.textContent?.trim()).filter(Boolean).slice(0, 20);
      const links = Array.from(document.querySelectorAll('a[href]')).map(node => ({
        text: node.textContent?.trim() || '',
        href: node.href || ''
      })).filter(item => item.text || item.href).slice(0, 20);
      const buttons = Array.from(document.querySelectorAll('button, input[type="button"], input[type="submit"]')).map(node => node.value || node.textContent || node.getAttribute('aria-label') || '').filter(Boolean).slice(0, 20);
      const paragraphs = Array.from(document.querySelectorAll('p, li, td, th')).map(node => node.textContent?.trim()).filter(Boolean).slice(0, 80);

      return {
        url: document.location.href,
        title: document.title,
        text: document.body ? document.body.innerText?.trim() || '' : '',
        headings,
        links,
        buttons,
        paragraphs
      };
    }
  });

  const payload = result[0]?.result || { success: false, error: 'No result returned.' };
  return {
    success: true,
    url: tab.url || payload.url || '',
    title: payload.title || tab.title || '',
    text: payload.text || '',
    headings: payload.headings || [],
    links: payload.links || [],
    buttons: payload.buttons || [],
    paragraphs: payload.paragraphs || []
  };
}

chrome.action.onClicked.addListener(async () => {
  const payload = await getCurrentTabInfo();

  chrome.runtime.sendNativeMessage(HOST_NAME, { action: 'get_active_tab', payload }, (response) => {
    if (chrome.runtime.lastError) {
      console.error('Native messaging error:', chrome.runtime.lastError.message);
      return;
    }

    console.log('Native host response:', response);
  });
});

chrome.runtime.onMessageExternal.addListener((message, sender, sendResponse) => {
  if (message && message.action === 'get_active_tab') {
    getCurrentTabInfo().then(payload => sendResponse({ success: true, payload })).catch(error => {
      sendResponse({ success: false, error: error.message || 'Unknown error' });
    });
    return true;
  }

  sendResponse({ success: false, error: 'Unsupported action.' });
  return false;
});
