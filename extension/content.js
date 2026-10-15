let focused = null;
let lastRequest = 0;
let notice = null;
let noticeTimer = null;

function showStatus(response) {
  const messages = {
    filled: "已填入登录信息。",
    locked: "请先解锁 Keep Password，再重新填充。",
    noMatch: "没有匹配的网站记录，请检查条目中的网站地址。",
    cancelled: "已取消填充。"
  };
  let text = response.message || messages[response.type] || "没有可填入的记录。";
  if (/native messaging host not found|specified native messaging host|access to the specified native messaging host/i.test(text)) {
    text = "浏览器尚未连接 Keep Password。请按扩展目录中的 README.md 完成注册。";
  }
  if (!notice) {
    notice = document.createElement("div");
    notice.setAttribute("role", "status");
    notice.style.cssText = "position:fixed;right:20px;top:20px;z-index:2147483647;max-width:380px;padding:12px 16px;border-radius:8px;background:#202a36;color:white;font:14px/1.5 sans-serif;box-shadow:0 3px 16px #0005;pointer-events:none;";
  }
  notice.textContent = `Keep Password：${text}`;
  document.documentElement.appendChild(notice);
  clearTimeout(noticeTimer);
  noticeTimer = setTimeout(() => notice.remove(), 7000);
}

function isPassword(input) {
  return input instanceof HTMLInputElement && input.type === "password" &&
    input.isConnected && !input.disabled && !input.readOnly;
}

function rememberPassword(input) {
  if (!isPassword(input)) {
    return;
  }
  focused = input;
  chrome.runtime.sendMessage({ type: "password-focused" }, () => {
    // A refreshed extension can leave old content scripts behind until the page reloads.
    void chrome.runtime.lastError;
  });
}

function usernameFor(password) {
  const form = password.form;
  const scope = form || password.parentElement || document;
  const preferred = scope.querySelector(
    'input[autocomplete="username"], input[type="email"], input[name="username"], input[name="user"], input[name="login"]'
  );
  if (preferred && preferred !== password && !preferred.disabled && !preferred.readOnly) {
    return preferred;
  }

  const inputs = Array.from((form || document).querySelectorAll("input"));
  const index = inputs.indexOf(password);
  for (let i = index - 1; i >= 0; i -= 1) {
    const input = inputs[i];
    const type = (input.getAttribute("type") || "text").toLowerCase();
    if ((type === "text" || type === "email" || type === "tel") && !input.disabled && !input.readOnly) {
      return input;
    }
  }
  return null;
}

function fill(input, value) {
  if (!input || !input.isConnected || input.disabled || input.readOnly) {
    return;
  }
  input.focus();
  // Use the native setter so React and similar controlled inputs receive the change.
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set;
  setter.call(input, value);
  input.dispatchEvent(new Event("input", { bubbles: true }));
  input.dispatchEvent(new Event("change", { bubbles: true }));
}

document.addEventListener("focusin", (event) => {
  rememberPassword(event.target);
}, true);
rememberPassword(document.activeElement);

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || message.type !== "fill-request") {
    return false;
  }

  const finish = (response) => {
    showStatus(response);
    sendResponse(response);
  };

  const now = Date.now();
  if (now - lastRequest < 1500) {
    finish({ type: "error", message: "请稍后再试。" });
    return false;
  }

  const active = document.activeElement;
  const password = isPassword(active) ? active : focused;
  if (!isPassword(password)) {
    finish({ type: "error", message: "请先聚焦密码框，再使用扩展按钮或快捷键。" });
    return false;
  }

  lastRequest = now;
  const username = usernameFor(password);
  const requestedUrl = location.href;
  chrome.runtime.sendMessage(
    { type: "discover", url: location.href, title: document.title },
    (response) => {
      const error = chrome.runtime.lastError;
      if (error || !response || response.type !== "fill") {
        finish(error ? { type: "error", message: error.message } : response || { type: "error", message: "没有可填入的记录。" });
        return;
      }
      try {
        if (location.href !== requestedUrl || !isPassword(password)) {
          finish({ type: "error", message: "登录页面或密码框已变化，请重新点击密码框后填充。" });
          return;
        }
        fill(username, response.username || "");
        if (!isPassword(password)) {
          finish({ type: "error", message: "登录页面更新了密码框，请重新点击密码框后填充。" });
          return;
        }
        fill(password, response.password || "");
        finish({ type: "filled" });
      } catch (_error) {
        finish({ type: "error", message: "网页不允许填入当前输入框，请刷新网页后重试。" });
      } finally {
        response.username = "";
        response.password = "";
      }
    }
  );
  return true;
});
