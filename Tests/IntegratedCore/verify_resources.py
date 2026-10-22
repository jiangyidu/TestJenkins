#!/usr/bin/env python3
"""Independent PSD resource and pixel checks using psd-tools + Pillow/LittleCMS.

python verify_resources.py Tests/Results/integrated-core/resources
"""
from io import BytesIO
from pathlib import Path
import sys

from PIL import ImageCms
from psd_tools import PSDImage
from psd_tools.constants import Resource


def main():
    root = Path(sys.argv[1])
    expected_icc = (root / 'expected-srgb.icc').read_bytes()
    expected_pixels = bytes([200, 80, 30, 128, 0, 220, 20, 255,
                             12, 34, 56, 0, 255, 255, 255, 64])
    for name in ['expected-srgb.icc', 'sampled-srgb.icc']:
        profile = ImageCms.ImageCmsProfile(BytesIO((root / name).read_bytes()))
        description = ImageCms.getProfileDescription(profile).strip()
        assert 'sRGB' in description, description
        print(f'PASS {name}: independently parsed by Pillow/LittleCMS ({description})')

    for name in ['resources-raw.psd', 'resources-rle.psd']:
        psd = PSDImage.open(root / name)
        assert psd.size == (2, 2) and psd.depth == 8 and psd.channels == 4
        assert len(psd) == 1 and psd[0].name == '资源与像素 🧩'
        ids = {int(key) for key in psd.image_resources.keys()}
        assert ids == {1005, 1039, 1064, 1057}, ids
        assert psd.image_resources.get_data(Resource.ICC_PROFILE) == expected_icc
        resolution = psd.image_resources.get_data(Resource.RESOLUTION_INFO)
        assert resolution.horizontal == 300 * 65536
        assert resolution.vertical == 144 * 65536
        assert resolution.horizontal_unit == resolution.vertical_unit == 1
        assert resolution.width_unit == resolution.height_unit == 1
        assert psd.image_resources.get_data(Resource.PIXEL_ASPECT_RATIO) == 1.25
        version = psd.image_resources.get_data(Resource.VERSION_INFO)
        assert version.has_composite and version.writer == 'PrefabToPsd'
        assert version.reader == 'PrefabToPsd' and version.file_version == 1

        # Decode through a different PSD implementation, not our reference reader.
        assert psd[0].topil(apply_icc=False).convert('RGBA').tobytes() == expected_pixels
        icc_layer = psd[0].topil(apply_icc=True).convert('RGBA').tobytes()
        for p in range(4):
            alpha = expected_pixels[p * 4 + 3]
            assert icc_layer[p * 4 + 3] == alpha
            if alpha:
                for c in range(3):
                    assert abs(icc_layer[p * 4 + c] - expected_pixels[p * 4 + c]) <= 1
        # Check stored white-matted merged channels exactly, including transparent RGB.
        planes = psd._record.image_data.get_data(psd._record.header)
        assert len(planes) == 4
        for p in range(4):
            a = expected_pixels[p * 4 + 3]
            assert planes[3][p] == a
            for c in range(3):
                matte = (expected_pixels[p * 4 + c] * a + 255 * (255 - a) + 127) // 255
                assert planes[c][p] == matte
        composite = psd.composite(force=True, apply_icc=False).convert('RGBA').tobytes()
        for p in range(4):
            actual_alpha, expected_alpha = composite[p * 4 + 3], expected_pixels[p * 4 + 3]
            assert abs(actual_alpha - expected_alpha) <= 1
            for c in range(3):
                difference = abs(composite[p * 4 + c] * actual_alpha
                                 - expected_pixels[p * 4 + c] * expected_alpha) / 255
                assert difference <= 2
        print(f'PASS {name}: ICC bytes exact; resolution/aspect/VersionInfo correct; '
              'layer and merged bytes exact; independent composite correct')
    print('PASS independent resources: 2 ICC profiles and 2 PSD files. No Unity GPU calls.')


if __name__ == '__main__':
    main()
