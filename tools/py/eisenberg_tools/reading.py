import json
import re


def plain(reading):
    if isinstance(reading, dict):
        text = reading.get("text")
        return text if isinstance(text, str) else json.dumps(reading)
    if not isinstance(reading, str):
        return ""
    try:
        document = json.loads(reading)
    except json.JSONDecodeError:
        return reading
    if isinstance(document, dict) and isinstance(document.get("text"), str):
        return document["text"]
    return reading


def load_json(value):
    if isinstance(value, str):
        text = value.strip()
        if not text:
            return None
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            return None
    return value


def polygons(location):
    """Points of every polygon found in a Content Understanding source."""
    found = []

    def walk(node):
        if isinstance(node, dict):
            handled = set()
            polygon = node.get("polygon")
            if isinstance(polygon, list) and len(polygon) >= 4 and all(isinstance(item, (int, float)) for item in polygon):
                found.append([(polygon[i], polygon[i + 1]) for i in range(0, len(polygon) - 1, 2)])
                handled.add("polygon")
            box = node.get("boundingBox") or node.get("bounding_box")
            if isinstance(box, dict) and all(key in box for key in ("x", "y", "width", "height")):
                x, y, width, height = box["x"], box["y"], box["width"], box["height"]
                found.append([(x, y), (x + width, y), (x + width, y + height), (x, y + height)])
                handled.update(("boundingBox", "bounding_box"))
            for key, child in node.items():
                if key not in handled:
                    walk(child)
        elif isinstance(node, list):
            if len(node) >= 4 and all(isinstance(item, (int, float)) for item in node):
                found.append([(node[i], node[i + 1]) for i in range(0, len(node) - 1, 2)])
            else:
                for child in node:
                    walk(child)

    walk(load_json(location))
    return found


def segments(reading):
    document = reading if isinstance(reading, dict) else load_json(reading)
    rows = []
    if isinstance(document, dict) and isinstance(document.get("segments"), list):
        for segment in document["segments"]:
            if not isinstance(segment, dict):
                continue
            rows.append({
                "page": segment.get("page") or 1,
                "text": segment.get("text") or "",
                "polygons": polygons(segment.get("location")),
            })
    if rows:
        return rows
    words = []
    contents = document.get("contents") if isinstance(document, dict) else None
    if isinstance(contents, list):
        for content in contents:
            for page in content.get("pages") or []:
                number = page.get("pageNumber") or 1
                for word in page.get("words") or []:
                    words.append({
                        "page": number,
                        "text": word.get("content") or "",
                        "polygons": polygons(word.get("source") or word.get("polygon")),
                    })
    return words


def pages_of(reading):
    grouped = {}
    for segment in segments(reading):
        grouped.setdefault(segment["page"], []).append(segment)
    return grouped
