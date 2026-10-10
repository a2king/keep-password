chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || message.type !== "discover") {
    return false;
  }

  let port;
  try {
    port = chrome.runtime.connectNative("com.keeppassword.host");
  } catch (error) {
    sendResponse({ type: "error", message: String(error) });
    return false;
  }

  let settled = false;
  const finish = (response) => {
    if (settled) {
      return;
    }
    settled = true;
    sendResponse(response);
    try {
      port.disconnect();
    } catch (_error) {
      // 宿主可能已经退出。
    }
  };

  port.onMessage.addListener((response) => finish(response));
  port.onDisconnect.addListener(() => {
    const reason = chrome.runtime.lastError && chrome.runtime.lastError.message;
    finish({ type: "error", message: reason || "本机消息已断开。" });
  });
  port.postMessage({
    type: "discover",
    url: message.url,
    title: message.title
  });
  return true;
});

function requestFill(tabId) {
  if (!tabId) {
    return;
  }
  chrome.tabs.sendMessage(tabId, { type: "fill-request" }, () => {
    chrome.runtime.lastError;
  });
}

chrome.action.onClicked.addListener((tab) => requestFill(tab.id));
chrome.commands.onCommand.addListener((command) => {
  if (command !== "fill-password") {
    return;
  }
  chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
    if (tabs[0]) {
      requestFill(tabs[0].id);
    }
  });
});
