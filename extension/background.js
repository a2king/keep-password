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
    message = "浏览器还没有连上 Keep Password。请先打开桌面端，然后完全退出并重新打开 Edge 后再试。";
  }
  message = message || "无法完成填充，请检查 Keep Password 是否已启动。";
  const success = response && ["filled", "candidates", "ready"].includes(response.type);
  chrome.action.setBadgeText({ tabId, text: response && response.type === "candidates" ? "…" : success ? "OK" : "!" });
  chrome.action.setBadgeBackgroundColor({ tabId, color: success ? "#287b46" : "#b44335" });
  const title = response && response.type === "candidates"
    ? "请选择账号并输入短密钥授权填入。"
    : message;
  chrome.action.setTitle({ tabId, title: `Keep Password：${title}` });
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message && message.type === "password-focused" && sender.tab) {
    // 只记录最后聚焦的 frame，供工具栏按钮和快捷键使用；候选由内容脚本直接查询。
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
  if (!message || !["discover", "discover-candidates", "fill-candidate"].includes(message.type)) {
    return false;
  }

  if (!sender.tab || !sender.url || !/^https?:\/\//i.test(sender.url)) {
    sendResponse({ type: "error", message: "只能为 HTTP 或 HTTPS 登录页面填充。" });
    return false;
  }

  sendNative({
    ...message,
    type: message.type,
    url: sender.url,
    title: message.title
  }, sendResponse);
  return true;
});

// 常驻 native port：宿主进程只启动一次，之后每条请求只是一次管道读写。
// 打开的 native port 也让 MV3 service worker 保持唤醒，空闲一段时间后主动断开。
const NATIVE_HOST = "com.keeppassword.host";
const NATIVE_IDLE_MS = 5 * 60 * 1000;
// 宿主到桌面端的接收超时是 120 秒，这里稍长一点，避免请求永远挂起。
const NATIVE_TIMEOUT_MS = 130 * 1000;
let native = null;
let nextRequestId = 1;
let nativeIdleTimer = null;

function openNative() {
  const state = { port: chrome.runtime.connectNative(NATIVE_HOST), pending: new Map(), legacy: false };
  state.port.onMessage.addListener((response) => onNativeMessage(state, response));
  state.port.onDisconnect.addListener(() => onNativeDisconnect(state));
  return state;
}

function sendNative(message, callback, retried = false) {
  clearTimeout(nativeIdleTimer);
  let state;
  try {
    state = native || (native = openNative());
  } catch (error) {
    native = null;
    callback({ type: "error", message: String(error) });
    return;
  }
  const requestId = nextRequestId++;
  const entry = { message, callback, retried, timer: null };
  entry.timer = setTimeout(() => {
    if (state.pending.delete(requestId)) {
      callback({ type: "error", message: "Keep Password 客户端没有响应。" });
      scheduleNativeIdle();
    }
  }, NATIVE_TIMEOUT_MS);
  state.pending.set(requestId, entry);
  try {
    state.port.postMessage({ ...message, requestId });
  } catch (error) {
    // port 已失效但 onDisconnect 还没到：换一个新 port 重试一次。
    clearTimeout(entry.timer);
    state.pending.delete(requestId);
    if (native === state) {
      native = null;
    }
    if (retried) {
      callback({ type: "error", message: String(error) });
    } else {
      sendNative(message, callback, true);
    }
  }
}

function onNativeMessage(state, response) {
  let requestId = response && response.requestId;
  if (requestId === undefined) {
    // 旧版宿主不回 requestId，且回复一条后就退出：按发送顺序配对，剩下的在断开时重发。
    state.legacy = true;
    requestId = state.pending.keys().next().value;
  }
  const entry = state.pending.get(requestId);
  if (!entry) {
    return;
  }
  state.pending.delete(requestId);
  clearTimeout(entry.timer);
  if (response && typeof response === "object") {
    delete response.requestId;
  }
  entry.callback(response);
  scheduleNativeIdle();
}

function onNativeDisconnect(state) {
  const reason = chrome.runtime.lastError && chrome.runtime.lastError.message;
  if (native === state) {
    native = null;
  }
  const left = Array.from(state.pending.values());
  state.pending.clear();
  for (const entry of left) {
    clearTimeout(entry.timer);
    if (state.legacy && !entry.retried) {
      sendNative(entry.message, entry.callback, true);
    } else {
      entry.callback({ type: "error", message: reason || "本机消息已断开。" });
    }
  }
}

function scheduleNativeIdle() {
  clearTimeout(nativeIdleTimer);
  if (!native || native.pending.size > 0) {
    return;
  }
  const state = native;
  nativeIdleTimer = setTimeout(() => {
    if (native === state && state.pending.size === 0) {
      native = null;
      try {
        state.port.disconnect();
      } catch (_error) {
        // 宿主可能已经退出。
      }
    }
  }, NATIVE_IDLE_MS);
}

function requestFill(tabId, automatic = false) {
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
    chrome.tabs.sendMessage(tabId, { type: "fill-request", automatic }, target, (response) => {
      const error = chrome.runtime.lastError;
      showResult(tabId, error
        ? { type: "error", message: "无法访问当前密码框，请刷新登录网页并重新点击密码框。浏览器内部页面无法填充。" }
        : response);
    });
  });
}

chrome.tabs.onRemoved.addListener((tabId) => {
  chrome.storage.session.remove(focusKey(tabId));
});
chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status === "loading") {
    chrome.storage.session.remove(focusKey(tabId));
  }
});

chrome.action.onClicked.addListener((tab) => requestFill(tab.id, false));
chrome.commands.onCommand.addListener((command) => {
  if (command !== "fill-password") {
    return;
  }
  chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
    if (tabs[0]) {
      requestFill(tabs[0].id, false);
    }
  });
});
