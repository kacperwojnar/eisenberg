import argparse
import json
import os
import subprocess
import sys
import urllib.request
from pathlib import Path

from eisenberg_tools.local_records import from_readings
from eisenberg_tools.reading import plain

SYSTEM = """
You build records of a form model from the readings of every document in the set. One record's values may come from different documents. Return one JSON object and nothing else:
{"instances":[{"type":"TypeName","subclass":null,"label":"short name","values":{"propertyName":"value"},"sources":{"propertyName":{"document":"the document name","page":1,"location":"where on the page"}}}]}
Use a type name from the model. subclass is the subclass name or null. For a property whose choice list is complete, the value is one of that list's stored keys, not the visible label. For an incomplete list, use the value as the documents state it. Omit a property when the documents do not contain it. sources names the document, page, and place for each value that the reading locates. Omit a source when the reading does not say where the value is. Return every record that can be built.
""".strip()


def load_pack(folder):
    folder = Path(folder)
    model = json.loads((folder / "model.json").read_text(encoding="utf-8"))
    lists = json.loads((folder / "choices.json").read_text(encoding="utf-8")) if (folder / "choices.json").exists() else []
    documents = []
    for path in sorted((folder / "documents").iterdir()):
        text = path.read_text(encoding="utf-8")
        documents.append((path.name, text))
    expected = json.loads((folder / "expected.json").read_text(encoding="utf-8")) if (folder / "expected.json").exists() else None
    return model, lists, documents, expected


def predict(pack, model, lists, documents, use_foundry):
    if use_foundry:
        return foundry_records(model, lists, documents)
    return worker_records(pack)


def worker_records(pack):
    repo = Path(__file__).resolve().parents[3]
    project = repo / "tools" / "Eisenberg.Pack" / "Eisenberg.Pack.csproj"
    completed = subprocess.run(
        ["dotnet", "run", "--project", str(project), "-v", "q", "--", str(Path(pack).resolve())],
        capture_output=True,
        text=True,
        check=False,
    )
    if completed.returncode != 0:
        detail = completed.stderr.strip()
        if "800711C7" in detail or "Application Control" in detail:
            print("The pack tool was blocked by Windows, so the same rules ran in Python.", file=sys.stderr)
            model, lists, documents, _ = load_pack(pack)
            return from_readings(model, lists, documents)
        raise SystemExit(detail or "The worker pack tool failed.")
    text = completed.stdout.strip()
    start = text.find("[")
    if start < 0:
        raise SystemExit(text or "The worker pack tool returned no records.")
    return json.loads(text[start:])


def foundry_records(model, lists, documents):
    endpoint = os.environ.get("FOUNDRY_ENDPOINT", "").rstrip("/")
    key = os.environ.get("FOUNDRY_API_KEY", "")
    if not endpoint or not key:
        raise SystemExit("FOUNDRY_ENDPOINT and FOUNDRY_API_KEY are required for --foundry.")
    deployment = os.environ.get("FOUNDRY_CHAT_DEPLOYMENT") or "claude-haiku-5-5"
    payload = {
        "model": model,
        "choiceLists": lists,
        "documents": [{"name": name, "text": plain(text)} for name, text in documents],
    }
    body = json.dumps({
        "model": deployment,
        "max_tokens": 16000,
        "system": SYSTEM,
        "messages": [{"role": "user", "content": json.dumps(payload)}],
    }).encode()
    request = urllib.request.Request(
        endpoint + "/anthropic/v1/messages",
        data=body,
        headers={"api-key": key, "anthropic-version": "2023-06-01", "content-type": "application/json"},
    )
    with urllib.request.urlopen(request) as response:
        answer = json.loads(response.read().decode())
    text = next(block.get("text", "") for block in answer.get("content", []) if block.get("text"))
    start, end = text.find("{"), text.rfind("}")
    parsed = json.loads(text[start:end + 1])
    records = []
    for item in parsed.get("instances") or []:
        sources = {}
        for prop, source in (item.get("sources") or {}).items():
            sources[prop] = {
                "fileName": source.get("document") or "",
                "page": source.get("page"),
                "location": source.get("location") or "",
            }
        records.append({"type": item.get("type"), "label": item.get("label"), "values": item.get("values") or {}, "sources": sources})
    return records


def compare(expected, predicted):
    lines = []
    failed = False
    used = []
    for wanted in expected:
        got = next((item for item in predicted if item not in used and item["type"] == wanted["type"] and (not wanted.get("label") or item["label"] == wanted["label"])), None)
        title = wanted.get("label") or wanted["type"]
        if got is None:
            lines.append(f"{title}: record was not built")
            failed = True
            continue
        used.append(got)
        for prop, value in (wanted.get("values") or {}).items():
            actual = got["values"].get(prop)
            if str(actual or "") != str(value):
                lines.append(f"{title}.{prop}: expected {value}, got {actual or 'nothing'}")
                failed = True
            else:
                lines.append(f"{title}.{prop}: {actual}")
        for prop, source in (wanted.get("sources") or {}).items():
            actual = (got.get("sources") or {}).get(prop) or {}
            if source.get("fileName") and actual.get("fileName") != source["fileName"]:
                lines.append(f"{title}.{prop} source: expected {source['fileName']}, got {actual.get('fileName') or 'nothing'}")
                failed = True
            elif source.get("page") and actual.get("page") != source["page"]:
                lines.append(f"{title}.{prop} page: expected {source['page']}, got {actual.get('page') or 'nothing'}")
                failed = True
            else:
                lines.append(f"{title}.{prop} source: {actual.get('fileName')} page {actual.get('page')}")
    for extra in predicted:
        if extra not in used:
            lines.append(f"extra record {extra['type']}: {extra['label']}")
            failed = True
    return failed, lines


def main():
    parser = argparse.ArgumentParser(description="Score records built from a pack of documents.")
    parser.add_argument("pack", type=Path)
    parser.add_argument("--foundry", action="store_true")
    args = parser.parse_args()
    model, lists, documents, expected = load_pack(args.pack)
    predicted = predict(args.pack, model, lists, documents, args.foundry)
    if expected is None:
        print(json.dumps(predicted, indent=2))
        return
    failed, lines = compare(expected, predicted)
    print("\n".join(lines) or "No records were expected.")
    if failed:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
