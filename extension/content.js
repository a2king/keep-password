let focused = null;
let lastRequest = 0;

function usernameFor(password) {
  const form = password.form;
  const scope = form || password.parentElement || document;
  const preferred = scope.querySelector(
    'input[autocomplete="username"], input[type="email"], input[name="username"], input[name="user"], input[name="login"]'
  );
  if (preferred && preferred !== password) {
    return preferred;
  }

  const inputs = Array.from((form || document).querySelectorAll("input"));
  const index = inputs.indexOf(password);
  for (let i = index - 1; i >= 0; i -= 1) {
    const input = inputs[i];
    const type = (input.getAttribute("type") || "text").toLowerCase();
    if (type === "text" || type === "email" || type === "tel") {
      return input;
    }
  }
  return null;
}

function fill(input, value) {
  if (!input) {
    return;
  }
  input.focus();
  input.value = value;
  input.dispatchEvent(new Event("input", { bubbles: true }));
  input.dispatchEvent(new Event("change", { bubbles: true }));
}

document.addEventListener("focusin", (event) => {
  const target = event.target;
  if (target instanceof HTMLInputElement && target.type === "password" && !target.disabled) {
    focused = target;
  }
}, true);

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || message.type !== "fill-request") {
    return false;
  }

  const now = Date.now();
  if (now - lastRequest < 1500) {
    sendResponse({ type: "error", message: "请稍后再试。" });
    return false;
  }

  const active = document.activeElement;
  const password = focused && document.contains(focused)
    ? focused
    : active;
  if (!(password instanceof HTMLInputElement) || password.type !== "password" || password.disabled) {
    sendResponse({ type: "error", message: "请先聚焦密码框，再使用扩展按钮或快捷键。" });
    return false;
  }

  lastRequest = now;
  const username = usernameFor(password);
  chrome.runtime.sendMessage(
    { type: "discover", url: location.href, title: document.title },
    (response) => {
      if (chrome.runtime.lastError || !response || response.type !== "fill") {
        sendResponse(response || { type: "error", message: "没有可填入的记录。" });
        return;
      }
      fill(username, response.username || "");
      fill(password, response.password || "");
      response.username = "";
      response.password = "";
      sendResponse({ type: "filled" });
    }
  );
  return true;
});
