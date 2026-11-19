const log = document.getElementById("log");
const model = document.getElementById("model");

document.getElementById("read-form").addEventListener("click", async () => {
  if (!chrome.scripting) {
    model.textContent = "On the extensions page, click Reload. The version must say 0.2.1.";
    return;
  }
  const tabs = await chrome.tabs.query({ active: true });
  const tab = tabs.find((item) => item.url && /^https?:/i.test(item.url));
  if (!tab?.id) {
    model.textContent = "Open the form page first. This cannot read edge:// or chrome:// pages.";
    return;
  }
  const results = await chrome.scripting.executeScript({
    target: { tabId: tab.id, allFrames: true },
    func: () => {
      const clean = (value) => (value || "").replace(/\s+/g, " ").trim();
      const googleForm = () => {
        const model = {};
        for (const item of document.querySelectorAll('[role="listitem"]')) {
          const heading = item.querySelector('[role="heading"]');
          const key = clean(heading?.innerText).replace(/\s*\*$/, "");
          if (!key || key.length > 300) continue;
          const radios = [...item.querySelectorAll('[role="radio"]')];
          const checks = [...item.querySelectorAll('[role="checkbox"]')];
          const input = item.querySelector("textarea, input:not([type=hidden])");
          const chosen = radios.find((entry) => entry.getAttribute("aria-checked") === "true");
          const checked = checks.filter((entry) => entry.getAttribute("aria-checked") === "true");
          const choiceText = (entry) => clean(entry.getAttribute("data-answer-value") || entry.getAttribute("aria-label") || entry.innerText);
          if (radios.length) model[key] = chosen ? choiceText(chosen) : "";
          else if (checks.length) model[key] = checked.map(choiceText);
          else if (input) model[key] = input.value || "";
          else {
            const selected = item.querySelector('[aria-selected="true"], [role="option"][aria-selected="true"]');
            model[key] = selected ? clean(selected.getAttribute("aria-label") || selected.innerText) : "";
          }
        }
        return model;
      };
      const textOnly = (node) => {
        if (!node || node.querySelector?.("input, select, textarea, [role=radio], [role=checkbox]")) return "";
        const text = clean(node.innerText);
        if (!text || text.length > 80 || /strzałek|naciśnij enter/i.test(text)) return "";
        return text;
      };
      const labelFor = (element) => {
        if (element.labels && element.labels.length > 0) return clean(element.labels[0].innerText);
        const aria = element.getAttribute("aria-label");
        if (aria) return clean(aria);
        if (element.placeholder) return clean(element.placeholder);
        let node = element;
        while (node && node !== document.body) {
          let previous = node.previousElementSibling;
          while (previous) {
            const text = textOnly(previous);
            if (text) return text;
            previous = previous.previousElementSibling;
          }
          node = node.parentElement;
        }
        return "";
      };
      const questions = googleForm();
      if (Object.keys(questions).length > 0) return { url: location.href, title: document.title, model: questions };
      const model = {};
      const consumed = new Set();
      for (const group of document.querySelectorAll('[role="radiogroup"]')) {
        if (group.closest("[id*='Cookiebot']")) continue;
        const selected = group.querySelector("[aria-checked='true'], input:checked");
        model[labelFor(group) || "choice"] = clean(selected?.innerText || "");
        group.querySelectorAll("[role='radio'], input").forEach((entry) => consumed.add(entry));
      }
      const looseRadios = [...document.querySelectorAll("input[type='radio']")].filter((entry) => !consumed.has(entry) && !entry.closest("[id*='Cookiebot']"));
      const byName = new Map();
      for (const entry of looseRadios) {
        const key = entry.name || entry.id || "radio";
        if (!byName.has(key)) byName.set(key, []);
        byName.get(key).push(entry);
      }
      for (const [name, entries] of byName) {
        entries.forEach((entry) => consumed.add(entry));
        const selected = entries.find((entry) => entry.checked);
        const key = labelFor(entries[0]) || name;
        model[key] = selected ? clean(selected.closest("li, label")?.innerText || selected.value) : "";
      }
      const put = (element, value) => {
        let key = labelFor(element) || element.name || element.id;
        if (!key) return;
        if (Object.prototype.hasOwnProperty.call(model, key) && element instanceof HTMLSelectElement) {
          const hint = clean(element.options[0]?.text);
          if (hint) key = `${key} (${hint})`;
        }
        model[key] = value;
      };
      for (const element of document.querySelectorAll("input, textarea, select")) {
        if (consumed.has(element) || element.closest("[id*='Cookiebot']")) continue;
        if (element instanceof HTMLInputElement) {
          const type = (element.type || "text").toLowerCase();
          if (["hidden", "submit", "button", "reset", "image", "password", "file"].includes(type)) continue;
          if (type === "checkbox") put(element, element.checked);
          else if (type === "radio") { if (element.checked) put(element, element.value); }
          else put(element, element.value);
        } else if (element instanceof HTMLTextAreaElement) {
          put(element, element.value);
        } else if (element instanceof HTMLSelectElement) {
          put(element, element.selectedOptions[0] ? element.selectedOptions[0].text.trim() : "");
        }
      }
      return { url: location.href, title: document.title, model };
    }
  });
  const pages = results.map((entry) => entry.result).filter((entry) => entry && Object.keys(entry.model || {}).length > 0);
  pages.sort((left, right) => Object.keys(right.model).length - Object.keys(left.model).length);
  model.textContent = JSON.stringify(pages[0] || { url: tab.url, title: tab.title, model: {} }, null, 2);
});

async function apiBase() {
  const stored = await chrome.storage.sync.get({ apiBase: "http://localhost:5000" });
  return stored.apiBase;
}

function add(text) {
  const p = document.createElement("p");
  p.textContent = text;
  log.appendChild(p);
}

document.getElementById("chat").addEventListener("submit", async (event) => {
  event.preventDefault();
  const message = document.getElementById("message").value;
  add(message);
  const response = await fetch(await apiBase() + "/chat", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-Dev-User": "user"
    },
    body: JSON.stringify({ message })
  });
  const body = await response.json();
  add(body.reply);
  document.getElementById("message").value = "";
});

