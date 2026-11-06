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
  if (message && typeof message.type === "string" && message.type.startsWith("chooser-")) {
    return handleChooserMessage(message, sender, sendResponse);
  }
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
  endTabSessions(tabId);
});
chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status === "loading") {
    chrome.storage.session.remove(focusKey(tabId));
    endTabSessions(tabId);
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

// ---- 候选框会话 ----
// 密码框所在 frame 发 chooser-open：后台记下会话（标签页、frame、页面地址、候选），让顶层页面挂上
// 扩展自己的候选框 iframe。候选框页面凭一次性 nonce 连上来领取候选、提交短密钥；
// 后台用会话里记下的页面地址向桌面端请求凭据，并只把凭据送回那个 frame。
// 会话只含候选元数据（无密码），存在 storage.session 里，service worker 休眠后仍能续上。
const CHOOSER_KEY = "chooser-sessions";
const MAX_CANDIDATES = 200;
let chooserSessions = null;
const chooserPorts = new Map();

function loadSessions(callback) {
  if (chooserSessions) {
    callback(chooserSessions);
    return;
  }
  chrome.storage.session.get(CHOOSER_KEY, (stored) => {
    void chrome.runtime.lastError;
    chooserSessions = chooserSessions || (stored && stored[CHOOSER_KEY]) || {};
    callback(chooserSessions);
  });
}

function saveSessions() {
  chrome.storage.session.set({ [CHOOSER_KEY]: chooserSessions || {} }, () => void chrome.runtime.lastError);
}

function randomToken() {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (value) => value.toString(16).padStart(2, "0")).join("");
}

function cleanCandidates(list) {
  if (!Array.isArray(list)) return [];
  return list.slice(0, MAX_CANDIDATES).map((item) => ({
    id: String((item && item.id) || ""),
    accountId: String((item && item.accountId) || ""),
    name: String((item && item.name) || ""),
    username: String((item && item.username) || ""),
    domain: String((item && item.domain) || "")
  })).filter((item) => item.id && item.accountId);
}

function frameTarget(session) {
  const target = { frameId: session.frameId };
  if (session.documentId) target.documentId = session.documentId;
  return target;
}

function tellFrame(tabId, target, message, callback) {
  chrome.tabs.sendMessage(tabId, message, target, (reply) => {
    void chrome.runtime.lastError;
    if (callback) callback(reply);
  });
}
function endSession(probe, { skipFrame } = {}) {
  const session = chooserSessions && chooserSessions[probe];
  if (!session) return;
  delete chooserSessions[probe];
  saveSessions();
  const port = chooserPorts.get(probe);
  chooserPorts.delete(probe);
  if (port) {
    try {
      port.disconnect();
    } catch (_error) {
      // 候选框已经关闭。
    }
  }
  const unmount = { type: "chooser-unmount", probe };
  if (session.mountFrameId !== undefined && session.mountFrameId !== skipFrame) {
    tellFrame(session.tabId, { frameId: session.mountFrameId }, unmount);
  }
  if (session.frameId !== session.mountFrameId && session.frameId !== skipFrame) {
    tellFrame(session.tabId, frameTarget(session), unmount);
  }
}

function endTabSessions(tabId) {
  loadSessions((sessions) => {
    for (const [probe, session] of Object.entries(sessions)) {
      if (session.tabId === tabId) endSession(probe);
    }
  });
}

function openSession(sessions, probe, message, sender, sendResponse) {
  const tabId = sender.tab.id;
  if (!sender.url || !/^https?:\/\//i.test(sender.url)) {
    sendResponse({ type: "error" });
    return;
  }
  // 同一标签页同时只保留一个候选框。
  for (const [other, existing] of Object.entries(sessions)) {
    if (existing.tabId === tabId) endSession(other, { skipFrame: sender.frameId });
  }
  const session = {
    tabId, frameId: sender.frameId, documentId: sender.documentId, url: sender.url,
    title: String(message.title || ""), mode: message.mode === "username" ? "username" : "password",
    candidates: cleanCandidates(message.candidates), nonce: randomToken()
  };
  sessions[probe] = session;
  const mountHere = () => {
    session.mountFrameId = sender.frameId;
    saveSessions();
    sendResponse({ type: "mount-here", nonce: session.nonce });
  };
  if (sender.frameId === 0) {
    mountHere();
    return;
  }
  // 登录框在 iframe 里：挂到顶层页面，避免被 iframe 边界裁掉。
  tellFrame(tabId, { frameId: 0 }, { type: "chooser-mount", probe, nonce: session.nonce }, (reply) => {
    if (sessions[probe] !== session) {
      sendResponse({ type: "error" });
      return;
    }
    if (reply && reply.type === "mounted") {
      session.mountFrameId = 0;
      saveSessions();
      sendResponse({ type: "mounting" });
    } else {
      mountHere();
    }
  });
}
function handleChooserMessage(message, sender, sendResponse) {
  const tabId = sender.tab && sender.tab.id;
  const probe = typeof message.probe === "string" ? message.probe : "";
  if (!Number.isInteger(tabId) || !probe || probe.length > 64) {
    sendResponse({ type: "error" });
    return false;
  }
  loadSessions((sessions) => {
    if (message.type === "chooser-open") {
      openSession(sessions, probe, message, sender, sendResponse);
      return;
    }
    const session = sessions[probe];
    if (!session || session.tabId !== tabId) {
      sendResponse({ type: "error" });
      return;
    }
    if (message.type === "chooser-update" && sender.frameId === session.frameId) {
      session.candidates = cleanCandidates(message.candidates);
      saveSessions();
      const port = chooserPorts.get(probe);
      if (port) port.postMessage({ type: "update", candidates: session.candidates, mode: session.mode });
    } else if (message.type === "chooser-close" && (sender.frameId === session.frameId || sender.frameId === session.mountFrameId)) {
      endSession(probe, { skipFrame: sender.frameId });
    }
    sendResponse({ type: "ready" });
  });
  return true;
}

function isChooserPage(sender) {
  return !!sender && typeof sender.url === "string" && sender.url.startsWith(chrome.runtime.getURL("chooser.html"));
}

function findByNonce(sessions, nonce, tabId) {
  if (typeof nonce !== "string" || nonce.length < 16) return null;
  for (const [probe, session] of Object.entries(sessions)) {
    if (session.nonce === nonce && session.tabId === tabId) return probe;
  }
  return null;
}

function submitFill(probe, session, message, port) {
  const candidate = session.candidates.find((item) => item.id === message.entryId && item.accountId === message.accountId);
  if (!candidate) {
    port.postMessage({ type: "error", message: "账号已变化，请重新选择。" });
    return;
  }
  // 地址取自会话：是密码框所在 frame 打开候选框时浏览器报告的地址，而不是候选框页面自己说的。
  sendNative({
    type: "fill-candidate", url: session.url, title: session.title,
    entryId: candidate.id, accountId: candidate.accountId, shortKey: String(message.shortKey || "")
  }, (result) => {
    if (!result || result.type !== "fill") {
      port.postMessage({ type: "error", message: (result && result.message) || "无法完成填充。" });
      return;
    }
    const fill = { type: "chooser-fill", probe, username: result.username || "", password: result.password || "" };
    result.username = "";
    result.password = "";
    tellFrame(session.tabId, frameTarget(session), fill, (reply) => {
      fill.username = "";
      fill.password = "";
      if (reply && reply.type === "filled") {
        endSession(probe, { skipFrame: session.frameId });
      } else {
        port.postMessage({ type: "error", message: (reply && reply.message) || "登录页面或输入框已变化，请重新点击。" });
      }
    });
  });
}

chrome.runtime.onConnect.addListener((port) => {
  if (port.name !== "kp-chooser" || !isChooserPage(port.sender) || !port.sender.tab) {
    port.disconnect();
    return;
  }
  const tabId = port.sender.tab.id;
  let probe = null;
  port.onDisconnect.addListener(() => {
    if (probe && chooserPorts.get(probe) === port) chooserPorts.delete(probe);
  });
  port.onMessage.addListener((message) => {
    if (!message || typeof message !== "object") return;
    loadSessions((sessions) => {
      if (message.type === "hello") {
        const found = findByNonce(sessions, message.nonce, tabId);
        if (!found) {
          port.postMessage({ type: "denied" });
          return;
        }
        probe = found;
        chooserPorts.set(probe, port);
        const session = sessions[probe];
        port.postMessage({ type: "init", candidates: session.candidates, mode: session.mode });
        return;
      }
      const session = probe && sessions[probe];
      if (!session) return;
      if (message.type === "close") endSession(probe);
      else if (message.type === "fill") submitFill(probe, session, message, port);
    });
  });
});
