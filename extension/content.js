const reported = new WeakSet();

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

function report(password) {
  if (reported.has(password)) {
    return;
  }
  reported.add(password);
  const username = usernameFor(password);
  chrome.runtime.sendMessage(
    {
      type: "discover",
      url: location.href,
      title: document.title
    },
    (response) => {
      if (chrome.runtime.lastError || !response || response.type !== "fill") {
        return;
      }
      fill(username, response.username || "");
      fill(password, response.password || "");
    }
  );
}

function scan() {
  document.querySelectorAll('input[type="password"]').forEach((password) => {
    if (password instanceof HTMLInputElement && !password.disabled) {
      report(password);
    }
  });
}

scan();
const observer = new MutationObserver(() => scan());
observer.observe(document.documentElement, { childList: true, subtree: true });
