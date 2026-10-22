#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PrefabToPsd.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace PrefabToPsd
{
    /// <summary>
    /// Editor integration tests. These use an independent PSD layer/composite reader,
    /// not the exporter's layer compositor. A graphics device is required.
    /// </summary>
    public static class PrefabPsdSelfTests
    {
        private static readonly List<string> Messages = new List<string>();
        private static int assertions;

        [MenuItem("Tools/Prefab to PSD/Run Self Tests")]
        public static void RunFromMenu()
        {
            RunAll();
        }

        // Unity -batchmode -projectPath ... -executeMethod PrefabToPsd.PrefabPsdSelfTests.RunBatch
        // Do not pass -nographics: the capture tests require an actual graphics device.
        public static void RunBatch()
        {
            try { RunAll(); EditorApplication.Exit(0); }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        public static void RunAll()
        {
            Messages.Clear();
            assertions = 0;
            string output = GetOutputDirectory();
            Directory.CreateDirectory(output);
            string folder = "Assets/PrefabToPsdTest_" + Guid.NewGuid().ToString("N");
            string folderName = folder.Substring("Assets/".Length);
            AssetDatabase.CreateFolder("Assets", folderName);
            string initialScenes = SceneSnapshot();
            int initialPreviewCount = PreviewSceneCount();
            try
            {
                Log("Unity " + Application.unityVersion + ", " + SystemInfo.graphicsDeviceType +
                    ", " + QualitySettings.activeColorSpace + ", " + DateTime.UtcNow.ToString("u"));
                TestCompositeAndPersistence(folder, output);
                TestMasks(folder, output);
                TestVisibleMaskIsolation(folder, output);
                TestGroupAlpha(folder, output);
                TestSlicedCanvasScale(folder, output);
                TestCancellation(folder, output);
                assertions += PrefabPsdWorkflowSelfTests.Run(folder, output, Log);
                Assert(SceneSnapshot() == initialScenes, "Main scenes and dirty flags are unchanged.");
                if (initialPreviewCount >= 0)
                    Assert(PreviewSceneCount() == initialPreviewCount, "No preview scenes leaked.");
                Log("PASS: " + assertions + " assertions.");
            }
            catch (Exception exception)
            {
                Log("FAIL: " + exception);
                throw;
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
                File.WriteAllLines(Path.Combine(output, "self-test-report.txt"), Messages.ToArray(), Encoding.UTF8);
                EditorUtility.ClearProgressBar();
            }
            Debug.Log("Prefab to PSD self-tests passed. Report and PSD fixtures: " + output);
        }

        private static void TestCompositeAndPersistence(string folder, string output)
        {
            WithPrefab(folder + "/Composite.prefab", delegate(GameObject root)
            {
                RectTransform group = PrefabPsdDemo.Rect(root.transform, "分组 🧩", 0, 0, 96, 64);
                PrefabPsdDemo.Image(group, "红色半透明", 8, 8, 24, 16, new Color(1, 0, 0, 0.5f));
                PrefabPsdDemo.Image(group, "Blue front", 20, 12, 20, 20, new Color(0, 0, 1, 0.5f));
                PrefabPsdDemo.Image(root.transform, "Inactive", 64, 0, 20, 20, Color.magenta)
                    .gameObject.SetActive(false);
                PrefabPsdDemo.Image(root.transform, "Disabled Graphic", 64, 24, 20, 20, Color.magenta)
                    .enabled = false;
            }, delegate(GameObject prefab)
            {
                string assetPath = AssetDatabase.GetAssetPath(prefab);
                string before = HashFile(assetPath);
                string hierarchyBefore = HierarchySnapshot(prefab);
                ExportReport rle = PrefabPsdExporter.Export(prefab, Path.Combine(output, "composite-rle.psd"),
                    Options(PsdCompression.Rle));
                PsdProbe image = PsdProbe.Read(rle.PsdPath);
                Assert(image.Width == 96 && image.Height == 64, "PSD dimensions match requested dimensions.");
                Assert(image.Channels == 4, "Merged image has RGB and transparency.");
                Assert(rle.LayerCount == 2, "Only the two visible enabled Graphics become pixel layers.");
                Assert(image.LayerNames.Contains("分组 🧩"), "Unicode folder name survives PSD encoding.");
                Assert(image.LayerNames.Contains("红色半透明"), "Unicode layer name survives PSD encoding.");
                Pixel(image, 2, 2, 0, 0, 0, 0, 3, "Transparent outside");
                Pixel(image, 10, 10, 255, 0, 0, 128, 4, "Red only");
                Pixel(image, 38, 25, 0, 0, 255, 128, 4, "Blue only");
                int red = QualitySettings.activeColorSpace == ColorSpace.Linear
                    ? Mathf.RoundToInt(Mathf.LinearToGammaSpace(1f / 3f) * 255) : 85;
                int blue = QualitySettings.activeColorSpace == ColorSpace.Linear
                    ? Mathf.RoundToInt(Mathf.LinearToGammaSpace(2f / 3f) * 255) : 170;
                Pixel(image, 24, 16, red, 0, blue, 191, 6, "Blue-over-red overlap");
                Pixel(image, 70, 8, 0, 0, 0, 0, 3, "Inactive object excluded");
                Pixel(image, 70, 28, 0, 0, 0, 0, 3, "Disabled Graphic excluded");
                ProbeLayer redLayer = image.Layer("红色半透明");
                ProbeLayer blueLayer = image.Layer("Blue front");
                Bounds(redLayer, 8, 8, 24, 16, "Red layer alpha bounds");
                Bounds(blueLayer, 20, 12, 20, 20, "Blue layer alpha bounds");
                LayerPixel(redLayer, 24, 16, 255, 0, 0, 128, 4, "Red layer keeps original pixels beneath blue");
                LayerPixel(blueLayer, 24, 16, 0, 0, 255, 128, 4, "Blue layer excludes lower red layer");
                LayerPixel(redLayer, 38, 25, 0, 0, 0, 0, 3, "Red layer excludes other graphic bounds");
                Assert(redLayer.Opacity == 255 && blueLayer.Opacity == 255,
                    "Graphic alpha is baked into channels without duplicate PSD opacity.");
                Assert(image.Layers.IndexOf(redLayer) < image.Layers.IndexOf(blueLayer),
                    "PSD physical records preserve bottom-to-top Unity sibling order.");
                Assert(File.Exists(rle.MetadataPath), "Metadata sidecar exists.");
                Assert(File.ReadAllText(rle.MetadataPath).Contains("红色半透明"), "Metadata keeps source names.");

                ExportReport raw = PrefabPsdExporter.Export(prefab, Path.Combine(output, "composite-raw.psd"),
                    Options(PsdCompression.Raw));
                PsdProbe rawImage = PsdProbe.Read(raw.PsdPath);
                Assert(Equal(image.Pixels, rawImage.Pixels), "Raw and RLE merged pixels are identical.");
                Assert(Equal(redLayer.Pixels, rawImage.Layer("红色半透明").Pixels), "Raw and RLE red layer pixels are identical.");
                Assert(Equal(blueLayer.Pixels, rawImage.Layer("Blue front").Pixels), "Raw and RLE blue layer pixels are identical.");
                Assert(HashFile(assetPath) == before, "Prefab asset bytes remain unchanged after exports.");
                Assert(HierarchySnapshot(prefab) == hierarchyBefore, "Prefab components remain unchanged.");
            });
        }

        private static void TestMasks(string folder, string output)
        {
            WithPrefab(folder + "/Masks.prefab", delegate(GameObject root)
            {
                Image image = PrefabPsdDemo.Image(root.transform, "Stencil Mask", 8, 8, 24, 24, Color.white);
                image.gameObject.AddComponent<Mask>().showMaskGraphic = false;
                PrefabPsdDemo.Image(image.transform, "Masked Green", -8, -8, 48, 48, Color.green);
                RectTransform rectMask = PrefabPsdDemo.Rect(root.transform, "RectMask2D", 48, 8, 24, 24);
                rectMask.gameObject.AddComponent<RectMask2D>();
                PrefabPsdDemo.Image(rectMask, "Clipped Blue", -8, -8, 48, 48, Color.blue);
            }, delegate(GameObject prefab)
            {
                ExportReport report = PrefabPsdExporter.Export(prefab, Path.Combine(output, "masks.psd"),
                    Options(PsdCompression.Rle));
                PsdProbe image = PsdProbe.Read(report.PsdPath);
                Pixel(image, 12, 12, 0, 255, 0, 255, 3, "Inside stencil mask");
                Pixel(image, 4, 12, 0, 0, 0, 0, 3, "Outside stencil mask");
                Pixel(image, 36, 12, 0, 0, 0, 0, 3, "Beyond stencil mask");
                Pixel(image, 52, 12, 0, 0, 255, 255, 3, "Inside RectMask2D");
                Pixel(image, 44, 12, 0, 0, 0, 0, 3, "Outside RectMask2D");
                Pixel(image, 76, 12, 0, 0, 0, 0, 3, "Beyond RectMask2D");
                Assert(report.LayerCount == 2, "Hidden stencil Graphic is not an output pixel layer.");
                ProbeLayer green = image.Layer("Masked Green");
                ProbeLayer blue = image.Layer("Clipped Blue");
                Bounds(green, 8, 8, 24, 24, "Stencil is baked into child layer bounds");
                Bounds(blue, 48, 8, 24, 24, "RectMask2D is baked into child layer bounds");
                LayerPixel(green, 12, 12, 0, 255, 0, 255, 3, "Isolated stencil child interior");
                LayerPixel(green, 4, 12, 0, 0, 0, 0, 3, "Isolated stencil child exterior");
                LayerPixel(green, 52, 12, 0, 0, 0, 0, 3, "Isolated stencil child excludes other mask subtree");
                LayerPixel(blue, 52, 12, 0, 0, 255, 255, 3, "Isolated RectMask2D child interior");
                LayerPixel(blue, 44, 12, 0, 0, 0, 0, 3, "Isolated RectMask2D child exterior");
            });
        }

        private static void TestVisibleMaskIsolation(string folder, string output)
        {
            WithPrefab(folder + "/VisibleMask.prefab", delegate(GameObject root)
            {
                Image mask = PrefabPsdDemo.Image(root.transform, "Visible Mask", 8, 8, 24, 24,
                    new Color(1, 0, 0, 0.5f));
                mask.gameObject.AddComponent<Mask>().showMaskGraphic = true;
                PrefabPsdDemo.Image(mask.transform, "Masked Blue", -8, -8, 48, 48,
                    new Color(0, 0, 1, 0.5f));
            }, delegate(GameObject prefab)
            {
                ExportReport report = PrefabPsdExporter.Export(prefab, Path.Combine(output, "visible-mask.psd"),
                    Options(PsdCompression.Rle));
                PsdProbe image = PsdProbe.Read(report.PsdPath);
                Assert(report.LayerCount == 2, "Visible mask and its child each have one pixel layer.");
                ProbeLayer mask = image.Layer("Visible Mask [Graphic]");
                ProbeLayer child = image.Layer("Masked Blue");
                Bounds(mask, 8, 8, 24, 24, "Visible mask bounds");
                Bounds(child, 8, 8, 24, 24, "Visible mask clips child layer bounds");
                LayerPixel(mask, 16, 16, 255, 0, 0, 128, 4, "Visible mask layer excludes child");
                LayerPixel(child, 16, 16, 0, 0, 255, 128, 4, "Child layer does not bake ancestor mask color twice");
                LayerPixel(child, 4, 16, 0, 0, 0, 0, 3, "Child remains clipped while mask color is suppressed");
            });
        }

        private static void TestGroupAlpha(string folder, string output)
        {
            WithPrefab(folder + "/GroupAlpha.prefab", delegate(GameObject root)
            {
                RectTransform group = PrefabPsdDemo.Rect(root.transform, "Alpha group", 0, 0, 96, 64);
                group.gameObject.AddComponent<CanvasGroup>().alpha = 0.5f;
                PrefabPsdDemo.Image(group, "Quarter alpha", 8, 8, 24, 24, new Color(0, 1, 0, 0.5f));
            }, delegate(GameObject prefab)
            {
                ExportReport report = PrefabPsdExporter.Export(prefab, Path.Combine(output, "group-alpha.psd"),
                    Options(PsdCompression.Rle));
                PsdProbe image = PsdProbe.Read(report.PsdPath);
                Pixel(image, 16, 16, 0, 255, 0, 64, 4, "CanvasGroup alpha is applied once");
                ProbeLayer layer = image.Layer("Quarter alpha");
                LayerPixel(layer, 16, 16, 0, 255, 0, 64, 4, "CanvasGroup alpha is applied once in isolated layer");
                Assert(layer.Opacity == 255, "CanvasGroup alpha is not duplicated as PSD layer opacity.");
            });
        }

        private static void TestSlicedCanvasScale(string folder, string output)
        {
            Sprite sprite = PrefabPsdDemo.CreateSlicedSpriteAsset(folder + "/SlicedTexture.asset");
            WithPrefab(folder + "/Sliced.prefab", delegate(GameObject root)
            {
                Canvas canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                CanvasScaler scaler = root.AddComponent<CanvasScaler>();
                scaler.referencePixelsPerUnit = 200f;
                canvas.referencePixelsPerUnit = 200f;
                Image panel = PrefabPsdDemo.Image(root.transform, "Sliced at 200 PPU", 8, 8, 64, 32, Color.white);
                panel.sprite = sprite;
                panel.type = Image.Type.Sliced;
            }, delegate(GameObject prefab)
            {
                string before = HierarchySnapshot(prefab);
                ExportReport report = PrefabPsdExporter.Export(prefab, Path.Combine(output, "sliced-ppu.psd"),
                    Options(PsdCompression.Rle));
                PsdProbe image = PsdProbe.Read(report.PsdPath);
                ProbeLayer panel = image.Layer("Sliced at 200 PPU");
                Bounds(panel, 8, 8, 64, 32, "Nine-slice output bounds");
                // Sprite border=4, sprite PPU=100, Canvas reference PPU=200:
                // the border must be 8 design pixels, not the 4-pixel default.
                LayerPixel(panel, 14, 24, 55, 202, 192, 255, 3, "Non-default Canvas PPU preserves 8-pixel left border");
                LayerPixel(panel, 18, 24, 24, 58, 77, 255, 3, "Nine-slice center follows the enlarged border");
                LayerPixel(panel, 40, 14, 55, 202, 192, 255, 3, "Non-default Canvas PPU preserves 8-pixel top border");
                Pixel(image, 14, 24, 55, 202, 192, 255, 3, "Merged nine-slice border");
                Assert(HierarchySnapshot(prefab) == before, "Canvas and CanvasScaler settings are unchanged.");
            });
        }

        private static void TestCancellation(string folder, string output)
        {
            WithPrefab(folder + "/Cancel.prefab", delegate(GameObject root)
            {
                PrefabPsdDemo.Image(root.transform, "Image", 8, 8, 24, 24, Color.white);
                PrefabPsdDemo.Image(root.transform, "Second Image", 48, 8, 24, 24, Color.blue);
            }, delegate(GameObject prefab)
            {
                int beforeScenes = PreviewSceneCount();
                string before = HierarchySnapshot(prefab);
                string path = Path.Combine(output, "cancel-" + Guid.NewGuid().ToString("N") + ".psd");
                ExportOptions options = Options(PsdCompression.Rle);
                int progressCalls = 0;
                options.Progress = delegate(float progress, string message)
                {
                    ++progressCalls;
                    if (progressCalls == 4) throw new OperationCanceledException("Self-test cancellation after first layer capture.");
                };
                bool cancelled = false;
                try { PrefabPsdExporter.Export(prefab, path, options); }
                catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && progressCalls >= 4, "Progress cancellation interrupts after a layer was captured.");
                Assert(!File.Exists(path), "Cancellation leaves no final PSD.");
                Assert(!File.Exists(Path.ChangeExtension(path, ".prefab.json")), "Cancellation leaves no final sidecar.");
                byte[] previous = Encoding.ASCII.GetBytes("Existing output must survive cancellation.");
                File.WriteAllBytes(path, previous);
                progressCalls = 0;
                cancelled = false;
                try { PrefabPsdExporter.Export(prefab, path, options); }
                catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && Equal(File.ReadAllBytes(path), previous), "Cancellation preserves an existing output file.");
                File.Delete(path);
                Assert(HierarchySnapshot(prefab) == before, "Cancellation leaves the source unchanged.");
                if (beforeScenes >= 0)
                    Assert(PreviewSceneCount() == beforeScenes, "Cancellation releases preview scenes.");
            });
        }

        private static ExportOptions Options(PsdCompression compression)
        {
            return new ExportOptions
            {
                Width = 96, Height = 64, Compression = compression,
                PreserveHierarchy = true, WriteMetadata = true
            };
        }

        private static void WithPrefab(string path, Action<GameObject> build, Action<GameObject> test)
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            GameObject root = null;
            try
            {
                root = PrefabPsdDemo.Root("SelfTest", 96, 64);
                SceneManager.MoveGameObjectToScene(root, scene);
                build(root);
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
                Assert(prefab != null, "Fixture prefab was created.");
                test(prefab);
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static string GetOutputDirectory()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; ++i)
                if (args[i] == "-prefabPsdTestOutput") return Path.GetFullPath(args[i + 1]);
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/PrefabToPsdSelfTests"));
        }

        private static string HierarchySnapshot(GameObject root)
        {
            StringBuilder result = new StringBuilder();
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                result.Append(transform.name).Append('|').Append(transform.gameObject.activeSelf)
                    .Append('|').Append(transform.GetSiblingIndex()).Append('\n');
                foreach (Component component in transform.GetComponents<Component>())
                    result.Append(component == null ? "<missing>" : EditorJsonUtility.ToJson(component))
                        .Append('\n');
            }
            return result.ToString();
        }

        private static string SceneSnapshot()
        {
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < SceneManager.sceneCount; ++i)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                result.Append(scene.handle).Append('|').Append(scene.path).Append('|')
                    .Append(scene.isLoaded).Append('|').Append(scene.isDirty).Append('\n');
            }
            return result.ToString();
        }

        private static int PreviewSceneCount()
        {
            PropertyInfo property = typeof(EditorSceneManager).GetProperty("previewSceneCount",
                BindingFlags.Static | BindingFlags.Public);
            return property == null ? -1 : (int)property.GetValue(null, null);
        }

        private static string HashFile(string path)
        {
            using (SHA256 hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(path)));
        }

        private static void Pixel(PsdProbe image, int x, int y,
            int red, int green, int blue, int alpha, int tolerance, string message)
        {
            int offset = (y * image.Width + x) * 4;
            int[] expected = { red, green, blue, alpha };
            for (int channel = 0; channel < 4; ++channel)
            {
                // Transparent RGB is undefined.
                if (alpha == 0 && channel < 3) continue;
                Assert(Math.Abs(image.Pixels[offset + channel] - expected[channel]) <= tolerance,
                    message + " channel " + channel + ": expected " + expected[channel] +
                    " ± " + tolerance + ", got " + image.Pixels[offset + channel] + ".");
            }
        }

        private static void Bounds(ProbeLayer layer, int left, int top, int width, int height, string message)
        {
            Assert(layer.Left == left && layer.Top == top && layer.Width == width && layer.Height == height,
                message + ": expected " + left + "," + top + " " + width + "x" + height +
                ", got " + layer.Left + "," + layer.Top + " " + layer.Width + "x" + layer.Height + ".");
        }

        // Coordinates are document coordinates; out-of-bounds layer pixels are transparent.
        private static void LayerPixel(ProbeLayer layer, int x, int y,
            int red, int green, int blue, int alpha, int tolerance, string message)
        {
            int[] expected = { red, green, blue, alpha };
            bool inside = x >= layer.Left && x < layer.Left + layer.Width &&
                y >= layer.Top && y < layer.Top + layer.Height;
            int offset = inside ? ((y - layer.Top) * layer.Width + x - layer.Left) * 4 : 0;
            for (int channel = 0; channel < 4; ++channel)
            {
                if (alpha == 0 && channel < 3) continue;
                int actual = inside ? layer.Pixels[offset + channel] : 0;
                Assert(Math.Abs(actual - expected[channel]) <= tolerance,
                    message + " channel " + channel + ": expected " + expected[channel] +
                    " ± " + tolerance + ", got " + actual + ".");
            }
        }

        private sealed class ProbeLayer
        {
            internal string Name;
            internal int Left, Top, Width, Height, Kind;
            internal byte Opacity;
            internal byte[] Pixels;
            internal int[] ChannelIds;
            internal uint[] ChannelLengths;
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; ++i) if (a[i] != b[i]) return false;
            return true;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            ++assertions;
            Log("PASS " + message);
        }

        private static void Log(string message)
        {
            Messages.Add(message);
            Debug.Log("[PrefabToPsd test] " + message);
        }

        // Independent read path: only the RGB8 PSD subset emitted by the exporter.
        private sealed class PsdProbe
        {
            internal int Width, Height, Channels;
            internal byte[] Pixels;
            internal readonly List<string> LayerNames = new List<string>();
            // On-disk PSD order is bottom-to-top, including folder divider records.
            internal readonly List<ProbeLayer> Layers = new List<ProbeLayer>();

            internal ProbeLayer Layer(string name)
            {
                foreach (ProbeLayer layer in Layers)
                    if (layer.Kind == 0 && layer.Name == name && layer.Width > 0 && layer.Height > 0)
                        return layer;
                throw new InvalidDataException("Missing expected pixel layer: " + name);
            }

            internal static PsdProbe Read(string path)
            {
                using (FileStream stream = File.OpenRead(path))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "8BPS" || U16(reader) != 1)
                        throw new InvalidDataException("Expected PSD version 1.");
                    reader.ReadBytes(6);
                    PsdProbe image = new PsdProbe();
                    image.Channels = U16(reader);
                    image.Height = checked((int)U32(reader));
                    image.Width = checked((int)U32(reader));
                    if (U16(reader) != 8 || U16(reader) != 3)
                        throw new InvalidDataException("Expected RGB 8-bit PSD.");
                    SkipSection(reader); // color mode data
                    SkipSection(reader); // image resources
                    uint layerMaskLength = U32(reader);
                    long layerMaskEnd = stream.Position + layerMaskLength;
                    if (layerMaskLength > 0)
                    {
                        uint infoLength = U32(reader);
                        long infoEnd = stream.Position + infoLength;
                        if (infoLength > 0)
                        {
                            int count = Math.Abs((int)unchecked((short)U16(reader)));
                            for (int i = 0; i < count; ++i) image.ReadLayerRecord(reader);
                            foreach (ProbeLayer layer in image.Layers) ReadLayerPixels(reader, layer, infoEnd);
                        }
                        stream.Position = infoEnd;
                    }
                    stream.Position = layerMaskEnd;
                    int compression = U16(reader);
                    int[] lengths = null;
                    if (compression == 1)
                    {
                        lengths = new int[image.Channels * image.Height];
                        for (int i = 0; i < lengths.Length; ++i) lengths[i] = U16(reader);
                    }
                    else if (compression != 0) throw new InvalidDataException("Unsupported compression.");
                    image.Pixels = new byte[checked(image.Width * image.Height * 4)];
                    for (int i = 3; i < image.Pixels.Length; i += 4) image.Pixels[i] = 255;
                    for (int channel = 0; channel < image.Channels; ++channel)
                    {
                        for (int y = 0; y < image.Height; ++y)
                        {
                            byte[] row = compression == 0 ? Exact(reader, image.Width)
                                : DecodePackBits(Exact(reader, lengths[channel * image.Height + y]), image.Width);
                            if (channel < 4)
                                for (int x = 0; x < image.Width; ++x)
                                    image.Pixels[(y * image.Width + x) * 4 + channel] = row[x];
                        }
                    }
                    // PSD merged RGB with transparency is matted against white, unlike
                    // the straight RGB in individual layer channels. Undo that matte.
                    // RGB precision inevitably decreases at very low merged alpha.
                    if (image.Channels >= 4)
                    {
                        for (int i = 0; i < image.Pixels.Length; i += 4)
                        {
                            int alpha = image.Pixels[i + 3];
                            for (int channel = 0; channel < 3; ++channel)
                            {
                                int value = alpha == 0 ? 0 :
                                    (image.Pixels[i + channel] * 255 - 255 * (255 - alpha) + alpha / 2) / alpha;
                                image.Pixels[i + channel] = (byte)Math.Max(0, Math.Min(255, value));
                            }
                        }
                    }
                    return image;
                }
            }

            private void ReadLayerRecord(BinaryReader reader)
            {
                ProbeLayer layer = new ProbeLayer();
                layer.Top = unchecked((int)U32(reader));
                layer.Left = unchecked((int)U32(reader));
                int bottom = unchecked((int)U32(reader));
                int right = unchecked((int)U32(reader));
                layer.Width = checked(right - layer.Left);
                layer.Height = checked(bottom - layer.Top);
                if (layer.Width < 0 || layer.Height < 0) throw new InvalidDataException("Negative layer dimensions.");
                int channels = U16(reader);
                layer.ChannelIds = new int[channels];
                layer.ChannelLengths = new uint[channels];
                for (int i = 0; i < channels; ++i)
                {
                    layer.ChannelIds[i] = unchecked((short)U16(reader));
                    layer.ChannelLengths[i] = U32(reader);
                }
                if (Encoding.ASCII.GetString(Exact(reader, 4)) != "8BIM")
                    throw new InvalidDataException("Bad layer blend signature.");
                Exact(reader, 4); // blend key
                layer.Opacity = reader.ReadByte();
                Exact(reader, 3); // clipping, flags, filler
                uint extraLength = U32(reader);
                long end = reader.BaseStream.Position + extraLength;
                SkipSection(reader);
                SkipSection(reader);
                int nameLength = reader.ReadByte();
                string name = Encoding.ASCII.GetString(Exact(reader, nameLength));
                int padded = (nameLength + 1 + 3) & ~3;
                reader.ReadBytes(padded - nameLength - 1);
                while (reader.BaseStream.Position + 12 <= end)
                {
                    string signature = Encoding.ASCII.GetString(Exact(reader, 4));
                    string key = Encoding.ASCII.GetString(Exact(reader, 4));
                    uint length = U32(reader);
                    long next = reader.BaseStream.Position + length + (length & 1);
                    if (signature == "8BIM" && key == "luni")
                    {
                        int units = checked((int)U32(reader));
                        name = Encoding.BigEndianUnicode.GetString(Exact(reader, checked(units * 2)));
                    }
                    if (signature == "8BIM" && key == "lsct" && length >= 4)
                        layer.Kind = checked((int)U32(reader));
                    if (next > end) throw new InvalidDataException("Layer tag exceeds extra data.");
                    reader.BaseStream.Position = next;
                }
                LayerNames.Add(name);
                layer.Name = name;
                Layers.Add(layer);
                reader.BaseStream.Position = end;
            }

            private static void ReadLayerPixels(BinaryReader reader, ProbeLayer layer, long infoEnd)
            {
                int pixelCount = checked(layer.Width * layer.Height);
                layer.Pixels = new byte[checked(pixelCount * 4)];
                for (int i = 3; i < layer.Pixels.Length; i += 4) layer.Pixels[i] = 255;
                for (int channel = 0; channel < layer.ChannelIds.Length; ++channel)
                {
                    long end = reader.BaseStream.Position + layer.ChannelLengths[channel];
                    if (end > infoEnd || layer.ChannelLengths[channel] < 2)
                        throw new InvalidDataException("Layer channel exceeds layer information section.");
                    int compression = U16(reader);
                    int[] rowLengths = null;
                    if (compression == 1)
                    {
                        rowLengths = new int[layer.Height];
                        for (int row = 0; row < rowLengths.Length; ++row) rowLengths[row] = U16(reader);
                    }
                    else if (compression != 0) throw new InvalidDataException("Unsupported layer compression.");
                    int component = layer.ChannelIds[channel] == -1 ? 3 : layer.ChannelIds[channel];
                    for (int y = 0; y < layer.Height; ++y)
                    {
                        byte[] row = compression == 0 ? Exact(reader, layer.Width)
                            : DecodePackBits(Exact(reader, rowLengths[y]), layer.Width);
                        if (component >= 0 && component < 4)
                            for (int x = 0; x < layer.Width; ++x)
                                layer.Pixels[(y * layer.Width + x) * 4 + component] = row[x];
                    }
                    if (reader.BaseStream.Position != end)
                        throw new InvalidDataException("Layer channel data length does not match channel header.");
                }
            }

            private static byte[] DecodePackBits(byte[] source, int expected)
            {
                byte[] row = new byte[expected];
                int input = 0, output = 0;
                while (input < source.Length)
                {
                    int token = unchecked((sbyte)source[input++]);
                    if (token >= 0)
                    {
                        int count = token + 1;
                        if (input + count > source.Length || output + count > row.Length)
                            throw new InvalidDataException("Bad PackBits literal.");
                        Buffer.BlockCopy(source, input, row, output, count);
                        input += count; output += count;
                    }
                    else if (token != -128)
                    {
                        int count = 1 - token;
                        if (input >= source.Length || output + count > row.Length)
                            throw new InvalidDataException("Bad PackBits repeat.");
                        byte value = source[input++];
                        for (int i = 0; i < count; ++i) row[output++] = value;
                    }
                }
                if (output != expected) throw new InvalidDataException("Truncated PackBits row.");
                return row;
            }

            private static void SkipSection(BinaryReader reader)
            {
                uint length = U32(reader);
                reader.BaseStream.Position += length;
            }
            private static int U16(BinaryReader reader)
            {
                int a = reader.ReadByte(), b = reader.ReadByte();
                return (a << 8) | b;
            }
            private static uint U32(BinaryReader reader)
            {
                return ((uint)U16(reader) << 16) | (uint)U16(reader);
            }
            private static byte[] Exact(BinaryReader reader, int count)
            {
                byte[] bytes = reader.ReadBytes(count);
                if (bytes.Length != count) throw new EndOfStreamException();
                return bytes;
            }
        }
    }
}
#endif
