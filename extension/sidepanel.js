const records = document.getElementById("records");
const status = document.getElementById("status");
const fill = document.getElementById("fill");

let formModel = { properties: [] };
let selected = null;

async function apiBase() {
  const stored = await chrome.storage.sync.get({ apiBase: "http://localhost:5000" });
  return stored.apiBase;
}

async function authHeaders(json) {
  const stored = await chrome.storage.local.get({ token: "", devUser: "user" });
  const headers = {};
  if (json) headers["Content-Type"] = "application/json";
  if (stored.token) headers.Authorization = "Bearer " + stored.token;
  headers["X-Dev-User"] = stored.devUser === "admin" ? "admin" : "user";
  return headers;
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
    response = await fetch(await apiBase() + "/form-instances", { headers: await authHeaders(false) });
  } catch {
    show("The API is not running.");
    return;
  }
  const account = document.getElementById("account");
  if (response.status === 401) {
    if (account) account.textContent = "Open the website and sign in. This extension uses that account.";
    show("Sign in on the website first.");
    return;
  }
  if (response.status === 403) {
    show("Filling is for a user account. Switch to the user on the website.");
    return;
  }
  if (!response.ok) {
    show("No records yet.");
    return;
  }
  const me = await fetch(await apiBase() + "/me", { headers: await authHeaders(false) });
  if (account && me.ok) {
    const person = await me.json();
    account.textContent = person.email;
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
    button.textContent = instance.formAddress
      ? `${instance.label || instance.typeName} · ${instance.formAddress}`
      : `${instance.label || instance.typeName} · ${instance.typeName}`;
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
    const planResponse = await fetch(await apiBase() + "/form-instances/" + selected.id + "/fill-plan", {
      headers: await authHeaders(false)
    });
    const plan = planResponse.ok ? await planResponse.json() : { steps: [] };
    const labels = (plan.steps || []).map((step) => step.pageText).filter(Boolean);
    const structure = await chrome.debugger.sendCommand(target, "Runtime.evaluate", {
      expression: `(${pageHas})(${JSON.stringify(labels)})`,
      returnByValue: true
    });
    const missing = structure.result?.value || [];
    if (labels.length > 0 && missing.length > labels.length / 2) {
      const summary = "The open page does not match the published model. Missing: " + missing.join(", ");
      show(summary);
      if (plan.formId) {
        await fetch(await apiBase() + "/form-changes", {
          method: "POST",
          headers: await authHeaders(true),
          body: JSON.stringify({ organizationId: selected.organizationId, formId: plan.formId, text: summary })
        });
      }
      return;
    }
    const expression = `(${executePlan})(${JSON.stringify(plan.steps || [])})`;
    const result = await chrome.debugger.sendCommand(target, "Runtime.evaluate", {
      expression,
      returnByValue: true
    });
    const lines = (result.result?.value || []).map((item) => `${item.name}: ${item.status}`);
    for (const item of result.result?.value || []) {
      if (item.status !== "not found") continue;
      const property = (selected.properties || []).find((entry) => entry.name === item.name);
      if (!property?.pageLabel) continue;
      const shot = await chrome.debugger.sendCommand(target, "Page.captureScreenshot", { format: "jpeg" });
      const point = await fetch(await apiBase() + "/computer-use", {
        method: "POST",
        headers: await authHeaders(true),
        body: JSON.stringify({ screenshot: shot.data, pageLabel: property.pageLabel })
      }).then((response) => response.json());
      if (!point.connected) continue;
      await chrome.debugger.sendCommand(target, "Input.dispatchMouseEvent", { type: "mousePressed", x: point.x, y: point.y, button: "left", clickCount: 1 });
      await chrome.debugger.sendCommand(target, "Input.dispatchMouseEvent", { type: "mouseReleased", x: point.x, y: point.y, button: "left", clickCount: 1 });
      item.status = "clicked from the screen";
    }
    const summary = (result.result?.value || []).map((item) => `${item.name}: ${item.status}`).join("\n") || "Nothing to enter.";
    show(summary);
    await fetch(await apiBase() + "/filled-forms", {
      method: "POST",
      headers: await authHeaders(true),
      body: JSON.stringify({
        organizationId: selected.organizationId,
        summary,
        formAddress: plan.address || selected.formAddress || "",
        recordLabel: selected.label || selected.typeName || "",
        instanceId: selected.id
      })
    });
    if ((result.result?.value || []).some((item) => item.status === "not found") && plan.formId) {
      await fetch(await apiBase() + "/form-changes", {
        method: "POST",
        headers: await authHeaders(true),
        body: JSON.stringify({
          organizationId: selected.organizationId,
          formId: plan.formId,
          text: `The open page does not match the published model. ${summary}`
        })
      });
    }
  } catch (error) {
    show(error.message || "Could not enter the record.");
  } finally {
    try { await chrome.debugger.detach(target); } catch { /* already detached */ }
  }
});

function pageHas(labels) {
  const text = (value) => (value || "").replace(/\s+/g, " ").trim().toLowerCase();
  const page = text(document.body.innerText);
  return labels.filter((label) => label && !page.includes(text(label)));
}

function executePlan(steps) {
  const text = (value) => (value || "").replace(/\s+/g, " ").trim();
  const same = (left, right) => text(left).toLowerCase() === text(right).toLowerCase();
  const seen = (element) => element.getClientRects().length > 0;
  const outcome = [];
  for (const step of steps) {
    const frame = window.frames[step.frame] || window;
    const root = frame.document || document;
    let control = null;
    if (step.selector) {
      try { control = root.querySelector(step.selector); } catch { control = null; }
    }
    if (!control && step.pageText) {
      const label = [...root.querySelectorAll("label, legend, span, p, div")].find((element) => seen(element) && same(element.innerText, step.pageText));
      const box = label?.closest("fieldset, section, form, li, div") || label?.parentElement;
      control = box?.querySelector("input:not([type=hidden]), textarea, select") || null;
    }
    if (step.incompleteList) {
      const match = [...root.querySelectorAll("option, [role=option], li")].find((element) => seen(element) && (same(element.innerText, step.value) || text(element.innerText).toLowerCase().includes(text(step.value).toLowerCase())));
      if (match) {
        match.click();
        outcome.push({ name: step.property, status: "matched on the page" });
        continue;
      }
    }
    if (!control) {
      outcome.push({ name: step.property, status: step.shownConditionally ? "hidden" : "not found" });
      continue;
    }
    if (control instanceof (frame.HTMLSelectElement || HTMLSelectElement)) {
      const options = [...control.options];
      const match = options.find((item) => same(item.text, step.value) || same(item.value, step.value));
      if (!match) {
        outcome.push({ name: step.property, status: "not found" });
        continue;
      }
      control.value = match.value;
    } else if (step.optionTexts && step.optionTexts.some((item) => same(item, step.value))) {
      const choice = [...root.querySelectorAll("label, [role=radio], [role=option], li, button")].find((element) => seen(element) && same(element.innerText, step.value));
      if (!choice) {
        outcome.push({ name: step.property, status: "not found" });
        continue;
      }
      choice.click();
      outcome.push({ name: step.property, status: "clicked" });
      continue;
    } else {
      const prototype = control instanceof (frame.HTMLTextAreaElement || HTMLTextAreaElement) ? (frame.HTMLTextAreaElement || HTMLTextAreaElement).prototype : (frame.HTMLInputElement || HTMLInputElement).prototype;
      Object.getOwnPropertyDescriptor(prototype, "value").set.call(control, String(step.value));
    }
    control.dispatchEvent(new Event("input", { bubbles: true }));
    control.dispatchEvent(new Event("change", { bubbles: true }));
    outcome.push({ name: step.property, status: "entered" });
  }
  return outcome;
}

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
    const option = property.incomplete
      ? [...document.querySelectorAll("option, [role='option'], li")].filter((element) => seen(element) && (same(element.innerText, value) || text(element.innerText).includes(text(value)) || text(value).includes(text(element.innerText)))).sort((left, right) => text(left.innerText).length - text(right.innerText).length)[0]
      : (property.options || []).find((item) => same(item, value));
    const choice = property.incomplete ? option : option ? exact(option) : null;
    if (choice) {
      choice.click();
      outcome.push({ name: property.name, status: property.incomplete ? "matched on the page" : "clicked" });
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

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && (changes.token || changes.devUser)) load();
});
load();
