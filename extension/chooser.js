// 候选框界面，运行在扩展自己的 iframe 里（chrome-extension:// 源），网页脚本读不到其中的候选和短密钥。
// 挂载它的内容脚本会用 postMessage 送来一次性 nonce，凭 nonce 连上后台取得候选；
// 选中账号、输入短密钥后由后台向桌面端验证，并把凭据直接送到密码框所在的 frame。
const NONCE_MESSAGE = "keep-password-nonce";
const SIZE_MESSAGE = "keep-password-chooser-size";
const VISIBLE_ROWS = 5;

const box = document.querySelector(".box");
const hint = document.querySelector(".hint");
const list = document.querySelector(".list");
const key = document.querySelector(".key");
const keyInput = document.querySelector(".key-input");
const fillButton = document.querySelector(".fill");
const error = document.querySelector(".error");
const back = document.querySelector(".back");

let port = null;
let nonce = null;
let authorized = false;
let closed = false;
let selected = null;
let mode = "password";
let shown = "";

function send(message) {
  if (!port) connect();
  try {
    port.postMessage(message);
  } catch (_error) {
    port = null;
    connect();
    port.postMessage(message);
  }
}

function connect() {
  port = chrome.runtime.connect({ name: "kp-chooser" });
  port.onMessage.addListener(onMessage);
  port.onDisconnect.addListener(() => {
    // 后台 service worker 被回收时端口会断开：带同一个 nonce 重新连上即可。
    port = null;
    if (!closed && authorized) {
      connect();
      port.postMessage({ type: "hello", nonce });
    }
  });
}

function close() {
  if (closed) return;
  closed = true;
  send({ type: "close" });
}

function reportSize() {
  // 报告不受 iframe 高度限制的自然高度，否则候选框被压扁后就再也撑不回来。
  const rows = list.querySelectorAll(".candidate");
  const rowHeight = rows.length ? rows[0].offsetHeight : 0;
  const listHeight = list.style.display === "none" ? 0
    : Math.min(list.scrollHeight, rowHeight > 0 ? rowHeight * VISIBLE_ROWS + 12 : list.scrollHeight);
  let height = listHeight + 2;
  for (const child of box.children) {
    if (child !== list) height += child.offsetHeight;
  }
  window.parent.postMessage({ kp: SIZE_MESSAGE, height: Math.ceil(height) }, "*");
}

function add(parent, className, text) {
  const element = document.createElement("div");
  element.className = className;
  element.textContent = text;
  parent.appendChild(element);
  return element;
}

function render(candidates) {
  shown = JSON.stringify(candidates.map((item) => [item.id, item.accountId, item.name, item.username, item.domain]));
  hint.textContent = mode === "password"
    ? `${candidates.length} 个匹配账号，选择后输入短密钥授权`
    : "选择账号并输入短密钥后填入用户名，然后自行点击下一步";
  list.replaceChildren();
  for (const candidate of candidates) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "candidate";
    button.setAttribute("role", "option");
    add(button, "name", candidate.name || "未命名");
    add(button, "meta", candidate.username || "未设置用户名");
    add(button, "domain", candidate.domain || "未设置网站");
    button.addEventListener("click", () => choose(candidate));
    list.appendChild(button);
  }
  reportSize();
}

function choose(candidate) {
  selected = candidate;
  error.textContent = "";
  list.style.display = "none";
  key.classList.add("visible");
  keyInput.value = "";
  fillButton.disabled = false;
  keyInput.focus();
  reportSize();
}

function showList() {
  selected = null;
  error.textContent = "";
  key.classList.remove("visible");
  list.style.display = "block";
  reportSize();
}

function submit() {
  if (!selected || fillButton.disabled) return;
  fillButton.disabled = true;
  error.textContent = "正在验证…";
  reportSize();
  send({ type: "fill", entryId: selected.id, accountId: selected.accountId, shortKey: keyInput.value || "" });
  keyInput.value = "";
}

function onMessage(message) {
  if (!message || typeof message !== "object") return;
  if (message.type === "init" || message.type === "update") {
    authorized = true;
    mode = message.mode === "username" ? "username" : mode;
    const candidates = Array.isArray(message.candidates) ? message.candidates : [];
    // 已经选中账号在输入短密钥时，不打断用户；重连后的重复 init 也不重画。
    if (selected || JSON.stringify(candidates.map((item) => [item.id, item.accountId, item.name, item.username, item.domain])) === shown) return;
    render(candidates);
    return;
  }
  if (message.type === "error") {
    fillButton.disabled = false;
    error.textContent = message.message || "无法完成填充。";
    keyInput.focus();
    reportSize();
    return;
  }
  if (message.type === "denied" && !authorized) {
    port = null;
  }
}

window.addEventListener("message", (event) => {
  // 网页本身也能往这里发消息，所以只把 nonce 当作“尝试”：后台核对通过才算连上，伪造的不会挡住真的。
  if (authorized || closed || event.source !== window.parent) return;
  const data = event.data;
  if (!data || data.kp !== NONCE_MESSAGE || typeof data.nonce !== "string" || data.nonce.length > 64) return;
  nonce = data.nonce;
  send({ type: "hello", nonce });
});

document.querySelector(".close").addEventListener("click", close);
back.addEventListener("click", showList);
fillButton.addEventListener("click", submit);
keyInput.addEventListener("keydown", (event) => {
  if (event.key === "Enter") submit();
});
document.addEventListener("keydown", (event) => {
  if (event.key === "Escape") {
    close();
    return;
  }
  if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
  const rows = Array.from(list.querySelectorAll(".candidate"));
  if (rows.length === 0 || list.style.display === "none") return;
  event.preventDefault();
  const index = rows.indexOf(document.activeElement);
  const next = event.key === "ArrowDown" ? Math.min(rows.length - 1, index + 1) : Math.max(0, index - 1);
  rows[next].focus();
});
// 点到候选框外的透明留白（阴影区域）时也关闭，和点网页其他位置一致。
document.addEventListener("pointerdown", (event) => {
  if (!box.contains(event.target)) close();
});
if (document.fonts && document.fonts.ready) document.fonts.ready.then(reportSize);
