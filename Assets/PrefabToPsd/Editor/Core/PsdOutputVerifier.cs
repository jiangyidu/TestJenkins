using System;
using System.Collections.Generic;
using System.IO;

namespace PrefabToPsd.Core
{
    /// <summary>
    /// Reads the emitted PSD before atomic commit and checks the complete RGB8 subset
    /// promised by this writer. This is a file integrity check, not a Photoshop renderer.
    /// </summary>
    public static class PsdOutputVerifier
    {
        public static void Verify(string path, PsdDocument expected)
        {
            if (expected == null) throw new ArgumentNullException("expected");
            PsdWriter.Validate(expected);
            using (PsdReferenceDocument actual = PsdReferenceReader.Open(path))
            {
                Require(actual.Width == expected.Width && actual.Height == expected.Height,
                    "Written PSD canvas size differs from the export document.");
                Require(actual.HeaderChannels == 4 && actual.HasMergedTransparency,
                    "Written PSD does not declare four-channel merged transparency.");
                Require(actual.Layers.Count == expected.Layers.Count,
                    "Written PSD layer count differs from the export document.");
                int[] ids = AssignedIds(expected.Layers);
                for (int i = 0; i < expected.Layers.Count; i++)
                {
                    PsdLayer wanted = expected.Layers[i];
                    PsdReferenceLayer got = actual.Layers[i];
                    Require(wanted.Kind == got.Kind && wanted.Name == got.Name && ids[i] == got.Id,
                        "Written PSD layer identity/order differs at record " + i + ".");
                    Require(wanted.Visible == got.Visible && wanted.Opacity == got.Opacity,
                        "Written PSD layer visibility/opacity differs at record " + i + ".");
                    if (wanted.Kind != PsdLayerKind.Pixel) continue;
                    Require(wanted.Left == got.Left && wanted.Top == got.Top &&
                        wanted.Width == got.Width && wanted.Height == got.Height,
                        "Written PSD layer bounds differ at record " + i + ".");
                    Equal(wanted.Rgba, actual.ReadRgba(got), "Written PSD layer pixels differ at record " + i + ".");
                }
                Dictionary<ushort, PsdResource> expectedResources = new Dictionary<ushort, PsdResource>();
                foreach (PsdResource resource in expected.SafeResources) expectedResources.Add(resource.Id, resource);
                foreach (PsdResource resource in actual.Resources)
                {
                    PsdSafeResources.Validate(resource);
                    PsdResource wanted;
                    if (expectedResources.TryGetValue(resource.Id, out wanted))
                    {
                        Equal(wanted.Data, resource.Data, "Written PSD image resource differs: " + resource.Id + ".");
                        expectedResources.Remove(resource.Id);
                    }
                    else Require(resource.Id == 1005, "Written PSD contains an unexpected inherited image resource.");
                }
                Require(expectedResources.Count == 0, "Written PSD lost inherited image resources.");

                byte[] stored = actual.ReadCompositeStoredRgba();
                Require(stored.Length == expected.CompositeRgba.Length, "Written merged-image size differs.");
                for (int i = 0; i < stored.Length; i += 4)
                {
                    int alpha = expected.CompositeRgba[i + 3];
                    Require(stored[i + 3] == alpha, "Written merged alpha differs.");
                    for (int channel = 0; channel < 3; channel++)
                    {
                        int whiteMatte = (expected.CompositeRgba[i + channel] * alpha + 255 * (255 - alpha) + 127) / 255;
                        Require(stored[i + channel] == whiteMatte, "Written merged RGB white matte differs.");
                    }
                }
            }
        }

        private static int[] AssignedIds(IList<PsdLayer> layers)
        {
            HashSet<int> used = new HashSet<int>();
            foreach (PsdLayer layer in layers) if (layer.Id > 0) used.Add(layer.Id);
            int[] ids = new int[layers.Count];
            int next = 1;
            for (int i = layers.Count - 1; i >= 0; i--)
            {
                int id = layers[i].Id;
                if (id == 0)
                {
                    while (used.Contains(next)) next++;
                    id = next++;
                    used.Add(id);
                }
                ids[i] = id;
            }
            return ids;
        }

        private static void Equal(byte[] wanted, byte[] actual, string message)
        {
            Require(wanted != null && actual != null && wanted.Length == actual.Length, message);
            for (int i = 0; i < wanted.Length; i++) if (wanted[i] != actual[i]) throw new InvalidDataException(message);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
    }
}
