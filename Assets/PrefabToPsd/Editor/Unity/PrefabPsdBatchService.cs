#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PrefabToPsd
{
    public sealed class BatchSourceIdentity
    {
        public string SourceName;
        public string SourcePath;
        public string Guid;
        public string StableKey;
    }

    public sealed class BatchExportFailure
    {
        public string SourceName;
        public string SourcePath;
        public string OutputPath;
        public string Message;
    }

    public sealed class BatchExportReport
    {
        public readonly List<ExportReport> Results = new List<ExportReport>();
        public readonly List<BatchExportFailure> Failures = new List<BatchExportFailure>();
        public bool Cancelled;
        public int AttemptedCount;
        public int SkippedDuplicateCount;
    }

    /// <summary>Shared GUI/CLI batch operation. The caller owns any progress UI.</summary>
    public static class PrefabPsdBatchService
    {
        private sealed class SourceItem
        {
            public GameObject Source;
            public BatchSourceIdentity Identity;
        }

        public static BatchExportReport Export(IList<GameObject> sources, string outputDirectory,
            ExportOptions options, Action<int, int, string> itemProgress = null)
        {
            if (sources == null) throw new ArgumentNullException("sources");
            if (sources.Count == 0) throw new ArgumentException("At least one source is required.", "sources");
            if (string.IsNullOrEmpty(outputDirectory)) throw new ArgumentException("An output directory is required.", "outputDirectory");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("Wait for compilation and exit Play Mode before exporting.");
            string directory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(directory);
            ExportOptions snapshot = (options ?? new ExportOptions()).Clone();
            Action<float, string> progress = snapshot.Progress;
            BatchExportReport report = new BatchExportReport();
            List<SourceItem> items = new List<SourceItem>();
            HashSet<int> instances = new HashSet<int>();
            for (int i = 0; i < sources.Count; i++)
            {
                GameObject source = sources[i];
                if (source == null)
                {
                    report.AttemptedCount++;
                    report.Failures.Add(new BatchExportFailure { SourceName = "(Missing object)", SourcePath = "",
                        OutputPath = "", Message = "Source is null or has been destroyed." });
                    continue;
                }
                if (!instances.Add(source.GetInstanceID())) { report.SkippedDuplicateCount++; continue; }
                string assetPath = AssetDatabase.GetAssetPath(source);
                // A prefab's persistent children share its GUID. Restrict asset batches
                // to roots so two internal objects cannot target each other's output.
                // A selected scene subtree still has its own hierarchy identity.
                if (EditorUtility.IsPersistent(source) && source.transform.parent != null)
                {
                    report.AttemptedCount++;
                    report.Failures.Add(new BatchExportFailure { SourceName = source.name, SourcePath = assetPath,
                        OutputPath = "", Message = "Batch asset sources must be prefab roots. Export an internal prefab subtree with the single-object exporter instead." });
                    continue;
                }
                string guid = string.IsNullOrEmpty(assetPath) ? "" : AssetDatabase.AssetPathToGUID(assetPath);
                string sceneKey = string.IsNullOrEmpty(assetPath) ? SceneKey(source) : "";
                items.Add(new SourceItem { Source = source, Identity = new BatchSourceIdentity {
                    SourceName = source.name, SourcePath = assetPath, Guid = guid,
                    StableKey = !string.IsNullOrEmpty(guid) ? "asset:" + guid : "scene:" + sceneKey } });
            }
            items.Sort(delegate(SourceItem left, SourceItem right)
            { return StringComparer.Ordinal.Compare(IdentityKey(left.Identity, 0), IdentityKey(right.Identity, 0)); });
            List<BatchSourceIdentity> identities = new List<BatchSourceIdentity>();
            foreach (SourceItem item in items) identities.Add(item.Identity);
            List<string> names = PlanFileNames(identities);
            for (int i = 0; i < items.Count; i++)
            {
                SourceItem item = items[i];
                string path = Path.Combine(directory, names[i]);
                report.AttemptedCount++;
                try
                {
                    // All callbacks occur before or during the next export. A cancellation
                    // after a prior commit therefore cannot erase or relabel that success.
                    if (itemProgress != null) itemProgress(i, items.Count, item.Identity.SourceName);
                    if (progress != null) progress(i / (float)items.Count, "Batch: " + item.Identity.SourceName);
                    int batchIndex = i;
                    ExportOptions itemOptions = snapshot.Clone();
                    itemOptions.Progress = delegate(float value, string message)
                    {
                        if (progress != null)
                            progress((batchIndex + Mathf.Clamp01(value)) / items.Count,
                                "[" + (batchIndex + 1) + "/" + items.Count + "] " + message);
                    };
                    ExportReport exported = PrefabPsdExporter.Export(item.Source, path, itemOptions);
                    report.Results.Add(exported);
                }
                catch (OperationCanceledException)
                {
                    report.Cancelled = true;
                    break;
                }
                catch (Exception exception)
                {
                    report.Failures.Add(new BatchExportFailure { SourceName = item.Identity.SourceName,
                        SourcePath = item.Identity.SourcePath, OutputPath = path, Message = exception.Message });
                }
            }
            return report;
        }

        /// <summary>
        /// Pure filename planning in input order. Case-insensitive collision handling is
        /// deterministic across platforms and does not use Directory.GetFiles or overwrite
        /// one input with another. Re-running the same inputs intentionally targets the same
        /// files, protected by the exporter's atomic commit.
        /// </summary>
        public static List<string> PlanFileNames(IList<BatchSourceIdentity> identities)
        {
            if (identities == null) throw new ArgumentNullException("identities");
            List<string> bases = new List<string>();
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<int> order = new List<int>();
            for (int i = 0; i < identities.Count; i++)
            {
                if (identities[i] == null) throw new ArgumentException("A batch identity is null.", "identities");
                string name = PrefabPsdNaming.SafeFileName(identities[i].SourceName);
                bases.Add(name); order.Add(i);
                int count; counts.TryGetValue(name, out count); counts[name] = count + 1;
            }
            order.Sort(delegate(int left, int right)
            {
                int compare = StringComparer.Ordinal.Compare(IdentityKey(identities[left], left), IdentityKey(identities[right], right));
                return compare != 0 ? compare : left.CompareTo(right);
            });
            List<string> result = new List<string>(new string[identities.Count]);
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int index in order)
            {
                string key = IdentityKey(identities[index], index);
                string desired = bases[index];
                BatchSourceIdentity identity = identities[index];
                bool hasStableIdentity = !string.IsNullOrEmpty(identity.Guid)
                    || !string.IsNullOrEmpty(identity.StableKey) || !string.IsNullOrEmpty(identity.SourcePath);
                // The suffix cannot depend on the other selected objects: otherwise
                // exporting MainA alone and MainB alone would both overwrite Main.psd.
                if (hasStableIdentity || counts[desired] > 1) desired += "_" + ShortIdentity(identity, key);
                string candidate = desired + ".psd";
                if (used.Contains(candidate))
                {
                    desired += "_" + Digest(key);
                    candidate = desired + ".psd";
                    int duplicate = 2;
                    while (used.Contains(candidate))
                        candidate = desired + "_" + (duplicate++).ToString(CultureInfo.InvariantCulture) + ".psd";
                }
                used.Add(candidate); result[index] = candidate;
            }
            return result;
        }

        private static string IdentityKey(BatchSourceIdentity identity, int fallbackIndex)
        {
            if (!string.IsNullOrEmpty(identity.StableKey)) return identity.StableKey;
            if (!string.IsNullOrEmpty(identity.Guid)) return "asset:" + identity.Guid.ToLowerInvariant();
            if (!string.IsNullOrEmpty(identity.SourcePath)) return "path:" + identity.SourcePath.Replace('\\', '/');
            return "name:" + (identity.SourceName ?? "") + ":" + fallbackIndex.ToString("D8", CultureInfo.InvariantCulture);
        }

        private static string ShortIdentity(BatchSourceIdentity identity, string key)
        {
            string guid = identity.Guid ?? "";
            if (guid.Length == 32)
            {
                bool valid = true;
                for (int i = 0; i < guid.Length; i++)
                    if (!Uri.IsHexDigit(guid[i])) { valid = false; break; }
                if (valid) return guid.ToLowerInvariant();
            }
            return Digest(key);
        }

        private static string Digest(string value)
        {
            using (SHA256 hash = SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(value));
                StringBuilder text = new StringBuilder(16);
                for (int i = 0; i < 8; i++) text.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        private static string SceneKey(GameObject source)
        {
            List<string> parts = new List<string>();
            Transform current = source.transform;
            while (current != null)
            {
                string name = current.name;
                parts.Add(current.GetSiblingIndex().ToString(CultureInfo.InvariantCulture) + ":"
                    + name.Length.ToString(CultureInfo.InvariantCulture) + ":" + name);
                current = current.parent;
            }
            parts.Reverse();
            string scene = source.scene.path;
            if (string.IsNullOrEmpty(scene))
                scene = "(unsaved:" + source.scene.handle.ToString(CultureInfo.InvariantCulture) + ")";
            return scene.Length.ToString(CultureInfo.InvariantCulture) + ":" + scene + "/" + string.Join("/", parts.ToArray());
        }
    }
}
#endif
