let focused = null;
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
  if (mounted && mounted.probe === current.probe) unmountChooser();
  chrome.runtime.sendMessage({ type: "chooser-close", probe: current.probe }, () => {
    void chrome.runtime.lastError;
  });
}

function rememberPassword(input, automatic = true) {
  if (!isLoginField(input)) return;
  focused = input;
  focusVersion += 1;
  if (!automatic || Date.now() < suppressFocusUntil) return;
  closeChooser();
  // 只让后台记住聚焦的 frame（供工具栏按钮和快捷键用）；候选直接在这里查询，不再绕回后台。
  chrome.runtime.sendMessage({ type: "password-focused" }, () => {
    void chrome.runtime.lastError;
  });
  requestCandidates(input, true, () => {});
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

const CHOOSER_MIN_WIDTH = 260;
const CHOOSER_GAP = 8;

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

// 候选框画在扩展自己的 iframe（chooser.html）里，并挂到顶层页面：
// 登录框在 iframe 里时（例如 163 邮箱的 dl.reg.163.com）不会被 iframe 边界裁掉，
// 网页脚本也读不到候选名单和短密钥。账号、短密钥和填入结果都经后台转发，凭据只回到密码框所在的 frame。
// 位置信息沿 frame 链逐级 postMessage 上传，每一级加上自己 iframe 元素的偏移。
const GEOMETRY_MESSAGE = "keep-password-geometry";
const SIZE_MESSAGE = "keep-password-chooser-size";
const CHOOSER_PAD = 10;
let mounted = null;
const childGeometry = new Map();
let lastRelayed = null;

function randomId() {
  const bytes = new Uint8Array(16);
  if (typeof crypto !== "undefined" && crypto.getRandomValues) crypto.getRandomValues(bytes);
  else for (let i = 0; i < bytes.length; i += 1) bytes[i] = Math.floor(Math.random() * 256);
  return Array.from(bytes, (value) => value.toString(16).padStart(2, "0")).join("");
}

function isTopFrame() {
  try {
    return typeof window === "undefined" || window.top === window;
  } catch (_error) {
    return false;
  }
}

function plainRect(rect) {
  return { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom };
}

function validRect(rect) {
  return !!rect && ["left", "top", "right", "bottom"].every((key) => Number.isFinite(rect[key]));
}

function shiftRect(rect, offset) {
  return { left: rect.left + offset.left, top: rect.top + offset.top, right: rect.right + offset.left, bottom: rect.bottom + offset.top };
}

// 找到承载 source 窗口的 iframe 元素，返回其内容区左上角在本视口里的位置。
function frameOffset(source) {
  if (!document.querySelectorAll) return null;
  for (const frame of document.querySelectorAll("iframe, frame")) {
    if (frame.contentWindow !== source) continue;
    const box = frame.getBoundingClientRect();
    const style = typeof getComputedStyle === "function" ? getComputedStyle(frame) : null;
    const padLeft = style ? parseFloat(style.paddingLeft) || 0 : 0;
    const padTop = style ? parseFloat(style.paddingTop) || 0 : 0;
    return { left: box.left + (frame.clientLeft || 0) + padLeft, top: box.top + (frame.clientTop || 0) + padTop };
  }
  return null;
}

function relayGeometry(id) {
  const entry = childGeometry.get(id);
  const offset = entry && frameOffset(entry.source);
  if (!offset || isTopFrame()) return;
  lastRelayed = id;
  window.parent.postMessage({ kp: GEOMETRY_MESSAGE, id, rect: shiftRect(entry.rect, offset) }, "*");
}

// 密码框所在 frame：把输入框位置报给挂载候选框的 frame。
function reportGeometry() {
  if (!chooser || !chooser.password || !chooser.password.isConnected || !chooser.password.getBoundingClientRect) return;
  if (mounted && mounted.probe === chooser.probe) layoutMounted();
  if (!isTopFrame()) {
    window.parent.postMessage({ kp: GEOMETRY_MESSAGE, id: chooser.probe, rect: plainRect(chooser.password.getBoundingClientRect()) }, "*");
  }
}

function mountedRect() {
  if (!mounted) return null;
  if (chooser && chooser.probe === mounted.probe) {
    return chooser.password && chooser.password.getBoundingClientRect ? plainRect(chooser.password.getBoundingClientRect()) : null;
  }
  const entry = childGeometry.get(mounted.probe);
  const offset = entry && frameOffset(entry.source);
  return offset ? shiftRect(entry.rect, offset) : null;
}

function setStyle(element, values) {
  for (const [name, value] of Object.entries(values)) element.style.setProperty(name, value, "important");
}

function layoutMounted() {
  if (!mounted || typeof window === "undefined") return;
  const rect = mountedRect();
  if (!rect) return;
  const frame = chooserFrame(rect, window.innerWidth);
  const want = mounted.height || 0;
  const below = window.innerHeight - frame.top - CHOOSER_GAP;
  const above = rect.top - CHOOSER_GAP * 2;
  let top = frame.top;
  let room = below;
  // 下方放不下且上方更宽敞时，翻到输入框上方。
  if (want > below && above > below) {
    room = above;
    top = Math.max(CHOOSER_GAP, rect.top - CHOOSER_GAP - Math.min(want, above));
  }
  const height = Math.max(0, Math.min(want, room));
  setStyle(mounted.iframe, {
    left: `${frame.left - CHOOSER_PAD}px`,
    top: `${top - CHOOSER_PAD}px`,
    width: `${frame.width + CHOOSER_PAD * 2}px`,
    height: `${height + CHOOSER_PAD * 2}px`,
    opacity: want > 0 ? "1" : "0"
  });
}

function unmountChooser() {
  if (!mounted) return;
  const current = mounted;
  mounted = null;
  current.iframe.remove();
}

function mountChooser(probe, nonce) {
  unmountChooser();
  const parent = document.body || document.documentElement;
  if (!parent || typeof document.createElement !== "function" || !chrome.runtime.getURL) return false;
  if (parent.tagName && parent.tagName.toUpperCase() === "FRAMESET") return false;
  const iframe = document.createElement("iframe");
  iframe.setAttribute("title", "Keep Password 账号选择");
  iframe.setAttribute("allowtransparency", "true");
  iframe.setAttribute("tabindex", "-1");
  setStyle(iframe, {
    position: "fixed", "z-index": "2147483647", border: "0", margin: "0", padding: "0",
    background: "transparent", "color-scheme": "light", display: "block", visibility: "visible",
    "min-width": "0", "min-height": "0", "max-width": "none", "max-height": "none",
    width: "0", height: "0", opacity: "0", transform: "none", transition: "opacity 120ms ease", "pointer-events": "auto"
  });
  iframe.src = chrome.runtime.getURL("chooser.html");
  mounted = { probe, iframe, height: 0 };
  // 把一次性 nonce 交给候选框页面，它凭此向后台领取候选。目标源限定为扩展自身。
  iframe.addEventListener("load", () => {
    if (mounted && mounted.iframe === iframe && iframe.contentWindow) {
      iframe.contentWindow.postMessage({ kp: "keep-password-nonce", nonce }, new URL(iframe.src).origin);
    }
  });
  parent.appendChild(iframe);
  trackChooser();
  layoutMounted();
  return true;
}

// 关闭由别的 frame 请求挂载在这里的候选框（点网页其他位置、按 Esc）。
function closeMounted() {
  if (!mounted) return;
  const probe = mounted.probe;
  unmountChooser();
  chrome.runtime.sendMessage({ type: "chooser-close", probe }, () => {
    void chrome.runtime.lastError;
  });
}

function trackChooser() {
  if (typeof window === "undefined" || window.__kpChooserTracked) return;
  window.__kpChooserTracked = true;
  let scheduled = false;
  const refresh = () => {
    scheduled = false;
    reportGeometry();
    if (mounted) layoutMounted();
    if (lastRelayed && childGeometry.has(lastRelayed)) relayGeometry(lastRelayed);
  };
  const schedule = () => {
    if (scheduled) return;
    scheduled = true;
    if (typeof requestAnimationFrame === "function") requestAnimationFrame(refresh);
    else refresh();
  };
  window.addEventListener("scroll", schedule, true);
  window.addEventListener("resize", schedule);
}

if (typeof window !== "undefined" && window.addEventListener) {
  window.addEventListener("message", (event) => {
    const data = event.data;
    if (!data || typeof data !== "object") return;
    if (data.kp === SIZE_MESSAGE) {
      // 只接受我们自己挂上的扩展 iframe 报来的高度。
      if (mounted && event.source === mounted.iframe.contentWindow && Number.isFinite(data.height) && data.height >= 0) {
        mounted.height = Math.min(data.height, 2000);
        layoutMounted();
      }
      return;
    }
    if (data.kp !== GEOMETRY_MESSAGE || typeof data.id !== "string" || data.id.length > 64 || !validRect(data.rect)) return;
    if (!event.source || event.source === window || !frameOffset(event.source)) return;
    childGeometry.set(data.id, { source: event.source, rect: plainRect(data.rect) });
    while (childGeometry.size > 8) childGeometry.delete(childGeometry.keys().next().value);
    trackChooser();
    if (mounted && mounted.probe === data.id) layoutMounted();
    if (!isTopFrame()) relayGeometry(data.id);
  });
}

function showCandidates(candidates, password) {
  closeChooser();
  if (!Array.isArray(candidates) || candidates.length === 0 || !password || !password.isConnected) return;
  const probe = randomId();
  const pageUrl = typeof location !== "undefined" ? location.href : "";
  chooser = { probe, password, version: focusVersion, url: pageUrl };
  const sorted = sortCandidatesByAddress(candidates, pageUrl).map((item) => ({
    id: item.id, accountId: item.accountId, name: item.name, username: item.username, domain: item.domain
  }));
  chrome.runtime.sendMessage({
    type: "chooser-open", probe, candidates: sorted,
    mode: isPassword(password) ? "password" : "username", title: document.title
  }, (reply) => {
    void chrome.runtime.lastError;
    if (!chooser || chooser.probe !== probe) return;
    // 顶层页面挂不上（没有内容脚本、frameset 页面等）时，后台让我们在密码框所在的 frame 里显示。
    if (reply && reply.type === "mount-here" && typeof reply.nonce === "string") mountChooser(probe, reply.nonce);
  });
  trackChooser();
  reportGeometry();
}

function updateChooser(candidates) {
  if (!chooser) return;
  const sorted = sortCandidatesByAddress(candidates, chooser.url).map((item) => ({
    id: item.id, accountId: item.accountId, name: item.name, username: item.username, domain: item.domain
  }));
  chrome.runtime.sendMessage({ type: "chooser-update", probe: chooser.probe, candidates: sorted }, () => {
    void chrome.runtime.lastError;
  });
}

// 后台转来的已授权凭据：只在密码框、页面地址都没变时填入。
function receiveFill(message) {
  const result = { username: message.username || "", password: message.password || "" };
  message.username = "";
  message.password = "";
  try {
    if (!chooser || chooser.probe !== message.probe) {
      return { type: "error", message: "候选框已关闭，请重新点击密码框。" };
    }
    const { password, version, url } = chooser;
    if (version !== focusVersion || location.href !== url || !isLoginField(password) || focused !== password) {
      return { type: "error", message: "登录页面或输入框已变化，请重新点击。" };
    }
    suppressFocusUntil = Date.now() + 1600;
    fillLogin(password, result);
    chooser = null;
    if (mounted && mounted.probe === message.probe) unmountChooser();
    showStatus({ type: "filled" });
    return { type: "filled" };
  } catch (_error) {
    return { type: "error", message: "网页不允许填入当前输入框，请刷新网页后重试。" };
  } finally {
    result.username = "";
    result.password = "";
  }
}
// 候选缓存：页面加载时预取，聚焦时直接渲染，再后台刷新一次。只含名称、用户名、域名，不含密码。
// 浏览器匹配只看页面主机名，所以按主机名缓存；SPA 内跳转不需要重新查询。
const CANDIDATE_TTL_MS = 30 * 1000;
const CANDIDATE_REFRESH_MS = 2 * 1000;
let candidateCache = null;
let candidateInflight = null;

function pageHost() {
  try {
    return new URL(location.href).hostname;
  } catch (_error) {
    return String((location && location.href) || "");
  }
}

function cacheable(result) {
  // 不缓存 locked：用户可能刚在桌面端解锁，旧提示会误导。
  return !!result && (result.type === "candidates" && Array.isArray(result.candidates) || result.type === "noMatch");
}

function resultKey(result) {
  if (!result) return "";
  const ids = Array.isArray(result.candidates) ? result.candidates.map((item) => `${item.id}/${item.accountId}/${item.name}/${item.username}`) : [];
  return `${result.type}|${ids.join(",")}`;
}

function freshCache(host, maxAge) {
  return candidateCache && candidateCache.host === host && Date.now() - candidateCache.at < maxAge ? candidateCache : null;
}

// 合并同一主机名上并发的查询，所有等待方拿到同一个结果。
function fetchCandidates(callback) {
  const host = pageHost();
  if (candidateInflight && candidateInflight.host === host) {
    if (callback) candidateInflight.callbacks.push(callback);
    return;
  }
  const inflight = { host, callbacks: callback ? [callback] : [] };
  candidateInflight = inflight;
  chrome.runtime.sendMessage({ type: "discover-candidates", url: location.href, title: document.title }, (response) => {
    const error = chrome.runtime.lastError;
    const result = error ? { type: "error", message: error.message } : response;
    if (candidateInflight === inflight) candidateInflight = null;
    if (cacheable(result)) candidateCache = { host, at: Date.now(), result };
    if (inflight.callbacks.length === 0 && result) {
      // 纯预取时不应收到凭据；万一旧版宿主回了 fill，立即清掉。
      result.username = "";
      result.password = "";
      return;
    }
    if (result && result.type === "fill") {
      // 凭据只交给最后一个等待方（最近一次聚焦或按钮），其余的不重复填入。
      inflight.callbacks.slice(0, -1).forEach((done) => done({ type: "ready" }));
      inflight.callbacks[inflight.callbacks.length - 1](result);
      return;
    }
    for (const done of inflight.callbacks) done(result);
  });
}

function prefetchCandidates() {
  if (!document.querySelector || typeof location === "undefined" || !/^https?:/i.test(location.href || "")) return;
  const hasLogin = document.querySelector('input[type="password"], input[autocomplete~="username"]');
  if (hasLogin && !freshCache(pageHost(), CANDIDATE_TTL_MS)) fetchCandidates(null);
}

// 把查询结果展示到聚焦的输入框上，返回给工具栏徽标用的状态。
function presentCandidates(result, password, automatic, requestVersion, requestedUrl) {
  if (result?.type === "ready") return { type: "ready" };
  // Keep compatibility with a host from before the candidate protocol. It is
  // still safe because that response was already produced after confirmation.
  if (result?.type === "fill") {
    try {
      if (requestVersion !== focusVersion || location.href !== requestedUrl || !isLoginField(password) || focused !== password) {
        const changed = { type: "error", message: "登录页面或输入框已变化，请重新点击。" };
        showStatus(changed, automatic);
        return changed;
      }
      suppressFocusUntil = Date.now() + 1600;
      fillLogin(password, result);
      showStatus({ type: "filled" }, automatic);
      return { type: "filled" };
    } catch (_error) {
      const failed = { type: "error", message: "网页不允许填入当前输入框，请刷新网页后重试。" };
      showStatus(failed, automatic);
      return failed;
    } finally {
      result.username = "";
      result.password = "";
    }
  }
  if (result?.type === "candidates" && Array.isArray(result.candidates)) {
    if (requestVersion !== focusVersion || !isLoginField(password) || focused !== password) {
      return { type: "ready" };
    }
    showCandidates(result.candidates, password);
    return { type: "candidates" };
  }
  const fallback = result || { type: "error", message: "没有可填入的记录。" };
  showStatus(fallback, automatic && ["noMatch", "cancelled"].includes(fallback.type));
  return fallback;
}

function requestCandidates(password, automatic, sendResponse) {
  const requestVersion = focusVersion;
  const requestedUrl = location.href;
  const host = pageHost();
  const cached = freshCache(host, CANDIDATE_TTL_MS);
  if (cached) {
    // 有缓存就立即显示，不等本机宿主。
    sendResponse(presentCandidates(cached.result, password, automatic, requestVersion, requestedUrl));
    if (Date.now() - cached.at < CANDIDATE_REFRESH_MS) return false;
    const shownKey = resultKey(cached.result);
    fetchCandidates((fresh) => {
      // 后台刷新只在结果变化、且用户还停在同一个输入框、还没选账号时才更新界面。
      if (!cacheable(fresh) || resultKey(fresh) === shownKey) return;
      if (requestVersion !== focusVersion || focused !== password || !chooser || chooser.password !== password) return;
      if (fresh.type === "candidates") {
        updateChooser(fresh.candidates);
      } else {
        closeChooser();
      }
    });
    return false;
  }
  fetchCandidates((result) => {
    sendResponse(presentCandidates(result, password, automatic, requestVersion, requestedUrl));
  });
  return true;
}

document.addEventListener("focusin", (event) => rememberPassword(event.target, event.isTrusted !== false), true);
document.addEventListener("keydown", (event) => {
  if (event.key !== "Escape") return;
  closeChooser();
  closeMounted();
}, true);
document.addEventListener("pointerdown", (event) => {
  const onChooser = mounted && event.target === mounted.iframe;
  if (chooser && event.target !== chooser.password && !onChooser) closeChooser();
  else if (!chooser && mounted && !onChooser) closeMounted();
}, true);
// 关掉候选框后再点一次仍处于焦点的输入框，不会触发 focusin：用 click 重新打开。
document.addEventListener("click", (event) => {
  const target = event.target;
  if (!chooser && event.isTrusted !== false && target === document.activeElement && isLoginField(target)) {
    rememberPassword(target, true);
  }
}, true);
if (document.activeElement) rememberPassword(document.activeElement, isIdentifier(document.activeElement));
prefetchCandidates();

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message && message.type === "chooser-mount") {
    // 只有顶层页面负责挂载；后台已核对这个 probe 来自同一标签页。
    sendResponse({ type: mountChooser(String(message.probe || ""), String(message.nonce || "")) ? "mounted" : "failed" });
    return false;
  }
  if (message && message.type === "chooser-unmount") {
    if (mounted && mounted.probe === message.probe) unmountChooser();
    if (chooser && chooser.probe === message.probe) chooser = null;
    sendResponse({ type: "ready" });
    return false;
  }
  if (message && message.type === "chooser-fill") {
    sendResponse(receiveFill(message));
    return false;
  }
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
