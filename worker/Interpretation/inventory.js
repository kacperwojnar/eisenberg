(args) => {
  const maxControls = args.maxControls || 1500;
  const maxOptions = args.maxOptions || 3000;
  const maxClickables = 300;
  const clean = (value) => (value || "").replace(/\s+/g, " ").trim();
  const cut = (value, length = 300) => {
    const text = clean(value);
    return text.length > length ? text.slice(0, length) + "…" : text;
  };
  const quote = (value) => String(value).replace(/\\/g, "\\\\").replace(/"/g, '\\"');

  const elements = [];
  const visit = (root) => {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      elements.push(node);
      if (node.shadowRoot) visit(node.shadowRoot);
    }
  };
  visit(document);

  const rendered = (element) => {
    if (!element || !element.isConnected || element.getClientRects().length === 0) return false;
    const style = getComputedStyle(element);
    if (style.visibility === "hidden" || style.visibility === "collapse") return false;
    const box = element.getBoundingClientRect();
    return box.width > 0 || box.height > 0;
  };

  const textWithoutControls = (element) => {
    if (!element) return "";
    const copy = element.cloneNode(true);
    copy.querySelectorAll("select, textarea, input, button, [role=listbox], [role=combobox], script, style").forEach((node) => node.remove());
    return cut(copy.textContent);
  };
  const byId = (element, id) => {
    const root = element.getRootNode();
    return (root.getElementById && root.getElementById(id)) || document.getElementById(id);
  };
  const labelledBy = (element) => clean((element.getAttribute("aria-labelledby") || "").split(/\s+/).filter(Boolean)
    .map((id) => textWithoutControls(byId(element, id))).join(" "));
  const labelsOf = (element) => clean([...(element.labels || [])].map(textWithoutControls).join(" "));
  const nameFromContent = (element) => element.tagName === "BUTTON" ||
    ["button", "checkbox", "radio", "switch", "tab", "option", "menuitemcheckbox", "menuitemradio"].includes((element.getAttribute("role") || "").toLowerCase());
  const accessibleName = (element) => cut(labelledBy(element) || element.getAttribute("aria-label") || labelsOf(element) ||
    element.getAttribute("title") || element.getAttribute("placeholder") || (nameFromContent(element) ? textWithoutControls(element) : ""));
  const cookieSelector = "[id*=cookie i], [class*=cookie i], [id*=onetrust i], [id*=didomi i], [id*=usercentrics i], [class*=qc-cmp i], [id^=sp_message i], [id*=truste i], [aria-label*=cookie i]";
  let cookieBanner = false;

  const questionText = (element, own, sectionHeading) => {
    let node = element;
    for (let depth = 0; depth < 6; depth++) {
      node = node.parentElement || (node.getRootNode() && node.getRootNode().host) || null;
      if (!node) return "";
      const parts = [];
      const headingParts = [];
      const walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
      for (let text = walker.nextNode(); text; text = walker.nextNode()) {
        if (element.contains(text)) continue;
        if (!(text.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING)) break;
        const parent = text.parentElement;
        if (!parent || parent.closest("script, style, select, option, button, [role=option], [role=listbox]")) continue;
        const label = parent.closest("label");
        if (label && label.control && !own.includes(label.control)) continue;
        const legend = parent.closest("legend");
        if (legend && legend.parentElement && !legend.parentElement.contains(element)) continue;
        if (!rendered(parent)) continue;
        const value = clean(text.textContent);
        if (!value) continue;
        if (parent.closest("h1, h2, h3, h4, h5, h6, [role=heading]")) headingParts.push(value);
        else parts.push(value);
      }
      const joined = clean((parts.length ? parts : headingParts).join(" "));
      if (!joined) continue;
      return joined === sectionHeading ? "" : cut(joined.length > 160 ? joined.slice(-160) : joined, 200);
    }
    return "";
  };
  const commonAncestor = (nodes) => {
    let ancestor = nodes[0].parentElement;
    while (ancestor && !nodes.every((node) => ancestor.contains(node))) ancestor = ancestor.parentElement;
    return ancestor || nodes[0];
  };

  const unique = (root, selector) => {
    try { return root.querySelectorAll(selector).length === 1; } catch { return false; }
  };
  const generatedId = (id) => /\d{4,}|[a-f0-9]{8,}|^(ember|react|mui|radix|headlessui|ng-)|:r[0-9a-z]+:/i.test(id);
  const localSelector = (element) => {
    const root = element.getRootNode();
    const tag = element.tagName.toLowerCase();
    if (element.id && !generatedId(element.id) && unique(root, "#" + CSS.escape(element.id))) return "#" + CSS.escape(element.id);
    for (const attribute of ["data-testid", "data-test", "data-qa", "data-cy", "name", "aria-label", "placeholder"]) {
      const value = element.getAttribute(attribute);
      if (!value) continue;
      let selector = `${tag}[${attribute}="${quote(value)}"]`;
      if (tag === "input" && (element.type === "radio" || element.type === "checkbox") && element.value) selector += `[value="${quote(element.value)}"]`;
      if (unique(root, selector)) return selector;
    }
    const parts = [];
    for (let node = element; node && node.nodeType === 1; node = node.parentElement) {
      if (node !== element && node.id && !generatedId(node.id) && unique(root, "#" + CSS.escape(node.id))) {
        parts.unshift("#" + CSS.escape(node.id));
        break;
      }
      let part = node.tagName.toLowerCase();
      const parent = node.parentElement;
      if (parent) {
        const same = [...parent.children].filter((child) => child.tagName === node.tagName);
        if (same.length > 1) part += `:nth-of-type(${same.indexOf(node) + 1})`;
      }
      parts.unshift(part);
    }
    return parts.join(" > ");
  };
  const selectorOf = (element) => {
    const chain = [localSelector(element)];
    for (let root = element.getRootNode(); root && root.host; root = root.host.getRootNode()) chain.unshift(localSelector(root.host));
    return chain.join(" >> ");
  };

  const required = (element, name) => !!(element.required || element.getAttribute("aria-required") === "true" || /\*\s*$/.test(name || ""));
  const constraints = (element) => {
    const result = {};
    for (const attribute of ["min", "max", "step", "minlength", "maxlength", "pattern", "inputmode", "autocomplete", "accept"]) {
      const value = element.getAttribute(attribute);
      if (value !== null && value !== "" && value !== "-1") result[attribute] = value;
    }
    if (element.multiple) result.multiple = true;
    if (element.readOnly || element.getAttribute("aria-readonly") === "true") result.readonly = true;
    for (const attribute of ["aria-valuemin", "aria-valuemax", "aria-valuenow", "aria-valuetext"]) {
      const value = element.getAttribute(attribute);
      if (value !== null && value !== "") result[attribute.slice("aria-".length)] = value;
    }
    return result;
  };
  const disabled = (element) => !!(element.disabled || element.getAttribute("aria-disabled") === "true");
  const rowsIn = (source) => {
    const roles = [...source.querySelectorAll("[role=option], [role=menuitem]")];
    if (roles.length) return roles;
    const lists = [source, ...source.querySelectorAll("ul, ol")];
    for (const list of lists) {
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
  const shallowRows = (element) => {
    const text = (node) => clean(node.innerText || node.textContent);
    const usable = (node) => text(node) && text(node).length <= 200;
    const direct = [...element.querySelectorAll(":scope > [role=option], :scope > [role=menuitem], :scope > li")].filter(usable);
    if (direct.length >= 2) return direct;
    const nested = [...element.querySelectorAll(":scope > * > [role=option], :scope > * > [role=menuitem], :scope > ul > li, :scope > ol > li, :scope > * > li")].filter(usable);
    return nested.length >= 2 ? nested : [];
  };
  const optionOf = (element) => ({
    value: clean(element.getAttribute("data-value") || element.getAttribute("value") || element.id || element.textContent),
    text: cut(element.getAttribute("aria-label") || element.textContent, 200),
    selector: selectorOf(element),
    disabled: disabled(element)
  });

  const nativeKind = (element) => {
    const tag = element.tagName;
    if (tag === "SELECT") return element.multiple ? "multi-select" : "select";
    if (tag === "TEXTAREA") return "text";
    if (tag !== "INPUT") return null;
    const type = (element.type || "text").toLowerCase();
    if (["hidden", "submit", "button", "reset", "image"].includes(type)) return null;
    if (type === "checkbox") return "checkbox";
    if (type === "radio") return "radio";
    if (type === "range") return "slider";
    if (type === "number") return "number";
    if (type === "file") return "file";
    if (["date", "datetime-local", "month", "week", "time"].includes(type)) return "date";
    return "text";
  };
  const roleKinds = {
    textbox: "text", searchbox: "text", combobox: "combobox", listbox: "listbox", checkbox: "checkbox",
    switch: "switch", slider: "slider", spinbutton: "number", radio: "radio", menuitemcheckbox: "checkbox",
    menuitemradio: "radio", tab: "tab"
  };
  const kindOf = (element) => {
    const role = (element.getAttribute("role") || "").toLowerCase();
    const native = nativeKind(element);
    if (native) return native === "text" && role === "combobox" ? "combobox" : native;
    if (roleKinds[role]) return roleKinds[role];
    const editable = element.getAttribute("contenteditable");
    if (editable === "" || editable === "true" || editable === "plaintext-only") return "text";
    if (element.tagName === "BUTTON" || role === "button") return element.hasAttribute("aria-pressed") ? "toggle" : null;
    return null;
  };

  const headings = [];
  const controls = [];
  const groups = new Map();
  const members = new Map();
  const buttons = [];
  const clickables = [];
  const errors = [];
  let heading = "";
  let truncatedControls = false;
  let truncatedClickables = false;

  for (const element of elements) {
    const tag = element.tagName;
    const role = (element.getAttribute("role") || "").toLowerCase();
    if (/^H[1-4]$/.test(tag) || role === "heading") {
      if (rendered(element)) {
        heading = cut(element.textContent, 200);
        if (heading && headings.length < 30) headings.push(heading);
      }
      continue;
    }
    const className = typeof element.className === "string" ? element.className : "";
    const invalid = element.getAttribute("aria-invalid") === "true";
    if ((role === "alert" || invalid || (/\b(error|invalid)\b/i.test(className) && clean(element.textContent).length <= 150)) && rendered(element)) {
      const text = cut(invalid ? accessibleName(element) + " (invalid)" : element.textContent, 200);
      if (text && errors.length < 40 && !errors.includes(text)) errors.push(text);
    }

    const kind = kindOf(element);
    if ((kind || tag === "BUTTON") && element.closest(cookieSelector)) {
      if (rendered(element)) cookieBanner = true;
      continue;
    }
    if (!kind) {
      const isButton = tag === "BUTTON" || role === "button" || (tag === "INPUT" && ["submit", "button", "reset", "image"].includes((element.type || "").toLowerCase()));
      if (isButton) {
        if (rendered(element) && buttons.length < 300) {
          buttons.push({
            text: cut(element.textContent || element.value || element.getAttribute("aria-label") || element.getAttribute("title"), 120),
            selector: selectorOf(element),
            type: (element.getAttribute("type") || "").toLowerCase(),
            submits: !!(element.form && (element.type === "submit" || element.type === "image")),
            expanded: element.getAttribute("aria-expanded"),
            disabled: disabled(element)
          });
        }
        continue;
      }
      if (tag === "A" || element.closest("select, button, a, label, [role=listbox], [role=option]")) continue;
      if (clickables.length >= maxClickables) { truncatedClickables = true; continue; }
      if (getComputedStyle(element).cursor !== "pointer") continue;
      const parent = element.parentElement;
      if (parent && getComputedStyle(parent).cursor === "pointer") continue;
      if (element.querySelector("input, select, textarea, button")) continue;
      const text = cut(element.textContent, 120);
      if (text && rendered(element)) clickables.push({ text, selector: selectorOf(element), heading });
      continue;
    }
    if (kind === "option") continue;

    const own = rendered(element);
    const labelVisible = [...(element.labels || [])].some(rendered);
    if (!own && !labelVisible) continue;
    if (controls.length >= maxControls) { truncatedControls = true; continue; }

    const name = accessibleName(element);
    const entry = {
      kind,
      tag: tag.toLowerCase(),
      type: (element.getAttribute("type") || "").toLowerCase(),
      role,
      id: element.id || "",
      nameAttribute: element.getAttribute("name") || "",
      testId: element.getAttribute("data-testid") || element.getAttribute("data-test") || element.getAttribute("data-qa") || "",
      name,
      question: "",
      heading,
      selector: selectorOf(element),
      required: required(element, name),
      disabled: disabled(element),
      constraints: constraints(element),
      hasValue: false
    };

    if (kind === "select" || kind === "multi-select") {
      const options = [...element.options];
      entry.options = options.slice(0, maxOptions).map((option) => ({
        value: option.value, text: cut(option.text, 200), disabled: option.disabled,
        group: option.parentElement && option.parentElement.tagName === "OPTGROUP" ? option.parentElement.label : undefined
      }));
      entry.optionsComplete = options.length <= maxOptions;
      entry.optionCount = options.length;
      entry.selected = options.filter((option) => option.selected).map((option) => option.value);
      entry.hasValue = options.some((option) => option.selected && option.value !== "");
      entry.question = questionText(element, [element], heading);
      controls.push(entry);
    } else if (kind === "radio" || (kind === "checkbox" && tag === "INPUT" && element.name)) {
      const container = element.closest("[role=radiogroup], [role=group], fieldset") ;
      const groupKey = kind + "|" + (element.name || "") + "|" + (element.form ? localSelector(element.form) : "") + "|" + (element.name ? "" : container ? localSelector(container) : localSelector(element.parentElement || element));
      let group = groups.get(groupKey);
      if (!group) {
        const legend = container && (container.tagName === "FIELDSET" ? container.querySelector("legend") : null);
        group = {
          ...entry,
          kind: kind === "radio" ? "radio-group" : "checkbox-group",
          name: cut((container && (labelledBy(container) || container.getAttribute("aria-label"))) || (legend ? textWithoutControls(legend) : "")),
          selector: container ? selectorOf(container) : entry.selector,
          options: [],
          selected: [],
          optionsComplete: true
        };
        groups.set(groupKey, group);
        members.set(group, []);
        controls.push(group);
      }
      members.get(group).push(element);
      const value = tag === "INPUT" ? element.value : (element.getAttribute("data-value") || element.id || name);
      group.options.push({ value, text: name || cut(element.value, 200), selector: entry.selector, disabled: entry.disabled });
      const checked = element.checked || element.getAttribute("aria-checked") === "true";
      if (checked) { group.selected.push(value); group.hasValue = true; }
      group.required = group.required || entry.required;
    } else if (kind === "listbox" || kind === "combobox") {
      const owned = (element.getAttribute("aria-controls") || element.getAttribute("aria-owns") || "").split(/\s+/).filter(Boolean)
        .map((id) => byId(element, id)).filter(Boolean);
      const sources = kind === "listbox" ? [element, ...owned] : owned;
      const nodes = [];
      const seenRows = new Set();
      for (const source of sources) {
        for (const row of rowsIn(source)) {
          if (seenRows.has(row)) continue;
          seenRows.add(row);
          nodes.push(row);
        }
      }
      const options = nodes.map(optionOf);
      entry.scrollable = sources.some((source) => source.scrollHeight > source.clientHeight + 4);
      if (tag === "INPUT" && element.list) for (const option of element.list.options) options.push({ value: option.value, text: cut(option.label || option.value, 200), disabled: option.disabled });
      entry.options = options.slice(0, maxOptions);
      entry.optionsComplete = options.length > 0 && options.length <= maxOptions && !entry.scrollable;
      entry.optionCount = options.length;
      entry.expanded = element.getAttribute("aria-expanded");
      entry.selected = nodes.filter((node) => node.getAttribute("aria-selected") === "true").map(optionOf).map((option) => option.value);
      entry.hasValue = tag === "INPUT" ? element.value !== "" : entry.selected.length > 0;
      entry.question = questionText(element, [element], heading);
      controls.push(entry);
    } else if (kind === "tab") {
      const container = element.closest("[role=tablist]") || element.parentElement;
      const groupKey = "tab|" + localSelector(container);
      let group = groups.get(groupKey);
      if (!group) {
        group = { ...entry, kind: "tab-group", name: cut(container.getAttribute("aria-label") || ""), selector: selectorOf(container), options: [], selected: [], optionsComplete: true };
        groups.set(groupKey, group);
        members.set(group, []);
        controls.push(group);
      }
      members.get(group).push(element);
      group.options.push({ value: name || element.id, text: name, selector: entry.selector, disabled: entry.disabled });
      if (element.getAttribute("aria-selected") === "true") group.selected.push(name || element.id);
    } else {
      if (tag === "INPUT" && element.list) {
        const options = [...element.list.options];
        entry.options = options.slice(0, maxOptions).map((option) => ({
          value: option.value, text: cut(option.label || option.value, 200), disabled: option.disabled
        }));
        entry.optionsComplete = options.length <= maxOptions;
        entry.optionCount = options.length;
      }
      if (kind === "checkbox" || kind === "switch" || kind === "toggle") {
        const on = element.checked || element.getAttribute("aria-checked") === "true" || element.getAttribute("aria-pressed") === "true";
        entry.options = [{ value: "true", text: "on" }, { value: "false", text: "off" }];
        entry.optionsComplete = true;
        entry.selected = [on ? "true" : "false"];
        entry.hasValue = on;
      } else {
        entry.hasValue = "value" in element ? element.value !== "" : clean(element.textContent) !== "";
        const ariaNow = element.getAttribute("aria-valuenow");
        if ((kind === "slider" || kind === "number") && ariaNow) entry.hasValue = ariaNow !== "";
      }
      entry.question = questionText(element, [element], heading);
      controls.push(entry);
    }
  }

  for (const [group, nodes] of members) group.question = questionText(nodes.length > 1 ? commonAncestor(nodes) : nodes[0], nodes, group.heading);

  const knownOptionSelectors = new Set();
  for (const control of controls) for (const option of control.options || []) if (option.selector) knownOptionSelectors.add(option.selector);
  for (const element of elements) {
    if (controls.length >= maxControls || !rendered(element) || element.closest(cookieSelector)) continue;
    if (element.closest("header, nav, footer, [role=navigation], [role=banner], [role=contentinfo]") || ["HEADER", "NAV", "FOOTER"].includes(element.tagName)) continue;
    const style = getComputedStyle(element);
    const role = (element.getAttribute("role") || "").toLowerCase();
    const overlay = style.position === "fixed" || role === "listbox" || role === "menu" || (style.position === "absolute" && style.zIndex !== "auto");
    if (!overlay) continue;
    const box = element.getBoundingClientRect();
    const coversPage = box.width > window.innerWidth * 0.85 && box.height > window.innerHeight * 0.7;
    if (coversPage && role !== "listbox" && role !== "menu") continue;
    if (role !== "listbox" && role !== "menu" && box.width > window.innerWidth * 0.9 && box.height < 120) continue;
    const rows = (role === "listbox" || role === "menu" ? rowsIn(element) : shallowRows(element)).filter((row) => !knownOptionSelectors.has(selectorOf(row)));
    const linkRows = rows.filter((row) => row.closest("a") || row.querySelector("a[href]"));
    if (rows.length < 2 || controls.some((control) => control.selector === selectorOf(element))) continue;
    if (role !== "listbox" && linkRows.length >= Math.ceil(rows.length * 0.8)) continue;
    const options = rows.slice(0, maxOptions).map(optionOf);
    for (const option of options) if (option.selector) knownOptionSelectors.add(option.selector);
    controls.push({
      kind: "listbox", tag: element.tagName.toLowerCase(), type: "", role, id: element.id || "",
      nameAttribute: element.getAttribute("name") || "",
      testId: element.getAttribute("data-testid") || element.getAttribute("data-test") || element.getAttribute("data-qa") || "",
      name: cut(element.getAttribute("aria-label") || "Open list"), question: "", heading, selector: selectorOf(element),
      required: false, disabled: disabled(element), constraints: {}, options,
      optionsComplete: rows.length <= maxOptions && element.scrollHeight <= element.clientHeight + 4,
      optionCount: rows.length, selected: [], hasValue: false, scrollable: element.scrollHeight > element.clientHeight + 4
    });
  }

  for (let index = 0; index < controls.length; index++) {
    const control = controls[index];
    if (control.kind === "checkbox-group" && control.options.length === 1) {
      const on = control.selected.length > 0;
      controls[index] = {
        ...control, kind: "checkbox", name: control.options[0].text || control.name, selector: control.options[0].selector,
        options: [{ value: "true", text: "on" }, { value: "false", text: "off" }], selected: [on ? "true" : "false"]
      };
    }
  }

  return {
    title: document.title,
    url: location.href,
    headings,
    progress: [...document.querySelectorAll("[aria-current=step], [aria-current=page]")].filter(rendered).slice(0, 5).map((node) => cut(node.textContent, 120)),
    errors,
    cookieBanner,
    controls,
    buttons,
    clickables,
    truncated: { controls: truncatedControls, clickables: truncatedClickables }
  };
}
