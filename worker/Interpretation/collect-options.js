async (args) => {
  const clean = (value) => (value || "").replace(/\s+/g, " ").trim();
  let root = document;
  let element = null;
  const parts = args.selector.split(" >> ");
  for (let index = 0; index < parts.length; index++) {
    element = root.querySelector(parts[index]);
    if (!element) return { error: "not found" };
    if (index < parts.length - 1) root = element.shadowRoot || element;
  }
  const owned = (element.getAttribute("aria-controls") || element.getAttribute("aria-owns") || "").split(/\s+/).filter(Boolean)
    .map((id) => document.getElementById(id)).filter(Boolean);
  const sources = [element, ...owned];
  const seen = new Map();
  const read = () => {
    for (const source of sources) {
      for (const option of source.querySelectorAll("[role=option]")) {
        const text = clean(option.getAttribute("aria-label") || option.textContent);
        const value = clean(option.getAttribute("data-value") || option.getAttribute("value") || option.id || text);
        if (!seen.has(value)) seen.set(value, { value, text });
      }
    }
  };
  read();
  for (const source of sources) {
    let last = -1;
    for (let guard = 0; guard < 500 && source.scrollTop !== last; guard++) {
      last = source.scrollTop;
      source.scrollTop = last + Math.max(source.clientHeight * 0.8, 20);
      await new Promise((resolve) => setTimeout(resolve, 80));
      read();
    }
    source.scrollTop = 0;
  }
  return { options: [...seen.values()] };
}
