import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { JSDOM } from "jsdom";

const here = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.join(here, "../../extension/sidepanel.js"), "utf8");
const start = source.indexOf("function executePlan");
const end = source.indexOf("\nfunction ", start + 1);
const dom = new JSDOM(`<!doctype html>
<form>
  <label>Name</label><input id="name">
  <label>Start</label><input id="start">
  <label>Fuel</label>
  <select id="fuel">
    <option value="petrol">Benzyna</option>
    <option value="diesel">Diesel</option>
  </select>
</form>`, { runScripts: "dangerously" });
const window = dom.window;
const script = window.document.createElement("script");
script.textContent = `${source.slice(start, end)}\nwindow.__executePlan = executePlan;`;
window.document.body.appendChild(script);
const document = window.document;

const steps = [
  { property: "name", value: "Ada Lovelace", pageText: "Name", selector: "#name", frame: 0, kind: "text", incompleteList: false, shownConditionally: false, optionTexts: [] },
  { property: "start", value: "1990-01-02", pageText: "Start", selector: "#start", frame: 0, kind: "date", incompleteList: false, shownConditionally: false, optionTexts: [] },
  { property: "fuel", value: "Benzyna", pageText: "Fuel", selector: "#fuel", frame: 0, kind: "choice", incompleteList: false, shownConditionally: false, optionTexts: ["Benzyna", "Diesel"] }
];

const outcome = window.__executePlan(steps);
const name = document.querySelector("#name").value;
const startValue = document.querySelector("#start").value;
const fuel = document.querySelector("#fuel").value;
const failed = outcome.filter((item) => item.status === "not found" || item.status === "hidden");
if (failed.length || name !== "Ada Lovelace" || startValue !== "1990-01-02" || fuel !== "petrol") {
  console.error(JSON.stringify({ outcome, name, start: startValue, fuel }, null, 2));
  process.exit(1);
}
console.log("Filled the page: name, start, fuel.");
