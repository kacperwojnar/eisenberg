import argparse
import json
from pathlib import Path

from eisenberg_tools.reading import plain

SYSTEM = (
    "You build records of a form model from the readings of every document in the set. "
    "Return one JSON object and nothing else, with an instances array. "
    "For a complete choice list, the value is one of the stored keys."
)


def line(model, lists, documents, records):
    user = {
        "model": model,
        "choiceLists": lists,
        "documents": [{"name": name, "text": plain(text)} for name, text in documents],
    }
    assistant = {
        "instances": [
            {
                "type": record["type"],
                "subclass": record.get("subclass"),
                "label": record.get("label") or record["type"],
                "values": record.get("values") or {},
                "sources": {
                    prop: {
                        "document": source.get("fileName") or "",
                        "page": source.get("page"),
                        "location": source.get("location") or "",
                    }
                    for prop, source in (record.get("sources") or {}).items()
                },
            }
            for record in records
        ]
    }
    return {
        "messages": [
            {"role": "system", "content": SYSTEM},
            {"role": "user", "content": json.dumps(user, ensure_ascii=False)},
            {"role": "assistant", "content": json.dumps(assistant, ensure_ascii=False)},
        ]
    }


def main():
    parser = argparse.ArgumentParser(description="Export corrected records as a Foundry training file.")
    parser.add_argument("pack", type=Path)
    parser.add_argument("--out", type=Path, default=Path("training.jsonl"))
    args = parser.parse_args()
    folder = args.pack
    model = json.loads((folder / "model.json").read_text(encoding="utf-8"))
    lists = json.loads((folder / "choices.json").read_text(encoding="utf-8")) if (folder / "choices.json").exists() else []
    documents = [(path.name, path.read_text(encoding="utf-8")) for path in sorted((folder / "documents").iterdir())]
    corrected = json.loads((folder / "corrected.json").read_text(encoding="utf-8"))
    args.out.write_text(json.dumps(line(model, lists, documents, corrected), ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{args.out} ({len(corrected)} records)")


if __name__ == "__main__":
    main()
