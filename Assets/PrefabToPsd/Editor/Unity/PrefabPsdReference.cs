#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using PrefabToPsd.Core;
using UnityEditor;
using UnityEngine;

namespace PrefabToPsd
{
    /// <summary>
    /// Read-only reference assistance. The current render and panel order remain authoritative.
    /// No fuzzy text/style matching, layer-order restoration or near-equal pixel replacement.
    /// </summary>
    public static class PrefabPsdReference
    {
        public static string ResolvePath(GameObject source, ExportOptions options, List<string> warnings)
        {
            if (options == null) throw new ArgumentNullException("options");
            bool requested = options.Preset == ExportPreset.SourceReference ||
                !string.IsNullOrEmpty(options.ReferencePsdPath);
            if (!requested) return null;
            if (!string.IsNullOrEmpty(options.ReferencePsdPath))
                return RequireExistingPsd(options.ReferencePsdPath);
            if (!options.AutoDetectReferencePsd)
                throw new FileNotFoundException("Source Reference requires an explicit reference PSD when automatic discovery is disabled.");

            HashSet<string> paths = new HashSet<string>(Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (!object.ReferenceEquals(source, null)) CollectAutomaticCandidates(source, paths, warnings);
            if (paths.Count > 1)
                throw new InvalidOperationException("Multiple reference PSD candidates were found. Select the intended PSD explicitly.");
            foreach (string path in paths) return path;
            throw new FileNotFoundException("No reference PSD was found. Select the original RGB8 PSD explicitly.");
        }

        // Keep native editor calls outside the pure explicit-path branch. Besides making
        // responsibilities clear, this lets managed tests exercise path validation without
        // JIT-compiling Unity's registered native AssetDatabase calls.
        private static void CollectAutomaticCandidates(GameObject source, HashSet<string> paths, List<string> warnings)
        {
            if (source != null)
            {
                foreach (Component component in source.GetComponents<Component>())
                {
                    if (component == null) continue;
                    Type type = component.GetType();
                    // Invoke only known provenance components, never arbitrary project getters.
                    if (type.FullName != "Mars.PsdTOUgui.PsdGeneratedScreen") continue;
                    foreach (string memberName in new[] { "SourceAssetPath", "SourcePsdAssetPath", "SourcePsdPath" })
                    {
                        try
                        {
                            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
                            if (property != null && property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0)
                                AddExistingPath(paths, property.GetValue(component, null) as string);
                            FieldInfo field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
                            if (field != null && field.FieldType == typeof(string)) AddExistingPath(paths, field.GetValue(component) as string);
                        }
                        catch (Exception ex) { Warn(warnings, "Reference provenance could not be read: " + ex.Message); }
                    }
                }
                string assetPath = AssetDatabase.GetAssetPath(source);
                if (string.IsNullOrEmpty(assetPath) && source.scene.IsValid())
                    assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(source);
                if (!string.IsNullOrEmpty(assetPath)) AddExistingPath(paths, Path.ChangeExtension(assetPath, ".psd"));
            }
        }

        public static void Apply(PsdDocument document, PrefabPsdMetadataDocument metadata,
            ExportOptions options, ExportReport report, string resolvedReferencePath)
        {
            if (document == null || metadata == null || options == null || report == null)
                throw new ArgumentNullException("document", "Document, metadata, options and report are required.");
            if (string.IsNullOrEmpty(resolvedReferencePath)) return;
            string referencePath = RequireExistingPsd(resolvedReferencePath);
            if (!string.IsNullOrEmpty(report.PsdPath) &&
                string.Equals(referencePath, Path.GetFullPath(report.PsdPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The output PSD must differ from the read-only reference PSD.");

            Dictionary<int, PsdLayerMetadata> byId = new Dictionary<int, PsdLayerMetadata>();
            foreach (PsdLayerMetadata item in metadata.layers)
            {
                if (byId.ContainsKey(item.psdLayerId)) throw new ArgumentException("Metadata contains duplicate export layer IDs.");
                byId.Add(item.psdLayerId, item);
            }
            Dictionary<string, PrefabPsdNodeMetadata> nodes = new Dictionary<string, PrefabPsdNodeMetadata>(StringComparer.Ordinal);
            foreach (PrefabPsdNodeMetadata item in metadata.nodes)
            {
                if (item == null || string.IsNullOrEmpty(item.hierarchyPath)) continue;
                if (nodes.ContainsKey(item.hierarchyPath)) throw new ArgumentException("Metadata contains duplicate hierarchy keys.");
                nodes.Add(item.hierarchyPath, item);
            }
            Dictionary<int, int> sourceIdCounts = new Dictionary<int, int>();
            Dictionary<PsdLayer, PsdLayerMetadata> associated = new Dictionary<PsdLayer, PsdLayerMetadata>();
            foreach (PsdLayer layer in document.Layers)
            {
                PsdLayerMetadata item;
                if (layer.Kind != PsdLayerKind.Pixel || !byId.TryGetValue(layer.Id, out item)) continue;
                associated.Add(layer, item);
                PrefabPsdNodeMetadata node;
                if (nodes.TryGetValue(item.hierarchyPath ?? "", out node) && node.originalPsd != null && node.originalPsd.sourceLayerId > 0)
                {
                    int id = node.originalPsd.sourceLayerId;
                    int count;
                    sourceIdCounts.TryGetValue(id, out count);
                    sourceIdCounts[id] = count + 1;
                }
            }
            Dictionary<PsdLayer, int> requestedIds = new Dictionary<PsdLayer, int>();
            HashSet<PsdReferenceLayer> used = new HashSet<PsdReferenceLayer>();
            long decodeBudget = Math.Max(65536L, Math.Min(268435456L, options.MemoryBudgetMB * 1024L * 1024L / 4L));
            using (PsdReferenceDocument reference = PsdReferenceReader.Open(referencePath,
                maxDecodedBytes: decodeBudget))
            {
                if (reference.Width != document.Width || reference.Height != document.Height)
                    Warn(report.Warnings, "Reference canvas dimensions differ; pixel reuse still requires exact current layer bounds and bytes.");
                foreach (PsdLayer layer in document.Layers)
                {
                    PsdLayerMetadata item;
                    if (!associated.TryGetValue(layer, out item)) continue;
                    item.referenceMatched = false;
                    item.referencePixelsReused = false;
                    PrefabPsdNodeMetadata node;
                    nodes.TryGetValue(item.hierarchyPath ?? "", out node);
                    PsdReferenceLayer match = Match(layer, node, reference.Layers, used, sourceIdCounts);
                    if (match == null) continue;
                    used.Add(match);
                    report.ReferenceMatchedLayers++;
                    item.referenceMatched = true;
                    item.referenceName = match.Name;
                    item.referenceId = match.Id;
                    if (options.PreserveReferenceIds && match.Id > 0) requestedIds[layer] = match.Id;

                    bool identical = false;
                    byte[] pixels = null;
                    if (match.EligibleForPixelReuse && match.Opacity == 255 && layer.Opacity == 255 &&
                        layer.Visible == match.Visible && BoundsEqual(layer, match))
                    {
                        try
                        {
                            pixels = reference.ReadRgba(match);
                            identical = Equal(layer.Rgba, pixels);
                        }
                        catch (Exception ex)
                        {
                            if (!(ex is InvalidDataException) && !(ex is NotSupportedException) && !(ex is ArgumentException)) throw;
                            Warn(report.Warnings, "Reference pixels were not reusable for '" + layer.Name + "': " + ex.Message);
                        }
                    }
                    if (!identical) continue;
                    if (options.ReuseIdenticalReferencePixels)
                    {
                        layer.Rgba = pixels;
                        report.ReferenceReusedLayers++;
                        item.referencePixelsReused = true;
                    }
                    if (options.PreserveReferenceNames && SourceNameUnchanged(node, match))
                    {
                        if (IsSafeRasterName(match.Name)) layer.Name = match.Name;
                        else Warn(report.Warnings, "Reference name '" + match.Name + "' was not restored because it contains importer semantics for already rasterized content. The original is recorded in JSON; current naming follows the selected preset.");
                    }
                }
                if (options.CopySafeReferenceResources) CopyResources(document, reference, report);
            }
            ApplyUniqueIds(document.Layers, associated, requestedIds, report);
            foreach (KeyValuePair<PsdLayer, PsdLayerMetadata> pair in associated)
            {
                pair.Value.psdLayerId = pair.Key.Id;
                pair.Value.name = pair.Key.Name;
            }
            report.ReferencePath = referencePath;
            metadata.referencePath = referencePath;
            metadata.referenceMatchedLayers = report.ReferenceMatchedLayers;
            metadata.referenceReusedLayers = report.ReferenceReusedLayers;
            metadata.referenceRestoredIds = report.ReferenceRestoredIds;
            metadata.referenceColorProfileCopied = report.ReferenceColorProfileCopied;
            Warn(report.Warnings, "Reference assistance preserves current layer order and never recreates Photoshop text or effects. Pixels are reused only when bounds and every RGBA byte match.");
        }

        private static PsdReferenceLayer Match(PsdLayer layer, PrefabPsdNodeMetadata node,
            List<PsdReferenceLayer> references, HashSet<PsdReferenceLayer> used, Dictionary<int, int> sourceIdCounts)
        {
            PrefabPsdOriginalMetadata original = node == null ? null : node.originalPsd;
            if (original != null && original.sourceLayerId > 0 && sourceIdCounts[original.sourceLayerId] == 1)
            {
                PsdReferenceLayer idMatch = Unique(references, used,
                    delegate(PsdReferenceLayer value) { return value.Id == original.sourceLayerId; });
                if (idMatch != null) return idMatch;
            }
            if (original != null && !string.IsNullOrEmpty(original.sourceLayerName))
            {
                PsdReferenceLayer sourceName = Unique(references, used,
                    delegate(PsdReferenceLayer value)
                    { return string.Equals(value.Name, original.sourceLayerName, StringComparison.Ordinal) && BoundsEqual(layer, value); });
                if (sourceName != null) return sourceName;
            }
            return Unique(references, used, delegate(PsdReferenceLayer value)
            {
                return BoundsEqual(layer, value) &&
                    (string.Equals(value.Name, layer.Name, StringComparison.Ordinal) ||
                     (node != null && string.Equals(value.Name, node.name, StringComparison.Ordinal)));
            });
        }

        private static PsdReferenceLayer Unique(List<PsdReferenceLayer> values,
            HashSet<PsdReferenceLayer> used, Predicate<PsdReferenceLayer> test)
        {
            PsdReferenceLayer found = null;
            foreach (PsdReferenceLayer value in values)
            {
                if (value.Kind != PsdLayerKind.Pixel || !test(value)) continue;
                // A used duplicate is still ambiguous, rather than silently selecting a later layer.
                if (found != null || used.Contains(value)) return null;
                found = value;
            }
            return found;
        }

        private static bool BoundsEqual(PsdLayer layer, PsdReferenceLayer reference)
        {
            return layer.Left == reference.Left && layer.Top == reference.Top &&
                layer.Width == reference.Width && layer.Height == reference.Height;
        }

        private static bool SourceNameUnchanged(PrefabPsdNodeMetadata node, PsdReferenceLayer reference)
        {
            if (node == null) return false;
            // No old generated-name snapshot exists. If an importer renamed the object,
            // do not guess whether the user subsequently renamed it again.
            if (string.Equals(node.name, reference.Name, StringComparison.Ordinal)) return true;
            return node.originalPsd != null &&
                string.Equals(node.name, node.originalPsd.sourceLayerName, StringComparison.Ordinal) &&
                string.Equals(node.originalPsd.sourceLayerName, reference.Name, StringComparison.Ordinal);
        }

        public static bool IsSafeRasterName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('@') >= 0 || name[0] == '#' || name[0] == '$') return false;
            foreach (string suffix in new[] { "txt", "text", "label", "tmptxt", "tmptext", "col", "color", "fill", "fillcolor", "msk", "mask", "viewport", "content", "btn", "button", "tg", "toggle", "sld", "slider", "inp", "input", "sv", "scroll", "prefab", "canvas" })
                if (name.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase) || name.EndsWith("_" + suffix, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static void CopyResources(PsdDocument document, PsdReferenceDocument reference, ExportReport report)
        {
            HashSet<ushort> present = new HashSet<ushort>();
            foreach (PsdResource resource in document.SafeResources) present.Add(resource.Id);
            foreach (PsdResource resource in reference.Resources)
            {
                if (resource.Id != 1005 && resource.Id != 1039 && resource.Id != 1064) continue;
                if (present.Contains(resource.Id)) continue;
                if (resource.Id == 1039 && !PsdSafeResources.IsSrgbIccProfile(resource.Data))
                {
                    Warn(report.Warnings, "The reference ICC profile was not copied: current Unity pixels require a verified sRGB profile, not a foreign or unknown RGB workspace.");
                    continue;
                }
                try
                {
                    PsdSafeResources.Validate(resource);
                    if (resource.Id == 1064 && !IsSquarePixelAspect(resource.Data))
                    {
                        Warn(report.Warnings, "The reference PixelAspectRatio was not copied: the current Unity design canvas uses square pixels.");
                        continue;
                    }
                    document.SafeResources.Add(new PsdResource { Id = resource.Id, Data = (byte[])resource.Data.Clone() });
                    present.Add(resource.Id);
                    if (resource.Id == 1039) report.ReferenceColorProfileCopied = true;
                }
                catch (ArgumentException ex) { Warn(report.Warnings, "Reference resource " + resource.Id + " was skipped: " + ex.Message); }
                catch (InvalidDataException ex) { Warn(report.Warnings, "Reference resource " + resource.Id + " was skipped: " + ex.Message); }
            }
        }

        private static bool IsSquarePixelAspect(byte[] data)
        {
            // Resource validation has already checked version, length and finite range.
            if (data == null || data.Length != 12) return false;
            byte[] bytes = new byte[8];
            Buffer.BlockCopy(data, 4, bytes, 0, 8);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return Math.Abs(BitConverter.ToDouble(bytes, 0) - 1.0) <= 0.000000001;
        }

        private static void ApplyUniqueIds(List<PsdLayer> layers,
            Dictionary<PsdLayer, PsdLayerMetadata> metadata, Dictionary<PsdLayer, int> requests, ExportReport report)
        {
            HashSet<int> reserved = new HashSet<int>();
            List<PsdLayer> rejected = new List<PsdLayer>();
            foreach (KeyValuePair<PsdLayer, int> pair in requests)
                if (pair.Value < 1 || !reserved.Add(pair.Value)) rejected.Add(pair.Key);
            foreach (PsdLayer layer in rejected)
            {
                requests.Remove(layer);
                Warn(report.Warnings, "A duplicate reference layer ID was not assigned twice.");
            }
            HashSet<int> used = new HashSet<int>();
            int next = 1;
            foreach (PsdLayer layer in layers)
            {
                int requested;
                if (requests.TryGetValue(layer, out requested))
                {
                    if (layer.Id != requested) report.ReferenceRestoredIds++;
                    layer.Id = requested;
                }
                else if (layer.Id <= 0 || reserved.Contains(layer.Id) || used.Contains(layer.Id))
                {
                    while (reserved.Contains(next) || used.Contains(next)) next++;
                    layer.Id = next++;
                }
                if (!used.Add(layer.Id)) throw new InvalidDataException("Reference identity assignment generated a duplicate ID.");
            }
        }

        private static bool Equal(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static void AddExistingPath(HashSet<string> paths, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string full = Path.GetFullPath(path);
                if (string.Equals(Path.GetExtension(full), ".psd", StringComparison.OrdinalIgnoreCase) && File.Exists(full)) paths.Add(full);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
        }

        private static string RequireExistingPsd(string path)
        {
            string full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(full), ".psd", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The reference must be a .psd file.");
            if (!File.Exists(full)) throw new FileNotFoundException("Reference PSD was not found.", full);
            return full;
        }

        private static void Warn(List<string> warnings, string message)
        {
            if (warnings != null && !warnings.Contains(message)) warnings.Add(message);
        }
    }
}
#endif
