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
  const rowsIn = (source) => {
    const roles = [...source.querySelectorAll("[role=option], [role=menuitem]")];
    if (roles.length) return roles;
    for (const list of [source, ...source.querySelectorAll("ul, ol")]) {
      const items = [...list.children].filter((child) => child.tagName === "LI" && clean(child.textContent));
      if (items.length >= 2) return items;
    }
    let best = [];
    const parents = new Set([...source.querySelectorAll("div, li")].map((node) => node.parentElement).filter(Boolean));
    parents.add(source);
    for (const parent of parents) {
      const children = [...parent.children].filter((child) => {
        if (child.tagName !== "DIV" && child.tagName !== "LI") return false;
        const text = clean(child.innerText || child.textContent);
        return text && text.length <= 200 && !child.querySelector("input, select, textarea");
      });
      if (children.length > best.length) best = children;
    }
    return best.length >= 2 ? best : [];
  };
  const seen = new Map();
  const read = () => {
    for (const source of sources) {
      for (const option of rowsIn(source)) {
        const text = clean(option.getAttribute("aria-label") || option.innerText || option.textContent);
        const value = clean(option.getAttribute("data-value") || option.getAttribute("value") || option.id || text);
        if (value && !seen.has(value)) seen.set(value, { value, text });
      }
    }
  };
  const scrollers = new Set();
  for (const source of sources) {
    for (const node of [source, ...source.querySelectorAll("*")]) {
      if (node.scrollHeight > node.clientHeight + 4) scrollers.add(node);
    }
  }
  read();
  for (const source of scrollers) {
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
