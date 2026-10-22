#!/usr/bin/env python3
"""Independent interoperability checks using psd-tools and Pillow, not the C# writer's decoder.

python -m pip install -r requirements.txt
python verify_psd.py path/to/generated/fixtures
"""
import base64
import pathlib
import struct
import sys

from PIL import Image
from psd_tools import PSDImage


def assert_equal(actual, expected, label):
    if actual != expected:
        raise AssertionError(label)


def read_manifest(path):
    rows = [line.split("\t") for line in path.read_text(encoding="utf-8").splitlines()]
    width, height, count = map(int, rows[0])
    layers = []
    for row in rows[1:]:
        layers.append(dict(index=int(row[0]), kind=row[1],
                           name=base64.b64decode(row[2]).decode("utf-8"),
                           left=int(row[3]), top=int(row[4]), width=int(row[5]), height=int(row[6]),
                           opacity=int(row[7]), visible=row[8] == "1"))
    assert_equal(len(layers), count, "Manifest layer count")
    return width, height, layers


def top_down_layers(container):
    # psd-tools exposes bottom-to-top stack order; compare to our top-to-bottom public model.
    for layer in reversed(list(container)):
        yield layer
        if layer.is_group():
            for descendant in top_down_layers(layer):
                yield descendant


def verify(path):
    prefix = path.with_suffix("")
    width, height, records = read_manifest(prefix.with_suffix(".manifest.tsv"))
    psd = PSDImage.open(path)
    assert_equal(psd.size, (width, height), f"{path.name}: dimensions")
    assert_equal(psd.depth, 8, f"{path.name}: depth")
    assert_equal(psd.channels, 4, f"{path.name}: channels")
    raw = path.read_bytes()
    assert_equal(raw[:4], b"8BPS", "Signature")
    assert_equal(struct.unpack(">H", raw[4:6])[0], 1, "PSD v1")
    # Photoshop's stored merged RGB is white-matted. Layer RGB is straight.
    # Compare stored planes exactly, then compare the recovered preview in premultiplied
    # space: unmatting an 8-bit preview necessarily amplifies rounding at low alpha.
    merged = psd.topil(apply_icc=False).convert("RGBA")
    expected_merged = prefix.with_suffix(".merged.rgba").read_bytes()
    planes = psd._record.image_data.get_data(psd._record.header)
    assert_equal(len(planes), 4, "Merged plane count")
    for pixel in range(width * height):
        alpha = expected_merged[pixel * 4 + 3]
        assert_equal(planes[3][pixel], alpha, f"{path.name}: merged alpha")
        for channel in range(3):
            expected = (expected_merged[pixel * 4 + channel] * alpha + 255 * (255 - alpha) + 127) // 255
            assert_equal(planes[channel][pixel], expected, f"{path.name}: merged white-matte RGB")
    recovered = merged.tobytes()
    assert_equal(recovered[3::4], expected_merged[3::4], "Recovered merged alpha")
    for pixel in range(width * height):
        alpha = expected_merged[pixel * 4 + 3]
        for channel in range(3):
            delta = abs(recovered[pixel * 4 + channel] - expected_merged[pixel * 4 + channel]) * alpha / 255.0
            if delta > 1.5:
                raise AssertionError(f"{path.name}: recovered preview error {delta}")
    merged.save(prefix.with_suffix(".preview.png"))
    expected = [record for record in records if record["kind"] != "GroupEnd"]
    actual = list(top_down_layers(psd))
    assert_equal(len(actual), len(expected), f"{path.name}: parsed node count")
    for layer, record in zip(actual, expected):
        label = f"{path.name}: {record['name']}"
        assert_equal(layer.name, record["name"], label + " Unicode name/order")
        assert_equal(layer.visible, record["visible"], label + " visibility")
        assert_equal(layer.opacity, record["opacity"], label + " opacity")
        assert_equal(layer.is_group(), record["kind"] == "GroupStart", label + " group kind")
        if not layer.is_group():
            bounds = (record["left"], record["top"], record["left"] + record["width"],
                      record["top"] + record["height"])
            assert_equal(layer.bbox, bounds, label + " rectangle")
            pixels = layer.topil(apply_icc=False).convert("RGBA").tobytes()
            expected_pixels = pathlib.Path(str(prefix) + f".layer{record['index']}.rgba").read_bytes()
            assert_equal(pixels, expected_pixels, label + " decoded channel data")
    # force=True bypasses the stored merged preview; exercise grouping, visibility,
    # layer opacity, clipping and source-over ordering using an independent compositor.
    rendered = psd.composite(force=True, apply_icc=False).convert("RGBA")
    assert_equal(rendered.size, (width, height), "Independent compositor dimensions")
    composited = rendered.tobytes()
    for pixel in range(width * height):
        alpha_actual, alpha_expected = composited[pixel * 4 + 3], expected_merged[pixel * 4 + 3]
        if abs(alpha_actual - alpha_expected) > 1:
            raise AssertionError(f"{path.name}: independent layer composite alpha")
        for channel in range(3):
            actual = composited[pixel * 4 + channel] * alpha_actual
            expected = expected_merged[pixel * 4 + channel] * alpha_expected
            if abs(actual - expected) / 255.0 > 2.0:
                raise AssertionError(f"{path.name}: independent layer composite RGB")
    print(f"PASS {path.name}: {len(records)} records; layer RGBA/matte bytes exact; independent composite within tolerance")


def main():
    directory = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "fixtures")
    files = sorted(directory.glob("*.psd"))
    if not files:
        raise RuntimeError("No generated PSD fixtures found")
    for path in files:
        verify(path)
    print(f"PASS independent PSD interoperability: {len(files)} files")


if __name__ == "__main__":
    main()
