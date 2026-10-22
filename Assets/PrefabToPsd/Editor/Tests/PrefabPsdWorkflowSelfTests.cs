#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
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
    /// Graphics-enabled workflow regressions. Invoked by Run Self Tests / RunBatch.
    /// Reader and writer tests run separately; these exercise real Unity sources.
    /// </summary>
    public static class PrefabPsdWorkflowSelfTests
    {
        private static int assertions;
        private static Action<string> log;

        public static int Run(string assetFolder, string outputDirectory, Action<string> logger)
        {
            assertions = 0;
            log = logger;
            TestOptionsClone();
            TestPreflightAndStructure(assetFolder, outputDirectory);
            TestReferenceChanges(outputDirectory);
            TestReferenceTextChange(outputDirectory);
            TestBatch(assetFolder, outputDirectory);
            TestVerificationCancellation(outputDirectory);
            Log("PASS workflow: " + assertions + " assertions.");
            return assertions;
        }

        private static void TestOptionsClone()
        {
            ExportOptions original = Options();
            original.Preset = ExportPreset.SourceReference;
            original.ReferencePsdPath = "Assets/Reference.psd";
            original.AutoDetectReferencePsd = false;
            original.CopySafeReferenceResources = false;
            original.PreserveReferenceNames = false;
            original.PreserveEmptyGroups = true;
            original.AddCanvasMarker = true;
            original.MemoryBudgetMB = 256;
            original.Compression = PsdCompression.Raw;
            original.Progress = delegate(float value, string message) { };
            ExportOptions clone = original.Clone();
            Check(!ReferenceEquals(original, clone), "Options.Clone creates a distinct configuration object.");
            Check(clone.Preset == original.Preset && clone.ReferencePsdPath == original.ReferencePsdPath
                && !clone.AutoDetectReferencePsd && !clone.CopySafeReferenceResources && !clone.PreserveReferenceNames,
                "Options.Clone preserves reference and preset configuration.");
            Check(clone.PreserveEmptyGroups && clone.AddCanvasMarker && clone.MemoryBudgetMB == 256
                && clone.Compression == PsdCompression.Raw && clone.Progress == original.Progress,
                "Options.Clone preserves structure, memory, compression and cancellation callback.");
            clone.ReferencePsdPath = "Changed.psd";
            clone.Width = 123;
            Check(original.ReferencePsdPath == "Assets/Reference.psd" && original.Width == 96,
                "Changing a batch configuration clone does not change the caller's settings.");
        }

        private static void TestPreflightAndStructure(string folder, string output)
        {
            WithPrefab(folder + "/Workflow.prefab", delegate(GameObject root)
            {
                PrefabPsdDemo.Image(root.transform, "Panel", 8, 8, 24, 16, Color.red);
                Text caption = PrefabPsdDemo.Rect(root.transform, "Caption", 8, 36, 70, 20)
                    .gameObject.AddComponent<Text>();
                caption.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                caption.text = "Raster text";
                caption.fontSize = 12;
                PrefabPsdDemo.Rect(root.transform, "EmptyContainer", 40, 8, 24, 16);
                PrefabPsdDemo.Rect(root.transform, "HiddenEmpty", 64, 8, 16, 16).gameObject.SetActive(false);
            }, delegate(GameObject prefab)
            {
                string path = AssetDatabase.GetAssetPath(prefab);
                string beforeBytes = HashFile(path);
                string beforeComponents = Snapshot(prefab);
                string beforeScenes = SceneSnapshot();
                ExportOptions structure = Options();
                PrefabPsdPresets.Apply(structure, ExportPreset.RoundTripStructure);
                structure.Width = 96;
                structure.Height = 64;
                structure.PreserveEmptyGroups = true;
                structure.AddCanvasMarker = true;
                PreflightReport preview = PrefabPsdPreflight.Inspect(prefab, structure);
                Check(preview.Width == 96 && preview.Height == 64 && preview.VisibleGraphicCount == 2,
                    "Preflight uses the requested design size and counts both visible Graphics.");
                Check(preview.NodeCount >= 5 && preview.HiddenCount >= 1,
                    "Preflight includes empty and hidden source structure.");
                Check(HashFile(path) == beforeBytes && Snapshot(prefab) == beforeComponents
                    && SceneSnapshot() == beforeScenes,
                    "Read-only preflight leaves prefab bytes, components and scene dirty flags unchanged.");

                Text sourceText = prefab.transform.Find("Caption").GetComponent<Text>();
                string rasterName = PrefabPsdNaming.GetName(sourceText.transform, sourceText, false,
                    structure, sourceText.name);
                Check(rasterName.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                    && !rasterName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase),
                    "Structure preset names rasterized text as image instead of pretending it has a TySh object.");

                ExportReport report = PrefabPsdExporter.Export(prefab,
                    Path.Combine(output, "workflow-structure.psd"), structure);
                Check(report.VerificationPassed, "Temporary PSD is verified before the structure export commits.");
                using (PsdReferenceDocument psd = PsdReferenceReader.Open(report.PsdPath))
                {
                    PsdReferenceLayer marker = Find(psd, "#Canvas", PsdLayerKind.Pixel);
                    Check(marker != null && !marker.Visible && marker.Left == 0 && marker.Top == 0
                        && marker.Width == 96 && marker.Height == 64,
                        "Hidden Canvas reference preserves the full design bounds.");
                    byte[] markerPixels = psd.ReadRgba(marker);
                    bool transparent = true;
                    for (int i = 3; i < markerPixels.Length; i += 4) transparent &= markerPixels[i] == 0;
                    Check(transparent, "Canvas reference contains no visible pixel data.");
                    Check(FindPrefix(psd, "EmptyContainer", PsdLayerKind.GroupStart) != null,
                        "Structure export preserves an empty positioning container.");
                    PsdReferenceLayer hidden = FindPrefix(psd, "HiddenEmpty", PsdLayerKind.GroupStart);
                    Check(hidden != null && !hidden.Visible, "Inactive empty group retains its hidden state.");
                    PsdReferenceLayer textLayer = Find(psd, rasterName, PsdLayerKind.Pixel);
                    Check(textLayer != null && !textLayer.HasTypeTool,
                        "Serialized text layer is a real raster layer without a misleading native text block.");
                }
                Check(HashFile(path) == beforeBytes && Snapshot(prefab) == beforeComponents,
                    "Structure export leaves the source prefab unchanged.");
            });
        }

        private static void TestReferenceChanges(string output)
        {
            Texture2D pixels = null;
            WithScene("Reference fixture", delegate(GameObject root)
            {
                PrefabPsdDemo.Image(root.transform, "Back", 8, 8, 40, 24, Color.red);
                PrefabPsdDemo.Image(root.transform, "Front", 20, 12, 24, 20, Color.blue);
                RawImage raw = PrefabPsdDemo.Rect(root.transform, "Pixels", 64, 8, 16, 16)
                    .gameObject.AddComponent<RawImage>();
                pixels = new Texture2D(16, 16, TextureFormat.RGBA32, false);
                pixels.filterMode = FilterMode.Point;
                pixels.wrapMode = TextureWrapMode.Clamp;
                Color32[] bytes = new Color32[16 * 16];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = new Color32(255, 0, 0, 255);
                pixels.SetPixels32(bytes);
                pixels.Apply(false, false);
                raw.texture = pixels;
            }, delegate(GameObject root)
            {
                string reference = Path.Combine(output, "workflow-reference-original.psd");
                ExportReport baseline = PrefabPsdExporter.Export(root, reference, Options());
                Check(baseline.VerificationPassed, "Generated reference passes persisted-layer verification.");
                ExportOptions referenceOptions = Options();
                referenceOptions.Preset = ExportPreset.SourceReference;
                referenceOptions.ReferencePsdPath = reference;
                referenceOptions.AutoDetectReferencePsd = false;
                ExportReport same = PrefabPsdExporter.Export(root,
                    Path.Combine(output, "workflow-reference-identical.psd"), referenceOptions);
                Check(same.ReferenceMatchedLayers == 3 && same.ReferenceReusedLayers == 3,
                    "Unchanged opaque layers can reuse exactly matching reference pixels.");
                Check(same.ReferencePath == Path.GetFullPath(reference),
                    "Export report records the explicit reference path.");

                byte[] originalPixels;
                using (PsdReferenceDocument doc = PsdReferenceReader.Open(reference))
                    originalPixels = doc.ReadRgba(Find(doc, "Pixels", PsdLayerKind.Pixel));
                pixels.SetPixel(0, 0, Color.green);
                pixels.Apply(false, false);
                ExportReport modified = PrefabPsdExporter.Export(root,
                    Path.Combine(output, "workflow-reference-one-pixel-changed.psd"), referenceOptions);
                Check(modified.ReferenceReusedLayers == 2,
                    "A single changed source texture pixel is not hidden by an approximate reference threshold.");
                using (PsdReferenceDocument doc = PsdReferenceReader.Open(modified.PsdPath))
                {
                    byte[] changed = doc.ReadRgba(Find(doc, "Pixels", PsdLayerKind.Pixel));
                    Check(ChangedPixels(originalPixels, changed) == 1,
                        "The changed output differs from the reference at exactly one visible texture pixel.");
                    int bottomLeft = (16 * 15) * 4;
                    Check(changed[bottomLeft] == 0 && changed[bottomLeft + 1] == 255
                        && changed[bottomLeft + 2] == 0 && changed[bottomLeft + 3] == 255,
                        "Reference reuse preserves the user's new green pixel.");
                }

                root.transform.Find("Front").SetSiblingIndex(0);
                ExportReport reordered = PrefabPsdExporter.Export(root,
                    Path.Combine(output, "workflow-reference-current-order.psd"), referenceOptions);
                using (PsdReferenceDocument doc = PsdReferenceReader.Open(reordered.PsdPath))
                {
                    PsdReferenceLayer back = Find(doc, "Back", PsdLayerKind.Pixel);
                    PsdReferenceLayer front = Find(doc, "Front", PsdLayerKind.Pixel);
                    Check(doc.Layers.IndexOf(back) < doc.Layers.IndexOf(front),
                        "Reference identity recovery preserves the user's current top-to-bottom layer order.");
                    HashSet<int> ids = new HashSet<int>();
                    foreach (PsdReferenceLayer layer in doc.Layers)
                        Check(layer.Id > 0 && ids.Add(layer.Id), "Restored layer IDs remain globally unique.");
                }

                string preservedReference = HashFile(reference);
                ExpectFailure(delegate { PrefabPsdExporter.Export(root, reference, referenceOptions); },
                    "Reference PSD cannot also be the export destination.");
                Check(HashFile(reference) == preservedReference, "Rejected output never overwrites the reference PSD.");
                string missingOutput = Path.Combine(output, "workflow-reference-missing.psd");
                referenceOptions.ReferencePsdPath = Path.Combine(output, "missing-reference-" + Guid.NewGuid().ToString("N") + ".psd");
                byte[] sentinel = Encoding.ASCII.GetBytes("Existing output survives bad reference settings.");
                File.WriteAllBytes(missingOutput, sentinel);
                ExpectFailure(delegate { PrefabPsdExporter.Export(root, missingOutput, referenceOptions); },
                    "Explicit missing reference fails instead of silently using default settings.");
                Check(Equal(sentinel, File.ReadAllBytes(missingOutput)),
                    "Invalid reference configuration preserves an existing output file.");
            }, delegate { if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels); });
        }

        private static void TestReferenceTextChange(string output)
        {
            WithScene("Text reference", delegate(GameObject root)
            {
                Text text = PrefabPsdDemo.Rect(root.transform, "Caption", 8, 8, 72, 40)
                    .gameObject.AddComponent<Text>();
                text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                text.fontSize = 16;
                text.text = "A";
                text.alignment = TextAnchor.UpperLeft;
                text.color = Color.white;
            }, delegate(GameObject root)
            {
                string reference = Path.Combine(output, "workflow-text-original.psd");
                PrefabPsdExporter.Export(root, reference, Options());
                root.transform.Find("Caption").GetComponent<Text>().alignment = TextAnchor.LowerLeft;
                ExportOptions options = Options();
                options.Preset = ExportPreset.SourceReference;
                options.ReferencePsdPath = reference;
                options.AutoDetectReferencePsd = false;
                ExportReport changed = PrefabPsdExporter.Export(root,
                    Path.Combine(output, "workflow-text-new-alignment.psd"), options);
                Check(changed.ReferenceReusedLayers == 0,
                    "Changing vertical text alignment does not reuse old reference glyph pixels.");
                using (PsdReferenceDocument before = PsdReferenceReader.Open(reference))
                using (PsdReferenceDocument after = PsdReferenceReader.Open(changed.PsdPath))
                {
                    PsdReferenceLayer original = Find(before, "Caption", PsdLayerKind.Pixel);
                    PsdReferenceLayer current = Find(after, "Caption", PsdLayerKind.Pixel);
                    Check(current != null && original != null && current.Top != original.Top,
                        "New text alignment remains visible in the output layer position.");
                }
            }, null);
        }

        private static void TestBatch(string folder, string output)
        {
            AssetDatabase.CreateFolder(folder, "BatchA");
            AssetDatabase.CreateFolder(folder, "BatchB");
            WithPrefab(folder + "/BatchA/Same.prefab", BuildBatchFixture, delegate(GameObject first)
            {
                WithPrefab(folder + "/BatchB/Same.prefab", BuildBatchFixture, delegate(GameObject second)
                {
                    string samePath = AssetDatabase.GetAssetPath(first);
                    string firstHash = HashFile(samePath);
                    string secondHash = HashFile(AssetDatabase.GetAssetPath(second));
                    ExportOptions options = Options();
                    PrefabPsdPresets.Apply(options, ExportPreset.ArtistDelivery);
                    options.Width = 120;
                    options.Height = 80;
                    options.PreserveHierarchy = false;
                    options.WriteMetadata = false;
                    options.Compression = PsdCompression.Raw;
                    string batchOutput = Path.Combine(output, "workflow-batch");
                    BatchExportReport batch = PrefabPsdBatchService.Export(new List<GameObject> { second, first, first },
                        batchOutput, options);
                    Check(batch.Results.Count == 2 && batch.Failures.Count == 0 && !batch.Cancelled
                        && batch.AttemptedCount == 2, "Batch deduplicates sources and exports both distinct prefab assets.");
                    Check(!string.Equals(batch.Results[0].PsdPath, batch.Results[1].PsdPath, StringComparison.OrdinalIgnoreCase),
                        "Equal prefab filenames from different folders never overwrite each other.");
                    foreach (ExportReport report in batch.Results)
                    {
                        Check(report.Width == 120 && report.Height == 80 && report.VerificationPassed,
                            "Batch retains the caller's dimensions and persisted verification option.");
                        Check(report.MetadataPath == null && !File.Exists(Path.ChangeExtension(report.PsdPath, ".prefab.json")),
                            "Batch retains WriteMetadata=false.");
                        Check(CompositeCompression(report.PsdPath) == 0, "Batch retains the requested Raw compression.");
                        using (PsdReferenceDocument doc = PsdReferenceReader.Open(report.PsdPath))
                            Check(Find(doc, "Panel", PsdLayerKind.Pixel) != null,
                                "Batch retains ArtistDelivery naming instead of recreating default options.");
                    }
                    Check(options.Width == 120 && options.Preset == ExportPreset.ArtistDelivery
                        && !options.WriteMetadata, "Batch leaves the caller configuration unchanged.");
                    BatchExportReport internalAsset = PrefabPsdBatchService.Export(
                        new List<GameObject> { first.transform.GetChild(0).gameObject },
                        Path.Combine(output, "workflow-batch-internal-asset"), options);
                    Check(internalAsset.Results.Count == 0 && internalAsset.Failures.Count == 1,
                        "Batch rejects persistent prefab children sharing a root GUID instead of risking output collisions.");

                    WithScene("Batch invalid source", delegate(GameObject root) { }, delegate(GameObject unused)
                    {
                        GameObject invalid = new GameObject("InvalidWithoutRectTransform");
                        SceneManager.MoveGameObjectToScene(invalid, unused.scene);
                        BatchExportReport partial = PrefabPsdBatchService.Export(
                            new List<GameObject> { invalid, first, second }, Path.Combine(output, "workflow-batch-partial"), options);
                        Check(partial.Results.Count == 2 && partial.Failures.Count == 1 && partial.AttemptedCount == 3,
                            "A failed batch source is reported while other sources complete.");
                    }, null);

                    int callbackCount = 0;
                    BatchExportReport cancelled = PrefabPsdBatchService.Export(new List<GameObject> { first, second },
                        Path.Combine(output, "workflow-batch-cancel"), options,
                        delegate(int index, int total, string name)
                        {
                            callbackCount++;
                            if (index == 1) throw new OperationCanceledException("Stop before second batch source.");
                        });
                    Check(callbackCount == 2 && cancelled.Cancelled && cancelled.Results.Count == 1,
                        "Batch cancellation after one commit keeps the completed result and stops later sources.");
                    using (PsdReferenceDocument doc = PsdReferenceReader.Open(cancelled.Results[0].PsdPath))
                        Check(doc.Width == 120 && doc.Height == 80, "Committed batch file remains valid after cancellation.");

                    ExportOptions invalidReference = Options();
                    invalidReference.Preset = ExportPreset.SourceReference;
                    invalidReference.ReferencePsdPath = Path.Combine(output, "batch-reference-missing.psd");
                    invalidReference.AutoDetectReferencePsd = false;
                    BatchExportReport referenceFailures = PrefabPsdBatchService.Export(new List<GameObject> { first, second },
                        Path.Combine(output, "workflow-batch-reference"), invalidReference);
                    Check(referenceFailures.Results.Count == 0 && referenceFailures.Failures.Count == 2,
                        "Batch carries explicit reference settings to each item instead of silently using defaults.");
                    Check(HashFile(samePath) == firstHash && HashFile(AssetDatabase.GetAssetPath(second)) == secondHash,
                        "Batch success, failure and cancellation leave both source prefab assets unchanged.");
                });
            });
        }

        private static void TestVerificationCancellation(string output)
        {
            WithScene("Verify cancellation", BuildBatchFixture, delegate(GameObject root)
            {
                string path = Path.Combine(output, "workflow-verify-cancel.psd");
                byte[] sentinel = Encoding.ASCII.GetBytes("Old destination must survive cancellation during verification.");
                File.WriteAllBytes(path, sentinel);
                ExportOptions options = Options();
                options.Progress = delegate(float value, string message)
                {
                    if (value >= 0.98f) throw new OperationCanceledException("Cancel before validated temporary file commits.");
                };
                bool cancelled = false;
                try { PrefabPsdExporter.Export(root, path, options); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && Equal(sentinel, File.ReadAllBytes(path)),
                    "Verification-stage cancellation preserves the previously committed output.");
                Check(Directory.GetFiles(output, "." + Path.GetFileName(path) + ".*.tmp").Length == 0,
                    "Verification-stage cancellation cleans the temporary PSD.");
            }, null);
        }

        private static void BuildBatchFixture(GameObject root)
        {
            PrefabPsdDemo.Image(root.transform, "Panel.img@top", 8, 8, 24, 16, Color.red);
        }

        private static ExportOptions Options()
        {
            return new ExportOptions { Width = 96, Height = 64, VerifyOutput = true, WriteMetadata = true };
        }

        private static void WithPrefab(string path, Action<GameObject> build, Action<GameObject> test)
        {
            WithScene(Path.GetFileNameWithoutExtension(path), build, delegate(GameObject root)
            {
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
                Check(prefab != null, "Workflow fixture prefab was saved.");
                test(prefab);
            }, null);
        }

        private static void WithScene(string name, Action<GameObject> build, Action<GameObject> test, Action cleanup)
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            GameObject root = null;
            try
            {
                root = PrefabPsdDemo.Root(name, 96, 64);
                SceneManager.MoveGameObjectToScene(root, scene);
                build(root);
                test(root);
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (cleanup != null) cleanup();
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static PsdReferenceLayer Find(PsdReferenceDocument doc, string name, PsdLayerKind kind)
        {
            foreach (PsdReferenceLayer layer in doc.Layers)
                if (layer.Kind == kind && layer.Name == name) return layer;
            return null;
        }

        private static PsdReferenceLayer FindPrefix(PsdReferenceDocument doc, string name, PsdLayerKind kind)
        {
            foreach (PsdReferenceLayer layer in doc.Layers)
                if (layer.Kind == kind && layer.Name.StartsWith(name, StringComparison.Ordinal)) return layer;
            return null;
        }

        private static int ChangedPixels(byte[] a, byte[] b)
        {
            Check(a.Length == b.Length, "Reference and changed layer have identical raster dimensions.");
            int changed = 0;
            for (int i = 0; i < a.Length; i += 4)
                if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3]) changed++;
            return changed;
        }

        private static int CompositeCompression(string path)
        {
            using (BinaryReader reader = new BinaryReader(File.OpenRead(path)))
            {
                reader.BaseStream.Position = 26;
                for (int i = 0; i < 3; i++)
                {
                    uint length = U32(reader);
                    reader.BaseStream.Position += length;
                }
                return (reader.ReadByte() << 8) | reader.ReadByte();
            }
        }

        private static uint U32(BinaryReader reader)
        {
            return ((uint)reader.ReadByte() << 24) | ((uint)reader.ReadByte() << 16)
                | ((uint)reader.ReadByte() << 8) | reader.ReadByte();
        }

        private static string Snapshot(GameObject root)
        {
            StringBuilder result = new StringBuilder();
            foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
            {
                result.Append(node.name).Append('|').Append(node.GetSiblingIndex()).Append('|')
                    .Append(node.gameObject.activeSelf).Append('\n');
                foreach (Component component in node.GetComponents<Component>())
                    result.Append(component == null ? "<Missing>" : EditorJsonUtility.ToJson(component)).Append('\n');
            }
            return result.ToString();
        }

        private static string SceneSnapshot()
        {
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                result.Append(scene.handle).Append('|').Append(scene.path).Append('|').Append(scene.isDirty).Append('\n');
            }
            return result.ToString();
        }

        private static string HashFile(string path)
        {
            using (SHA256 hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(path)));
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void ExpectFailure(Action action, string message)
        {
            bool failed = false;
            try { action(); }
            catch (Exception) { failed = true; }
            Check(failed, message);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Workflow regression: " + message);
            assertions++;
            Log("PASS workflow " + message);
        }

        private static void Log(string message)
        {
            if (log != null) log(message);
            else Debug.Log("[PrefabToPsd workflow test] " + message);
        }
    }
}
#endif
