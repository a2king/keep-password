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
  const port = { onMessage: event(), onDisconnect: event(), postMessage(message) { nativeMessages.push(message); }, disconnect() {} };
  const chrome = {
    runtime: { onMessage: event(), connectNative: () => port },
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
  vm.runInNewContext(backgroundSource, { chrome });
  return { chrome, store, sent, badges, titles, nativeMessages, port,
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

test("a trusted password focus starts the inline candidate request automatically", () => {
  const harness = background();
  harness.receive({ type: "password-focused", auto: true }, { tab: { id: 11 }, frameId: 2, documentId: "login" }, () => {});
  assert.equal(harness.sent.length, 1);
  assert.equal(harness.sent[0].message.type, "fill-request");
  assert.equal(harness.sent[0].message.automatic, true);
  assert.equal(harness.sent[0].target.frameId, 2);
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
    assert.match(harness.notices.at(-1), response.type === "locked" ? /先解锁/ : /尚未连接/);
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
  const frame = sandbox.chooserFrame({ left: 240, right: 420, top: 100, bottom: 132 }, 1000);
  assert.equal(frame.left, 240);
  assert.equal(frame.top, 140);
  assert.ok(frame.left + frame.width <= 1000);
});

test("a read-only password box does not request credentials", () => {
  const harness = content();
  harness.password.readOnly = true;
  let result;
  harness.receive({ type: "fill-request" }, {}, (response) => { result = response; });
  assert.equal(harness.requests.length, 0);
  assert.equal(result.type, "error");
});
