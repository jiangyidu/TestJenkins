#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PrefabToPsd
{
    public sealed class BatchRequest
    {
        public readonly List<string> InputPaths = new List<string>();
        public string OutputFile;
        public string OutputDirectory;
        public readonly ExportOptions Options = new ExportOptions();
    }

    /// <summary>Same options and exporter as the window. GPU required; do not pass -nographics.</summary>
    public static class PrefabPsdBatch
    {
        public static void Export()
        {
            try
            {
                BatchRequest request = ParseArguments(Environment.GetCommandLineArgs());
                List<GameObject> sources = new List<GameObject>();
                foreach (string path in request.InputPaths)
                {
                    GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (source == null) throw new FileNotFoundException("Prefab asset not found: " + path);
                    sources.Add(source);
                }
                request.Options.Progress = delegate(float value, string message)
                {
                    Debug.Log("[PrefabToPsd] " + (int)(value * 100) + "% " + message);
                };
                bool failed = false;
                if (!string.IsNullOrEmpty(request.OutputFile))
                {
                    string parent = Path.GetDirectoryName(request.OutputFile);
                    Directory.CreateDirectory(parent);
                    ExportReport report = PrefabPsdExporter.Export(sources[0], request.OutputFile, request.Options);
                    PrintReport(report);
                }
                else
                {
                    BatchExportReport report = PrefabPsdBatchService.Export(sources, request.OutputDirectory, request.Options);
                    foreach (ExportReport success in report.Results) PrintReport(success);
                    foreach (BatchExportFailure failure in report.Failures)
                        Debug.LogError("[PrefabToPsd] FAILED " + failure.SourceName + ": " + failure.Message);
                    failed = report.Failures.Count > 0 || report.Cancelled;
                    Debug.Log("[PrefabToPsd] Batch: " + report.Results.Count + " succeeded, " + report.Failures.Count + " failed.");
                }
                if (Application.isBatchMode) EditorApplication.Exit(failed ? 1 : 0);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                else throw;
            }
        }

        /// <summary>Pure argument validation; does not load assets, render or change project state.</summary>
        public static BatchRequest ParseArguments(string[] args)
        {
            if (args == null) throw new ArgumentNullException("args");
            BatchRequest request = new BatchRequest();
            string preset = Argument(args, "-prefabToPsdPreset", false);
            if (preset != null)
            {
                ExportPreset selected;
                if (!Enum.TryParse<ExportPreset>(preset, true, out selected) || !Enum.IsDefined(typeof(ExportPreset), selected))
                    throw new ArgumentException("Unknown prefabToPsdPreset: " + preset);
                PrefabPsdPresets.Apply(request.Options, selected);
            }
            string input = Argument(args, "-prefabToPsdInput", false);
            string listFile = Argument(args, "-prefabToPsdInputsFile", false);
            if ((input == null) == (listFile == null))
                throw new ArgumentException("Provide exactly one of -prefabToPsdInput or -prefabToPsdInputsFile.");
            if (input != null) request.InputPaths.Add(input.Replace('\\', '/'));
            else
            {
                foreach (string line in File.ReadAllLines(listFile))
                {
                    string path = line.Trim();
                    if (path.Length > 0) request.InputPaths.Add(path.Replace('\\', '/'));
                }
            }
            if (request.InputPaths.Count == 0) throw new ArgumentException("The input list is empty.");
            foreach (string path in request.InputPaths)
                if (!path.StartsWith("Assets/", StringComparison.Ordinal)
                    || path.IndexOf("/../", StringComparison.Ordinal) >= 0
                    || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Input must be an Assets/... .prefab asset path: " + path);
            request.OutputFile = Argument(args, "-prefabToPsdOutput", false);
            request.OutputDirectory = Argument(args, "-prefabToPsdOutputDirectory", false);
            if ((request.OutputFile == null) == (request.OutputDirectory == null))
                throw new ArgumentException("Provide exactly one of -prefabToPsdOutput or -prefabToPsdOutputDirectory.");
            if (request.OutputFile != null)
            {
                if (request.InputPaths.Count != 1) throw new ArgumentException("Multiple inputs require -prefabToPsdOutputDirectory.");
                if (!Path.IsPathRooted(request.OutputFile) || !string.Equals(Path.GetExtension(request.OutputFile), ".psd", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Output must be an absolute .psd file path.");
                request.OutputFile = Path.GetFullPath(request.OutputFile);
            }
            else
            {
                if (!Path.IsPathRooted(request.OutputDirectory)) throw new ArgumentException("Output directory must be absolute.");
                request.OutputDirectory = Path.GetFullPath(request.OutputDirectory);
            }
            string width = Argument(args, "-prefabToPsdWidth", false);
            string height = Argument(args, "-prefabToPsdHeight", false);
            if (width != null || height != null)
            {
                if (width == null || height == null) throw new ArgumentException("Width and height must be supplied together.");
                request.Options.Width = PositiveInt(width, "width");
                request.Options.Height = PositiveInt(height, "height");
            }
            string memory = Argument(args, "-prefabToPsdMemoryMB", false);
            if (memory != null)
            {
                request.Options.MemoryBudgetMB = PositiveInt(memory, "memory");
                if (request.Options.MemoryBudgetMB < 16 || request.Options.MemoryBudgetMB > 4096)
                    throw new ArgumentException("Memory budget must be 16..4096 MB.");
            }
            string reference = Argument(args, "-prefabToPsdReference", false);
            if (reference != null) request.Options.ReferencePsdPath = reference;
            if (Has(args, "-prefabToPsdNoVerify")) request.Options.VerifyOutput = false;
            if (Has(args, "-prefabToPsdFlat")) request.Options.PreserveHierarchy = false;
            if (Has(args, "-prefabToPsdNoMetadata")) request.Options.WriteMetadata = false;
            return request;
        }

        private static void PrintReport(ExportReport report)
        {
            Debug.Log("[PrefabToPsd] SUCCESS " + report.PsdPath + ", layers=" + report.LayerCount
                + ", verified=" + report.VerificationPassed + ", referenceReused=" + report.ReferenceReusedLayers);
            foreach (string warning in report.Warnings) Debug.LogWarning("[PrefabToPsd] " + warning);
        }

        private static int PositiveInt(string value, string label)
        {
            int number;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 1)
                throw new ArgumentException(label + " must be a positive integer.");
            return number;
        }

        private static bool Has(string[] args, string key) { return Array.IndexOf(args, key) >= 0; }

        private static string Argument(string[] args, string key, bool required)
        {
            string result = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] != key) continue;
                if (result != null) throw new ArgumentException("Duplicate argument: " + key);
                if (i + 1 >= args.Length || args[i + 1].StartsWith("-"))
                    throw new ArgumentException("Missing value: " + key);
                result = args[i + 1];
            }
            if (required && result == null) throw new ArgumentException("Missing argument: " + key);
            return result;
        }
    }
}
#endif
