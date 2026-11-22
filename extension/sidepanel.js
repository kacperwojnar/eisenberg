const records = document.getElementById("records");
const status = document.getElementById("status");
const fill = document.getElementById("fill");

let formModel = { properties: [] };
let selected = null;

async function apiBase() {
  const stored = await chrome.storage.sync.get({ apiBase: "http://localhost:5000" });
  return stored.apiBase;
}

function show(text) {
  status.textContent = text;
}

async function load() {
  records.replaceChildren();
  selected = null;
  fill.disabled = true;
  let response;
  try {
    response = await fetch(await apiBase() + "/form-instances", {
      headers: { "X-Dev-User": "user" }
    });
  } catch {
    show("The API is not running.");
    return;
  }
  if (!response.ok) {
    show("No records yet.");
    return;
  }
  const body = await response.json();
  formModel = body.model || { properties: [] };
  const instances = body.instances || [];
  if (instances.length === 0) {
    show("No records yet.");
    return;
  }
  for (const instance of instances) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "record";
    button.textContent = instance.label || instance.id;
    button.addEventListener("click", () => {
      selected = instance;
      for (const other of records.querySelectorAll("button")) other.setAttribute("aria-pressed", "false");
      button.setAttribute("aria-pressed", "true");
      fill.disabled = false;
    });
    records.appendChild(button);
  }
  show("");
}

async function openTab() {
  const tabs = await chrome.tabs.query({ active: true });
  return tabs.find((item) => item.url && /^https?:/i.test(item.url));
}

fill.addEventListener("click", async () => {
  if (!selected) return;
  if (!chrome.debugger) {
    show("On the extensions page, click Reload. The version must say 0.3.0.");
    return;
  }
  const tab = await openTab();
  if (!tab?.id) {
    show("Open the form page first. This cannot fill edge:// or chrome:// pages.");
    return;
  }
  const target = { tabId: tab.id };
  try {
    await chrome.debugger.attach(target, "1.3");
    const expression = `(${enterValues})(${JSON.stringify(formModel.properties || [])}, ${JSON.stringify(selected.values || {})})`;
    const result = await chrome.debugger.sendCommand(target, "Runtime.evaluate", {
      expression,
      returnByValue: true
    });
    const lines = (result.result?.value || []).map((item) => `${item.name}: ${item.status}`);
    show(lines.join("\n") || "Nothing to enter.");
  } catch (error) {
    show(error.message || "Could not enter the record.");
  } finally {
    try { await chrome.debugger.detach(target); } catch { /* already detached */ }
  }
});

function enterValues(properties, values) {
  const text = (value) => (value || "").replace(/\s+/g, " ").trim();
  const same = (left, right) => text(left).toLowerCase() === text(right).toLowerCase();
  const seen = (element) => element.getClientRects().length > 0;
  const exact = (wanted) => {
    const found = [...document.querySelectorAll("label, legend, button, [role='radio'], [role='option'], li, span, div, p")];
    return found.filter((element) => seen(element) && same(element.innerText, wanted))
      .sort((left, right) => left.innerText.length - right.innerText.length)[0] || null;
  };
  const controlNear = (label) => {
    const root = label?.closest("fieldset, section, form, li") || label?.parentElement;
    return root?.querySelector("input:not([type=hidden]):not([type=radio]):not([type=checkbox]), textarea, select") || null;
  };
  const outcome = [];
  for (const property of properties) {
    const value = values[property.name];
    if (value == null || value === "") {
      outcome.push({ name: property.name, status: "empty" });
      continue;
    }
    const option = (property.options || []).find((item) => same(item, value));
    if (option) {
      const choice = exact(option);
      if (!choice) {
        outcome.push({ name: property.name, status: "not found" });
        continue;
      }
      choice.click();
      outcome.push({ name: property.name, status: "clicked" });
      continue;
    }
    const label = exact(property.pageLabel || "");
    const control = controlNear(label);
    if (!control) {
      outcome.push({ name: property.name, status: "not found" });
      continue;
    }
    if (control instanceof HTMLSelectElement) {
      const match = [...control.options].find((item) => same(item.text, value) || same(item.value, value));
      if (!match) {
        outcome.push({ name: property.name, status: "not found" });
        continue;
      }
      control.value = match.value;
    } else {
      const prototype = control instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(prototype, "value").set.call(control, String(value));
    }
    control.dispatchEvent(new Event("input", { bubbles: true }));
    control.dispatchEvent(new Event("change", { bubbles: true }));
    outcome.push({ name: property.name, status: "entered" });
  }
  return outcome;
}

load();
