import json
import re
from datetime import date


def from_readings(model, lists, documents):
    """The same match the worker uses when Foundry is not connected."""
    prepared = [(name, plain) for name, plain in ((name, _plain(text)) for name, text in documents) if plain]
    if not prepared:
        return []
    types = {item["name"]: item for item in model.get("types") or [] if item.get("name")}
    records = []
    for name in types:
        values = {}
        sources = {}
        for prop in properties_of(name, types, lists):
            if prop.get("ignored"):
                continue
            found = choice(prop, prepared) if prop.get("complete") and prop.get("choices") else labeled(prop, prepared)
            if found is None:
                continue
            value, source = found
            values[prop["name"]] = value
            sources[prop["name"]] = source
        if not values or problems(name, types, lists, values):
            continue
        label = values.get("name") or next(iter(values.values()))
        records.append({"type": name, "label": label, "values": values, "sources": sources})
    return records


def properties_of(name, types, lists):
    chain = []
    seen = set()
    current = types.get(name)
    while current and current["name"] not in seen:
        seen.add(current["name"])
        chain.append(current)
        parent = current.get("subclassOf")
        current = types.get(parent) if parent else None
    merged = {}
    for item in reversed(chain):
        for prop in item.get("properties") or []:
            if prop.get("name"):
                merged[prop["name"]] = decorate(prop, lists)
    return list(merged.values())


def decorate(prop, lists):
    choice_id = prop.get("choiceListId")
    stored = next((item for item in lists if item.get("id") == choice_id), None)
    choices = read_choices(stored.get("choices") if stored else None)
    copy = dict(prop)
    copy["choices"] = choices
    copy["complete"] = bool(stored and stored.get("complete"))
    return copy


def read_choices(raw):
    if raw is None:
        return []
    document = raw if isinstance(raw, (list, dict)) else _load(raw)
    array = document if isinstance(document, list) else (document or {}).get("choices") or []
    choices = []
    for item in array:
        if not isinstance(item, dict):
            continue
        value = item.get("value") or item.get("text") or ""
        text = item.get("text") or item.get("value") or ""
        if value or text:
            choices.append({"value": value, "text": text})
    return choices


def choice(prop, documents):
    ranked = sorted(prop["choices"], key=lambda item: max(len(item["text"]), len(item["value"])), reverse=True)
    for name, text in documents:
        for item in ranked:
            if contains(text, item["text"]) or contains(text, item["value"]):
                return item["value"], {"fileName": name, "page": 1, "location": "local text"}
    return None


def labeled(prop, documents):
    labels = []
    for control in prop.get("controls") or []:
        if control.get("pageText") and control["pageText"] not in labels:
            labels.append(control["pageText"])
    if prop.get("name") and prop["name"] not in labels:
        labels.append(prop["name"])
    for name, text in documents:
        for line in text.split("\n"):
            for label in labels:
                match = re.match(rf"^{re.escape(label)}\s*[:\-]\s*(.+)$", line.strip(), re.IGNORECASE)
                if not match:
                    continue
                value = match.group(1).strip()
                if not value:
                    continue
                if prop.get("kind") == "number" and _number(value) is None:
                    continue
                if prop.get("kind") == "date" and _date(value) is None:
                    continue
                return value, {"fileName": name, "page": 1, "location": "local text"}
    return None


def problems(type_name, types, lists, values):
    if type_name not in types:
        return [f"Unknown type {type_name}."]
    found = []
    for prop in properties_of(type_name, types, lists):
        if prop.get("ignored"):
            continue
        value = values.get(prop["name"], "")
        empty = not str(value).strip()
        if prop.get("required") and empty and not prop.get("shownWhen"):
            found.append(f"{prop['name']} is required.")
        if empty:
            continue
        if prop.get("complete") and prop.get("choices"):
            if not any(_same(choice["value"], value) or _same(choice["text"], value) for choice in prop["choices"]):
                found.append(f"{prop['name']} is not one of the stored choices.")
        if prop.get("kind") == "number" and _number(value) is None:
            found.append(f"{prop['name']} is not a number.")
        if prop.get("kind") == "date" and _date(value) is None:
            found.append(f"{prop['name']} is not a date.")
    return found


def contains(text, term):
    if not term or not str(term).strip():
        return False
    if len(term) < 4:
        return re.search(rf"\b{re.escape(term)}\b", text, re.IGNORECASE) is not None
    return term.casefold() in text.casefold()


def _plain(reading):
    from eisenberg_tools.reading import plain
    return plain(reading)


def _load(raw):
    try:
        return json.loads(raw)
    except (TypeError, json.JSONDecodeError):
        return None


def _same(left, right):
    return str(left or "").strip().casefold() == str(right or "").strip().casefold()


def _number(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _date(value):
    try:
        return date.fromisoformat(str(value).strip()[:10])
    except ValueError:
        return None
