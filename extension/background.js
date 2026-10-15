function focusKey(tabId) {
  return `password-frame-${tabId}`;
}

function showResult(tabId, response) {
  const messages = {
    filled: "已填入登录信息。",
    locked: "请先解锁 Keep Password，再重新填充。",
    noMatch: "没有匹配的网站记录，请检查条目中的网站地址。",
    cancelled: "已取消填充。"
  };
  let message = response && (response.message || messages[response.type]);
  if (message && /native messaging host not found|specified native messaging host|access to the specified native messaging host/i.test(message)) {
    message = "浏览器尚未连接 Keep Password，请按 extension/README.md 注册本机宿主和当前扩展 ID。";
  }
  message = message || "无法完成填充，请检查 Keep Password 是否已启动。";
  const success = response && response.type === "filled";
  chrome.action.setBadgeText({ tabId, text: success ? "OK" : "!" });
  chrome.action.setBadgeBackgroundColor({ tabId, color: success ? "#287b46" : "#b44335" });
  chrome.action.setTitle({ tabId, title: `Keep Password：${message}` });
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message && message.type === "password-focused" && sender.tab) {
    // Session storage survives Manifest V3 worker suspension, but contains no credentials.
    const target = { frameId: sender.frameId };
    if (sender.documentId) {
      target.documentId = sender.documentId;
    }
    chrome.storage.session.set({ [focusKey(sender.tab.id)]: target }, () => {
      sendResponse({ type: chrome.runtime.lastError ? "error" : "ready" });
    });
    return true;
  }
  if (!message || message.type !== "discover") {
    return false;
  }

  if (!sender.tab || !sender.url || !/^https?:\/\//i.test(sender.url)) {
    sendResponse({ type: "error", message: "只能为 HTTP 或 HTTPS 登录页面填充。" });
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
  try {
    port.postMessage({
      type: "discover",
      url: sender.url,
      title: message.title
    });
  } catch (error) {
    finish({ type: "error", message: String(error) });
  }
  return true;
});

function requestFill(tabId) {
  if (!Number.isInteger(tabId)) {
    return;
  }
  chrome.storage.session.get(focusKey(tabId), (stored) => {
    if (chrome.runtime.lastError) {
      showResult(tabId, { type: "error", message: "无法读取聚焦的密码框，请刷新网页后重试。" });
      return;
    }
    const target = stored[focusKey(tabId)] || { frameId: 0 };
    // Send to one document only. Broadcasting to all frames can open multiple prompts.
    chrome.tabs.sendMessage(tabId, { type: "fill-request" }, target, (response) => {
      const error = chrome.runtime.lastError;
      showResult(tabId, error
        ? { type: "error", message: "无法访问当前密码框，请刷新登录网页并重新点击密码框。浏览器内部页面无法填充。" }
        : response);
    });
  });
}

chrome.tabs.onRemoved.addListener((tabId) => chrome.storage.session.remove(focusKey(tabId)));
chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status === "loading") {
    chrome.storage.session.remove(focusKey(tabId));
  }
});

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
