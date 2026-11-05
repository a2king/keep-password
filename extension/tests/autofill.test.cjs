const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const backgroundSource = readFileSync(join(__dirname, "..", "background.js"), "utf8");
const contentSource = readFileSync(join(__dirname, "..", "content.js"), "utf8");

function event() {
  return { listeners: [], addListener(callback) { this.listeners.push(callback); } };
}

function background(store = {}) {
  const sent = [];
  const badges = [];
  const titles = [];
  const nativeMessages = [];
  const ports = [];
  const timers = new Map();
  let timerId = 0;
  const makePort = () => {
    const created = { onMessage: event(), onDisconnect: event(), postMessage(message) { nativeMessages.push(message); }, disconnect() { this.disconnected = true; } };
    ports.push(created);
    return created;
  };
  const port = makePort();
  let connects = 0;
  const chrome = {
    runtime: { onMessage: event(), connectNative: () => (connects++ === 0 ? port : makePort()) },
    storage: { session: {
      set(values, callback) { Object.assign(store, values); callback?.(); },
      get(key, callback) { callback(store); },
      remove(key) { delete store[key]; }
    } },
    action: {
      onClicked: event(), setBadgeText(value) { badges.push(value); },
      setBadgeBackgroundColor() {}, setTitle(value) { titles.push(value); }
    },
    commands: { onCommand: event() },
    tabs: {
      onRemoved: event(), onUpdated: event(),
      sendMessage(tabId, message, target, callback) { sent.push({ tabId, message, target }); callback({ type: "filled" }); }
    }
  };
  vm.runInNewContext(backgroundSource, {
    chrome,
    setTimeout(callback, ms) { timers.set(++timerId, { callback, ms }); return timerId; },
    clearTimeout(id) { timers.delete(id); }
  });
  return { chrome, store, sent, badges, titles, nativeMessages, port, ports, timers,
    connects: () => connects,
    receive: chrome.runtime.onMessage.listeners[0], click: chrome.action.onClicked.listeners[0] };
}

test("one request reaches only the last focused frame, including after worker suspension", () => {
  const first = background();
  first.receive({ type: "password-focused" }, { tab: { id: 7 }, frameId: 0, documentId: "old-document" }, () => {});
  first.receive({ type: "password-focused" }, { tab: { id: 7 }, frameId: 4, documentId: "login-document" }, () => {});
  const restarted = background(first.store);
  restarted.click({ id: 7 });
  assert.equal(restarted.sent.length, 1);
  assert.equal(restarted.sent[0].target.frameId, 4);
  assert.equal(restarted.sent[0].target.documentId, "login-document");
  assert.equal(restarted.badges[0].text, "OK");
  restarted.chrome.tabs.onUpdated.listeners[0](7, { status: "loading" });
  restarted.click({ id: 7 });
  assert.equal(restarted.sent[1].target.frameId, 0);
});

test("a password focus only records the frame; the content script queries candidates itself", () => {
  const harness = background();
  let reply;
  harness.receive({ type: "password-focused" }, { tab: { id: 11 }, frameId: 2, documentId: "login" }, (value) => { reply = value; });
  assert.equal(reply.type, "ready");
  assert.equal(harness.sent.length, 0);
  assert.equal(harness.nativeMessages.length, 0);
  assert.deepEqual({ ...harness.store["password-frame-11"] }, { frameId: 2, documentId: "login" });
});

test("requests share one native port and replies are matched by requestId", () => {
  const harness = background();
  const replies = [];
  const sender = { tab: { id: 5 }, url: "https://real.example/login" };
  harness.receive({ type: "discover-candidates" }, sender, (value) => replies.push(["first", value]));
  harness.receive({ type: "discover-candidates" }, sender, (value) => replies.push(["second", value]));
  assert.equal(harness.connects(), 1);
  const [first, second] = harness.nativeMessages;
  assert.notEqual(first.requestId, second.requestId);
  harness.port.onMessage.listeners[0]({ type: "noMatch", requestId: second.requestId });
  harness.port.onMessage.listeners[0]({ type: "candidates", candidates: [], requestId: first.requestId });
  assert.deepEqual(replies.map(([name, value]) => [name, value.type, value.requestId]),
    [["second", "noMatch", undefined], ["first", "candidates", undefined]]);
  harness.receive({ type: "discover-candidates" }, sender, () => {});
  assert.equal(harness.connects(), 1);
});

test("an older host that answers once without requestId still serves every request", () => {
  const harness = background();
  const replies = [];
  const sender = { tab: { id: 5 }, url: "https://real.example/login" };
  harness.receive({ type: "discover-candidates" }, sender, (value) => replies.push(value.type));
  harness.receive({ type: "fill-candidate", shortKey: "key" }, sender, (value) => replies.push(value.type));
  harness.port.onMessage.listeners[0]({ type: "noMatch" });
  harness.port.onDisconnect.listeners[0]();
  assert.deepEqual(replies, ["noMatch"]);
  assert.equal(harness.connects(), 2);
  assert.equal(harness.nativeMessages.at(-1).type, "fill-candidate");
  harness.ports[1].onMessage.listeners[0]({ type: "fill", username: "a", password: "b" });
  assert.deepEqual(replies, ["noMatch", "fill"]);
});

test("an idle native port is closed so the worker can sleep", () => {
  const harness = background();
  harness.receive({ type: "discover-candidates" }, { tab: { id: 5 }, url: "https://real.example/login" }, () => {});
  const { requestId } = harness.nativeMessages[0];
  harness.port.onMessage.listeners[0]({ type: "noMatch", requestId });
  const idle = Array.from(harness.timers.values()).find((timer) => timer.ms === 5 * 60 * 1000);
  idle.callback();
  assert.equal(harness.port.disconnected, true);
  harness.receive({ type: "discover-candidates" }, { tab: { id: 5 }, url: "https://real.example/login" }, () => {});
  assert.equal(harness.connects(), 2);
});

test("matching uses the browser-reported frame URL and preserves host errors", () => {
  const harness = background();
  let response;
  harness.receive({ type: "discover", url: "https://forged.example", title: "Login" },
    { tab: { id: 1 }, url: "https://real.example/login" }, (result) => { response = result; });
  assert.equal(harness.nativeMessages[0].url, "https://real.example/login");
  harness.chrome.runtime.lastError = { message: "Specified native messaging host not found." };
  harness.port.onDisconnect.listeners[0]();
  assert.match(response.message, /not found/);
});

test("candidate and fill-candidate messages are forwarded without replacing the browser URL", () => {
  const harness = background();
  harness.receive({ type: "discover-candidates", url: "https://forged.example", title: "Login" },
    { tab: { id: 5 }, url: "https://real.example/login" }, () => {});
  assert.equal(harness.nativeMessages[0].type, "discover-candidates");
  assert.equal(harness.nativeMessages[0].url, "https://real.example/login");

  harness.receive({ type: "fill-candidate", entryId: "entry", accountId: "account", shortKey: "key" },
    { tab: { id: 5 }, url: "https://real.example/login" }, () => {});
  assert.equal(harness.nativeMessages[1].type, "fill-candidate");
  assert.equal(harness.nativeMessages[1].entryId, "entry");
  assert.equal(harness.nativeMessages[1].url, "https://real.example/login");
});

test("a missing content script gives visible toolbar guidance", () => {
  const harness = background();
  harness.chrome.tabs.sendMessage = (_tabId, _message, _target, callback) => {
    harness.chrome.runtime.lastError = { message: "Receiving end does not exist." };
    callback();
    delete harness.chrome.runtime.lastError;
  };
  harness.click({ id: 3 });
  assert.equal(harness.badges[0].text, "!");
  assert.match(harness.titles[0].title, /刷新登录网页/);
});

function content() {
  const listeners = {};
  const inputs = [];
  const requests = [];
  const notices = [];
  class Input {
    constructor(type) {
      this.type = type;
      this.isConnected = true;
      this.disabled = false;
      this.readOnly = false;
      this.events = [];
      this._value = "";
      this.form = { querySelector: () => inputs.find((input) => input.type === "text"), querySelectorAll: () => inputs };
    }
    get value() { return this._value; }
    set value(value) { this._value = value; }
    getAttribute(name) { return name === "type" ? this.type : null; }
    focus() { document.activeElement = this; }
    dispatchEvent(value) { this.events.push(value.type); }
  }
  const username = new Input("text");
  const password = new Input("password");
  inputs.push(username, password);
  const document = {
    title: "Login", activeElement: password,
    addEventListener(name, callback) { listeners[name] = callback; },
    createElement() { return { setAttribute() {}, style: {}, remove() {} }; },
    documentElement: { appendChild(value) { notices.push(value.textContent); } }
  };
  const location = { href: "https://real.example/login" };
  const chrome = { runtime: {
    onMessage: event(),
    sendMessage(message, callback) {
      if (message.type === "discover" || message.type === "discover-candidates") { requests.push({ message, callback }); }
      else { callback({ type: "ready" }); }
    }
  } };
  vm.runInNewContext(contentSource, {
    HTMLInputElement: Input, document, location, chrome,
    Event: class { constructor(type) { this.type = type; } },
    setTimeout: () => 1, clearTimeout() {}
  });
  return { username, password, notices, requests, location,
    receive: chrome.runtime.onMessage.listeners[0] };
}

test("controlled inputs get native values and input/change events after approval", () => {
  const harness = content();
  let overriddenSetterCalls = 0;
  Object.defineProperty(harness.password, "value", {
    get() { return this._value; }, set() { overriddenSetterCalls += 1; }
  });
  let result;
  assert.equal(harness.receive({ type: "fill-request" }, {}, (response) => { result = response; }), true);
  const secret = { type: "fill", username: "alice", password: "test-password" };
  harness.requests[0].callback(secret);
  assert.equal(harness.username.value, "alice");
  assert.equal(harness.password.value, "test-password");
  assert.equal(overriddenSetterCalls, 0);
  assert.deepEqual(harness.password.events, ["input", "change"]);
  assert.equal(result.type, "filled");
  assert.equal(secret.password, "");
  assert.match(harness.notices.at(-1), /已填入/);
});

test("host registration and locked-vault errors are visible in the page", () => {
  for (const response of [
    { type: "error", message: "Specified native messaging host not found." },
    { type: "locked" }
  ]) {
    const harness = content();
    harness.receive({ type: "fill-request" }, {}, () => {});
    harness.requests[0].callback(response);
    assert.match(harness.notices.at(-1), response.type === "locked" ? /先解锁/ : /还没有连上/);
    assert.equal(harness.password.value, "");
  }
});

test("navigation or a replaced field while awaiting approval prevents filling", () => {
  for (const change of [
    (harness) => { harness.location.href = "https://real.example/other"; },
    (harness) => { harness.password.isConnected = false; }
  ]) {
    const harness = content();
    let result;
    harness.receive({ type: "fill-request" }, {}, (response) => { result = response; });
    change(harness);
    const secret = { type: "fill", username: "alice", password: "test-password" };
    harness.requests[0].callback(secret);
    assert.equal(harness.password.value, "");
    assert.equal(harness.username.value, "");
    assert.equal(secret.password, "");
    assert.equal(result.type, "error");
  }
});

test("candidates closer to the page address come first and the menu stays under the field", () => {
  const sandbox = {
    HTMLInputElement: function Input() {},
    document: { addEventListener() {}, activeElement: null, createElement() { return { setAttribute() {}, style: {} }; } },
    location: { href: "https://login.example.com/signin" },
    chrome: { runtime: { onMessage: event(), sendMessage(_message, callback) { callback({ type: "ready" }); } } },
    Event: class { constructor(type) { this.type = type; } },
    setTimeout: () => 1,
    clearTimeout() {},
    URL
  };
  vm.runInNewContext(contentSource, sandbox);
  const ordered = sandbox.sortCandidatesByAddress([
    { domain: "shop.example.com", name: "远" },
    { domain: "login.example.com", name: "近" },
    { domain: "example.com", name: "根" }
  ], sandbox.location.href);
  assert.deepEqual(ordered.map((item) => item.name), ["近", "根", "远"]);
  // 普通宽度的输入框：候选框与输入框左右对齐、同宽，位于下方 8px。
  const frame = sandbox.chooserFrame({ left: 240, right: 640, top: 100, bottom: 132 }, 1000);
  assert.equal(frame.left, 240);
  assert.equal(frame.top, 140);
  assert.equal(frame.width, 400);
  // 很窄的输入框：保持左对齐，宽度不小于 260（短密钥输入行需要空间）。
  const narrow = sandbox.chooserFrame({ left: 240, right: 420, top: 100, bottom: 132 }, 1000);
  assert.equal(narrow.left, 240);
  assert.equal(narrow.width, 260);
  // 靠近视口右边：整体左移，不超出视口。
  const edge = sandbox.chooserFrame({ left: 900, right: 980, top: 100, bottom: 132 }, 1000);
  assert.equal(edge.width, 260);
  assert.ok(edge.left + edge.width <= 1000 - 8);
});

function openContent({ href, active = 0, inputs }) {
  const requests = [];
  const notices = [];
  class Input {
    constructor(type, attrs = {}) {
      this.type = type;
      this.attrs = { type, ...attrs };
      this.isConnected = true;
      this.disabled = false;
      this.readOnly = false;
      this.hidden = false;
      this.events = [];
      this._value = "";
    }
    get value() { return this._value; }
    set value(next) { this._value = next; }
    getAttribute(name) { return Object.prototype.hasOwnProperty.call(this.attrs, name) ? this.attrs[name] : null; }
    focus() {}
    dispatchEvent(value) { this.events.push(value.type); }
  }
  const fields = inputs.map(([type, attrs]) => new Input(type, attrs));
  const document = {
    title: "Sign in",
    activeElement: fields[active],
    addEventListener() {},
    createElement() { return { setAttribute() {}, style: {}, remove() {} }; },
    documentElement: { appendChild(value) { notices.push(value.textContent); } },
    querySelectorAll(selector) {
      return selector === 'input[type="password"]' ? fields.filter((field) => field.type === "password") : [];
    }
  };
  const location = { href };
  const chrome = { runtime: {
    onMessage: event(),
    sendMessage(message, callback) {
      if (message.type === "discover" || message.type === "discover-candidates") requests.push({ message, callback });
      else callback({ type: "ready" });
    }
  } };
  vm.runInNewContext(contentSource, {
    HTMLInputElement: Input, document, location, chrome, URL,
    Event: class { constructor(type) { this.type = type; } },
    setTimeout: () => 1, clearTimeout() {}
  });
  return { fields, requests, notices, receive: chrome.runtime.onMessage.listeners[0] };
}

test("google identifier page fills the username without a password field", () => {
  const harness = openContent({
    href: "https://accounts.google.com/v3/signin/identifier?flowName=GlifWebSignIn",
    inputs: [["email", { name: "identifier", id: "identifierId", autocomplete: "username" }]]
  });
  let result;
  assert.equal(harness.receive({ type: "fill-request" }, {}, (response) => { result = response; }), true);
  const secret = { type: "fill", username: "ada@gmail.com", password: "secret" };
  harness.requests[0].callback(secret);
  assert.equal(harness.fields[0].value, "ada@gmail.com");
  assert.equal(result.type, "filled");
  assert.equal(secret.password, "");
  assert.match(harness.notices.at(-1), /已填入/);
});

test("an email field outside a login page does not request credentials", () => {
  const harness = openContent({
    href: "https://shop.example/contact",
    inputs: [["email", { name: "email", id: "email" }]]
  });
  let result;
  harness.receive({ type: "fill-request" }, {}, (response) => { result = response; });
  assert.equal(harness.requests.length, 0);
  assert.equal(result.type, "error");
  assert.equal(harness.fields[0].value, "");
});

test("a username field on a page that already shows a password waits for the password field", () => {
  const harness = openContent({
    href: "https://real.example/login",
    inputs: [
      ["text", { name: "username", autocomplete: "username" }],
      ["password", { name: "password" }]
    ]
  });
  let result;
  harness.receive({ type: "fill-request" }, {}, (response) => { result = response; });
  assert.equal(harness.requests.length, 0);
  assert.equal(result.type, "error");
});

test("a read-only password box does not request credentials", () => {
  const harness = content();
  harness.password.readOnly = true;
  let result;
  harness.receive({ type: "fill-request" }, {}, (response) => { result = response; });
  assert.equal(harness.requests.length, 0);
  assert.equal(result.type, "error");
});

function cachedContent() {
  const listeners = {};
  const requests = [];
  const notices = [];
  let now = 1_000_000;
  class Input {
    constructor(type) { this.type = type; this.isConnected = true; this.disabled = false; this.readOnly = false; this._value = ""; this.form = null; }
    get value() { return this._value; }
    set value(next) { this._value = next; }
    getAttribute(name) { return name === "type" ? this.type : null; }
    focus() {}
    dispatchEvent() {}
  }
  const password = new Input("password");
  const document = {
    title: "Login", activeElement: null,
    addEventListener(name, callback) { listeners[name] = callback; },
    querySelector: (selector) => (selector.includes('input[type="password"]') ? password : null),
    querySelectorAll: () => [password],
    createElement() { return { setAttribute() {}, style: {}, remove() {} }; },
    documentElement: { appendChild(value) { notices.push(value.textContent); } }
  };
  const chrome = { runtime: {
    onMessage: event(),
    sendMessage(message, callback) {
      if (message.type === "discover-candidates") requests.push({ message, callback });
      else callback({ type: "ready" });
    }
  } };
  const FakeDate = class extends Date { static now() { return now; } };
  vm.runInNewContext(contentSource, {
    HTMLInputElement: Input, document, location: { href: "https://real.example/login" }, chrome, URL, Date: FakeDate,
    Event: class { constructor(type) { this.type = type; } },
    setTimeout: () => 1, clearTimeout() {}
  });
  return { password, requests, notices, receive: chrome.runtime.onMessage.listeners[0],
    focus: () => listeners.focusin({ target: password, isTrusted: true }),
    advance: (ms) => { now += ms; } };
}

test("a login page prefetches candidates once and focus uses the cached answer", () => {
  const harness = cachedContent();
  assert.equal(harness.requests.length, 1);
  assert.equal(harness.requests[0].message.type, "discover-candidates");
  harness.requests[0].callback({ type: "noMatch" });

  harness.focus();
  assert.equal(harness.requests.length, 1);

  let result;
  assert.equal(harness.receive({ type: "fill-request" }, {}, (value) => { result = value; }), false);
  assert.equal(result.type, "noMatch");
  assert.match(harness.notices.at(-1), /没有匹配/);

  harness.advance(3000);
  harness.focus();
  assert.equal(harness.requests.length, 2);
});

test("focus while the prefetch is still pending joins it instead of sending another request", () => {
  const harness = cachedContent();
  harness.focus();
  let result;
  harness.receive({ type: "fill-request" }, {}, (value) => { result = value; });
  assert.equal(harness.requests.length, 1);
  harness.requests[0].callback({ type: "locked" });
  assert.equal(result.type, "locked");
  assert.match(harness.notices.at(-1), /先解锁/);
  harness.receive({ type: "fill-request" }, {}, () => {});
  assert.equal(harness.requests.length, 2);
});
