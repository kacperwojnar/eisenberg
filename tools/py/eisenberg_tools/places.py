import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw

from eisenberg_tools.reading import pages_of


def draw(reading, images, out):
    pages = pages_of(reading)
    if not pages:
        raise SystemExit("The reading has no page to draw.")
    out.mkdir(parents=True, exist_ok=True)
    written = []
    for number, segments in pages.items():
        boxes = [(segment["text"], polygon) for segment in segments for polygon in segment["polygons"]]
        image = images.get(number) or _blank(boxes)
        pen = ImageDraw.Draw(image)
        scale = _scale(boxes, image)
        for text, polygon in boxes:
            points = [(x * scale, y * scale) for x, y in polygon]
            pen.polygon(points, outline=(20, 70, 40))
            if points:
                pen.text(points[0], (text or "")[:48], fill=(20, 20, 20))
        path = out / f"page-{number}.png"
        image.save(path)
        written.append(path)
    return written


def load_document(path):
    path = Path(path)
    if path.suffix.lower() == ".pdf":
        import pypdfium2 as pdfium
        pdf = pdfium.PdfDocument(str(path))
        images = {}
        for index in range(len(pdf)):
            page = pdf[index]
            width_inches = page.get_size()[0] / 72
            bitmap = page.render(scale=100 / 72)
            image = bitmap.to_pil().convert("RGB")
            image.info["inches"] = width_inches
            images[index + 1] = image
        return images
    image = Image.open(path).convert("RGB")
    image.info["inches"] = 8.5
    return {1: image}


def _blank(boxes):
    points = [point for _, polygon in boxes for point in polygon]
    if not points:
        return Image.new("RGB", (800, 1000), "white")
    scale = 100 if max(max(point) for point in points) <= 20 else 1
    width = int(max(point[0] for point in points) * scale) + 40
    height = int(max(point[1] for point in points) * scale) + 40
    image = Image.new("RGB", (max(width, 200), max(height, 200)), "white")
    image.info["inches"] = None
    return image


def _scale(boxes, image):
    points = [point for _, polygon in boxes for point in polygon]
    if not points:
        return 1
    largest = max(max(point) for point in points)
    if largest > 20:
        return 1
    inches = image.info.get("inches")
    if inches:
        return image.width / inches
    return 100


def main():
    parser = argparse.ArgumentParser(description="Draw the places stored in a document reading.")
    parser.add_argument("reading", type=Path)
    parser.add_argument("--document", type=Path, help="The PDF or image of the document. Places are drawn on its pages.")
    parser.add_argument("--image", action="append", default=[], metavar="PAGE=FILE")
    parser.add_argument("--out", type=Path, default=Path("places"))
    args = parser.parse_args()
    reading = json.loads(args.reading.read_text(encoding="utf-8"))
    images = load_document(args.document) if args.document else {}
    for item in args.image:
        page, _, path = item.partition("=")
        images[int(page)] = Image.open(path).convert("RGB")
        images[int(page)].info["inches"] = 8.5
    for path in draw(reading, images, args.out):
        print(path)


if __name__ == "__main__":
    main()
