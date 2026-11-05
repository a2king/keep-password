let focused = null;
let lastRequest = 0;
let notice = null;
let noticeTimer = null;
let chooser = null;
let suppressFocusUntil = 0;
let focusVersion = 0;

function showStatus(response, silent = false) {
  if (silent) return;
  const messages = {
    filled: "已填入登录信息。",
    locked: "请先解锁 Keep Password，再重新填充。",
    noMatch: "没有匹配的网站记录，请检查条目中的网站地址。",
    cancelled: "已取消填充。"
  };
  let text = response.message || messages[response.type] || "没有可填入的记录。";
  if (/native messaging host not found|specified native messaging host|access to the specified native messaging host/i.test(text)) {
    text = "浏览器还没有连上 Keep Password。请先打开桌面端，然后完全退出并重新打开 Edge 后再试。";
  }
  if (!notice) {
    notice = document.createElement("div");
    notice.setAttribute("role", "status");
    notice.style.cssText = "position:fixed;right:20px;top:20px;z-index:2147483647;max-width:380px;padding:12px 16px;border-radius:8px;background:#202a36;color:white;font:14px/1.5 sans-serif;box-shadow:0 3px 16px #0005;pointer-events:none;";
  }
  notice.textContent = `Keep Password：${text}`;
  (document.documentElement || document.body).appendChild(notice);
  clearTimeout(noticeTimer);
  noticeTimer = setTimeout(() => notice.remove(), 7000);
}

function isPassword(input) {
  return input instanceof HTMLInputElement && input.type === "password" && input.isConnected && !input.disabled && !input.readOnly;
}

function isVisibleField(input) {
  if (!input || input.hidden) return false;
  if (typeof input.getClientRects === "function" && input.getClientRects().length === 0) return false;
  if (typeof window !== "undefined" && typeof window.getComputedStyle === "function") {
    const style = window.getComputedStyle(input);
    if (style && (style.display === "none" || style.visibility === "hidden")) return false;
  }
  return true;
}

function looksLikeLoginPage() {
  let path = "";
  try {
    path = new URL(location.href).pathname;
  } catch (_error) {
    path = String((location && (location.pathname || location.href)) || "");
  }
  return /signin|sign-in|login|log-in|identifier|\/auth(?:\/|$)/i.test(path);
}

function pageHasPassword() {
  if (!document.querySelectorAll) return false;
  return Array.from(document.querySelectorAll('input[type="password"]')).some((input) => isPassword(input) && isVisibleField(input));
}

function isIdentifier(input) {
  if (!(input instanceof HTMLInputElement) || !input.isConnected || input.disabled || input.readOnly || isPassword(input)) return false;
  const type = (input.getAttribute("type") || "text").toLowerCase();
  if (!["text", "email", "tel"].includes(type)) return false;
  if (!isVisibleField(input)) return false;
  const autocomplete = (input.getAttribute("autocomplete") || "").toLowerCase().split(/\s+/).filter(Boolean);
  const name = `${input.getAttribute("name") || ""} ${input.getAttribute("id") || ""}`.toLowerCase();
  const loginPage = looksLikeLoginPage();
  const explicitUsername = autocomplete.includes("username")
    || /(^|[^a-z])(identifier|username|loginfmt)([^a-z]|$)/.test(name);
  const emailOnLogin = loginPage && (type === "email" || autocomplete.includes("email") || name.includes("email"));
  if (!explicitUsername && !emailOnLogin) return false;
  if (!loginPage && !autocomplete.includes("username")) return false;
  return !pageHasPassword();
}

function isLoginField(input) {
  return isPassword(input) || isIdentifier(input);
}

function fillLogin(field, result) {
  if (isPassword(field)) {
    fill(usernameFor(field), result.username || "");
    fill(field, result.password || "");
    return;
  }
  fill(field, result.username || "");
}

function closeChooser() {
  if (!chooser) return;
  const current = chooser;
  chooser = null;
  current.root.remove();
}

function rememberPassword(input, automatic = true) {
  if (!isLoginField(input)) return;
  focused = input;
  focusVersion += 1;
  if (!automatic || Date.now() < suppressFocusUntil) return;
  closeChooser();
  chrome.runtime.sendMessage({ type: "password-focused", auto: true }, () => {
    void chrome.runtime.lastError;
  });
}

function usernameFor(password) {
  const form = password.form;
  const scope = form || password.parentElement || document;
  const preferred = scope.querySelector('input[autocomplete="username"], input[type="email"], input[name="username"], input[name="user"], input[name="login"]');
  if (preferred && preferred !== password && !preferred.disabled && !preferred.readOnly) return preferred;
  const inputs = Array.from((form || document).querySelectorAll("input"));
  const index = inputs.indexOf(password);
  for (let i = index - 1; i >= 0; i -= 1) {
    const input = inputs[i];
    const type = (input.getAttribute("type") || "text").toLowerCase();
    if ((type === "text" || type === "email" || type === "tel") && !input.disabled && !input.readOnly) return input;
  }
  return null;
}

function fill(input, value) {
  if (!input || !input.isConnected || input.disabled || input.readOnly) return;
  input.focus();
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set;
  setter.call(input, value);
  input.dispatchEvent(new Event("input", { bubbles: true }));
  input.dispatchEvent(new Event("change", { bubbles: true }));
}

function addElement(parent, tag, className, text) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = text;
  parent.appendChild(element);
  return element;
}

const CHOOSER_MIN_WIDTH = 260;
const CHOOSER_GAP = 8;
const CHOOSER_VISIBLE_ROWS = 5;

function hostLabels(value) {
  const text = String(value || "").trim().toLowerCase();
  if (!text) return [];
  let host = text;
  try {
    host = new URL(text.includes("://") ? text : `https://${text}`).hostname;
  } catch (_error) {
    host = text.split("/")[0].split(":")[0];
  }
  const labels = host.replace(/\.$/, "").split(".").filter(Boolean);
  return labels[0] === "www" && labels.length > 1 ? labels.slice(1) : labels;
}

function addressScore(entryHost, pageUrl) {
  const entry = hostLabels(entryHost);
  const page = hostLabels(pageUrl);
  if (entry.length === 0 || page.length === 0) return 0;
  let shared = 0;
  while (shared < entry.length && shared < page.length && entry[entry.length - 1 - shared] === page[page.length - 1 - shared]) {
    shared += 1;
  }
  const leftover = (entry.length - shared) + (page.length - shared);
  return shared * 1000 - leftover * 10 - Math.abs(entry.length - page.length);
}

function sortCandidatesByAddress(candidates, pageUrl) {
  return candidates
    .map((candidate, index) => ({ candidate, index, score: addressScore(candidate && (candidate.domain || candidate.url), pageUrl) }))
    .sort((left, right) => right.score - left.score || left.index - right.index)
    .map((item) => item.candidate);
}

// 候选框与输入框左右对齐、同宽；输入框太窄时保持左对齐但不窄于 CHOOSER_MIN_WIDTH（短密钥输入行需要空间），
// 超出视口右边时整体左移，始终留出 CHOOSER_GAP 边距。
function chooserFrame(rect, viewportWidth) {
  const inputWidth = Math.max(0, (rect.right ?? rect.left) - rect.left);
  const room = Math.max(0, viewportWidth - CHOOSER_GAP * 2);
  const width = Math.min(room, Math.max(CHOOSER_MIN_WIDTH, inputWidth));
  const left = Math.max(CHOOSER_GAP, Math.min(rect.left, viewportWidth - CHOOSER_GAP - width));
  return { left, top: rect.bottom + CHOOSER_GAP, width };
}

function positionChooser(root, password) {
  if (!password || !password.getBoundingClientRect || typeof window === "undefined") return;
  const frame = chooserFrame(password.getBoundingClientRect(), window.innerWidth);
  root.style.left = `${frame.left}px`;
  root.style.top = `${frame.top}px`;
  root.style.width = `${frame.width}px`;
  const list = chooser && chooser.list;
  const row = list && list.querySelector ? list.querySelector(".candidate") : null;
  if (!list || !row || !row.getBoundingClientRect) return;
  const rowHeight = row.getBoundingClientRect().height;
  if (rowHeight <= 0) return;
  const room = window.innerHeight - frame.top - CHOOSER_GAP;
  const header = Math.max(0, root.getBoundingClientRect().height - list.getBoundingClientRect().height);
  const visible = Math.max(rowHeight, Math.min(rowHeight * CHOOSER_VISIBLE_ROWS, room - header));
  list.style.maxHeight = `${visible}px`;
  list.style.overflowY = "auto";
}

function trackChooser() {
  if (typeof window === "undefined" || window.__kpChooserTracked) return;
  window.__kpChooserTracked = true;
  const refresh = () => {
    if (chooser && chooser.password && chooser.password.isConnected) positionChooser(chooser.root, chooser.password);
  };
  window.addEventListener("scroll", refresh, true);
  window.addEventListener("resize", refresh);
}

function showCandidates(candidates, password) {
  closeChooser();
  if (!Array.isArray(candidates) || candidates.length === 0 || !password || !password.isConnected) return;
  const parent = document.body || document.documentElement;
  if (!parent || typeof document.createElement !== "function") return;

  const root = document.createElement("div");
  root.setAttribute("role", "dialog");
  root.setAttribute("aria-label", "Keep Password 账号选择");
  root.style.cssText = "position:fixed;z-index:2147483646;opacity:0;transform:translateY(-4px) scale(.985);transition:opacity 140ms ease,transform 140ms ease;";
  const shadow = root.attachShadow ? root.attachShadow({ mode: "open" }) : root;
  const style = addElement(shadow, "style", "", `
    :host { all: initial; }
    .box { box-sizing:border-box; width:100%; overflow:hidden; border:1px solid #d8dee8; border-radius:12px; background:#fff; color:#1f2937; box-shadow:0 12px 32px rgba(15,23,42,.22); font:13px/1.4 -apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif; }
    .head { padding:13px 15px 9px; border-bottom:1px solid #edf0f4; display:flex; align-items:center; justify-content:space-between; }
    .brand { font-weight:650; font-size:14px; }
    .close { border:0; background:transparent; color:#7b8491; cursor:pointer; font-size:18px; line-height:1; padding:2px 4px; }
    .hint { padding:9px 15px 5px; color:#7b8491; font-size:12px; }
    .list { padding:4px 8px 8px; overflow-y:auto; }
    .candidate { width:100%; border:0; background:transparent; border-radius:9px; text-align:left; cursor:pointer; padding:9px 8px; display:block; }
    .candidate:hover,.candidate:focus { background:#edf5ff; outline:none; }
    .name { font-weight:600; color:#1d2939; }
    .meta { color:#637083; margin-top:2px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
    .domain { color:#8792a2; font-size:12px; margin-top:1px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
    .key { display:none; padding:10px 14px 14px; border-top:1px solid #edf0f4; }
    .key.visible { display:block; }
    .key-label { color:#667085; font-size:12px; margin-bottom:6px; }
    .key-row { display:flex; gap:7px; }
    .key-input { flex:1; min-width:0; border:1px solid #cbd5e1; border-radius:7px; padding:8px 9px; font:inherit; letter-spacing:normal; outline:none; }
    .key-input:focus { border-color:#3984e8; box-shadow:0 0 0 2px #3984e833; }
    .fill { border:0; border-radius:7px; padding:0 12px; background:#1677e8; color:#fff; cursor:pointer; font-weight:600; }
    .back { border:0; background:transparent; color:#3478c8; cursor:pointer; padding:8px 0 0; font-size:12px; }
    .error { color:#c0392b; font-size:12px; margin-top:7px; }
  `);
  void style;
  const box = addElement(shadow, "div", "box");
  const head = addElement(box, "div", "head");
  addElement(head, "div", "brand", "选择 Keep Password 账号");
  const close = addElement(head, "button", "close", "×");
  close.type = "button";
  addElement(box, "div", "hint", isPassword(password)
    ? `${candidates.length} 个匹配账号，选择后输入短密钥授权`
    : "选择账号并输入短密钥后填入用户名，然后自行点击下一步");
  const list = addElement(box, "div", "list");
  const key = addElement(box, "div", "key");
  addElement(key, "div", "key-label", "短密钥");
  const keyRow = addElement(key, "div", "key-row");
  const keyInput = addElement(keyRow, "input", "key-input");
  keyInput.type = "password";
  keyInput.autocomplete = "off";
  keyInput.placeholder = "输入短密钥";
  const fillButton = addElement(keyRow, "button", "fill", "授权填入");
  fillButton.type = "button";
  const error = addElement(key, "div", "error");
  const back = addElement(key, "button", "back", "返回账号列表");
  back.type = "button";

  chooser = { root, password, version: focusVersion, list, key, keyInput, fillButton, error, selected: null };
  close.addEventListener("click", closeChooser);
  back.addEventListener("click", () => {
    if (!chooser) return;
    chooser.selected = null;
    chooser.error.textContent = "";
    chooser.key.classList.remove("visible");
    chooser.list.style.display = "block";
  });
  fillButton.addEventListener("click", () => {
    if (chooser?.selected) requestCandidateFill(chooser.selected, chooser.keyInput.value || "");
  });
  keyInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && chooser?.selected) requestCandidateFill(chooser.selected, chooser.keyInput.value || "");
    if (event.key === "Escape") closeChooser();
  });

  const pageUrl = typeof location !== "undefined" ? location.href : "";
  for (const candidate of sortCandidatesByAddress(candidates, pageUrl)) {
    const button = addElement(list, "button", "candidate");
    button.type = "button";
    addElement(button, "div", "name", candidate.name || "未命名");
    addElement(button, "div", "meta", candidate.username || "未设置用户名");
    addElement(button, "div", "domain", candidate.domain || "未设置网站");
    button.addEventListener("click", () => {
      if (!chooser) return;
      chooser.selected = candidate;
      chooser.error.textContent = "";
      chooser.list.style.display = "none";
      chooser.key.classList.add("visible");
      chooser.keyInput.value = "";
      chooser.keyInput.focus();
    });
  }

  parent.appendChild(root);
  trackChooser();
  positionChooser(root, password);
  requestAnimationFrame(() => {
    if (root.isConnected) {
      root.style.opacity = "1";
      root.style.transform = "translateY(0) scale(1)";
    }
  });
}

function requestCandidateFill(candidate, shortKey) {
  if (!chooser || !chooser.password || !isLoginField(chooser.password)) {
    closeChooser();
    return;
  }
  const password = chooser.password;
  const requestVersion = chooser.version;
  const requestedUrl = location.href;
  chooser.fillButton.disabled = true;
  chooser.error.textContent = "正在验证…";
  chrome.runtime.sendMessage({ type: "fill-candidate", entryId: candidate.id, accountId: candidate.accountId, shortKey, url: requestedUrl, title: document.title }, (response) => {
    const error = chrome.runtime.lastError;
    const result = error ? { type: "error", message: error.message } : response;
    if (!result || result.type !== "fill") {
      if (chooser) {
        chooser.fillButton.disabled = false;
        chooser.error.textContent = result?.message || "无法完成填充。";
        chooser.keyInput.value = "";
        chooser.keyInput.focus();
      }
      return;
    }
    try {
      if (requestVersion !== focusVersion || location.href !== requestedUrl || !isLoginField(password) || focused !== password) {
        showStatus({ type: "error", message: "登录页面或输入框已变化，请重新点击。" });
        return;
      }
      suppressFocusUntil = Date.now() + 1600;
      fillLogin(password, result);
      closeChooser();
      showStatus({ type: "filled" });
    } catch (_error) {
      showStatus({ type: "error", message: "网页不允许填入当前输入框，请刷新网页后重试。" });
    } finally {
      result.username = "";
      result.password = "";
    }
  });
}

function requestCandidates(password, automatic, sendResponse) {
  const now = Date.now();
  if (now - lastRequest < 700) {
    sendResponse({ type: "ready" });
    return false;
  }
  lastRequest = now;
  const requestVersion = focusVersion;
  const requestedUrl = location.href;
  chrome.runtime.sendMessage({ type: "discover-candidates", url: requestedUrl, title: document.title }, (response) => {
    const error = chrome.runtime.lastError;
    const result = error ? { type: "error", message: error.message } : response;
    // Keep compatibility with a host from before the candidate protocol. It is
    // still safe because that response was already produced after confirmation.
    if (result?.type === "fill") {
      if (requestVersion !== focusVersion || location.href !== requestedUrl || !isLoginField(password) || focused !== password) {
        const changed = { type: "error", message: "登录页面或输入框已变化，请重新点击。" };
        result.username = "";
        result.password = "";
        showStatus(changed, automatic);
        sendResponse(changed);
        return;
      }
      suppressFocusUntil = Date.now() + 1600;
      try {
        fillLogin(password, result);
        showStatus({ type: "filled" }, automatic);
        sendResponse({ type: "filled" });
      } catch (_error) {
        const failed = { type: "error", message: "网页不允许填入当前输入框，请刷新网页后重试。" };
        showStatus(failed, automatic);
        sendResponse(failed);
      } finally {
        result.username = "";
        result.password = "";
      }
      return;
    }
    if (result?.type === "candidates" && Array.isArray(result.candidates)) {
      if (requestVersion !== focusVersion || !isLoginField(password) || focused !== password) {
        sendResponse({ type: "ready" });
        return;
      }
      showCandidates(result.candidates, password);
      sendResponse({ type: "candidates" });
      return;
    }
    const fallback = result || { type: "error", message: "没有可填入的记录。" };
    showStatus(fallback, automatic && ["noMatch", "cancelled"].includes(fallback.type));
    sendResponse(fallback);
  });
  return true;
}

document.addEventListener("focusin", (event) => rememberPassword(event.target, event.isTrusted !== false), true);
document.addEventListener("keydown", (event) => {
  if (event.key === "Escape") closeChooser();
}, true);
document.addEventListener("pointerdown", (event) => {
  if (chooser && event.target !== chooser.password && !chooser.root.contains(event.target)) closeChooser();
}, true);
if (document.activeElement) rememberPassword(document.activeElement, isIdentifier(document.activeElement));

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || message.type !== "fill-request") return false;
  const automatic = message.automatic === true;
  const active = document.activeElement;
  const password = isLoginField(active) ? active : focused;
  if (!isLoginField(password)) {
    const result = { type: "error", message: "请先聚焦用户名或密码框，再使用扩展按钮或快捷键。" };
    showStatus(result, automatic);
    sendResponse(result);
    return false;
  }
  return requestCandidates(password, automatic, sendResponse);
});
