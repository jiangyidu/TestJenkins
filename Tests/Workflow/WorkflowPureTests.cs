using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PrefabToPsd;
using PrefabToPsd.Core;

/// <summary>Real package methods only; no Unity native object creation or API stubs.</summary>
internal static class WorkflowPureTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
        Console.WriteLine("PASS " + message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        bool caught = false;
        try { action(); }
        catch (T) { caught = true; }
        Check(caught, message);
    }

    public static int Main(string[] args)
    {
        try
        {
            string output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("workflow-results");
            Directory.CreateDirectory(output);
            TestNames();
            TestPlans();
            TestPresetsAndClone();
            TestArguments(output);
            Console.WriteLine("PASS: " + assertions + " workflow pure assertions. Unity native objects/rendering were not invoked.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void TestNames()
    {
        Check(PrefabPsdNaming.CleanArtistName("login_bg") == "login_bg", "Business underscore suffix remains intact");
        Check(PrefabPsdNaming.CleanArtistName("screen_login_panel") == "screen_login_panel", "Multiple business underscores remain intact");
        Check(PrefabPsdNaming.CleanArtistName("#Title.txt@top") == "Title", "Known text/layout markers are cleaned for artists");
        Check(PrefabPsdNaming.CleanArtistName("Card.img @slice=4|4|4|4") == "Card", "Known slice annotation is removed from baked artist output");
        Check(PrefabPsdNaming.CleanArtistName("Card.img@top@stretchx") == "Card", "Adjacent bare layout tags are all removed");
        Check(PrefabPsdNaming.CleanArtistName("Card.img@width=80@height=40") == "Card", "Adjacent value tags are removed");
        Check(PrefabPsdNaming.CleanArtistName("name@home") == "name@home", "Unknown at-sign text is not mistaken for layout DSL");
        Check(PrefabPsdNaming.CleanArtistName("report.json") == "report.json", "Unknown dot suffix remains intact");
        Check(PrefabPsdNaming.CleanArtistName("$$  ") == "Layer", "Empty cleaned name has a usable fallback");
        ExportOptions roundTrip = new ExportOptions { Preset = ExportPreset.RoundTripStructure };
        Check(PrefabPsdNaming.GetName(null, null, false, roundTrip, "Title.txt") == "Title.img", "Baked text is an image, never a fake native text layer");
        Check(PrefabPsdNaming.GetName(null, null, false, roundTrip, "Button [Graphic]") == "Button.bg", "Container's own rendered graphic has a background role");
        Check(PrefabPsdNaming.GetName(null, null, true, roundTrip, "Container") == "Container.group", "Plain folder receives structural group naming");
        Check(PrefabPsdNaming.GetName(null, null, false, new ExportOptions(), "literal.txt") == "literal.txt", "Current appearance preserves the current name");
        ExportOptions reference = new ExportOptions { Preset = ExportPreset.SourceReference };
        Check(PrefabPsdNaming.GetName(null, null, false, reference, "Title.txt") == "Title.img", "Reference mode neutralizes an existing fake native-text suffix");
        Check(PrefabPsdNaming.GetName(null, null, false, reference, "login_txt") == "login_txt.img", "Reference mode neutralizes underscore text semantics without deleting business text");
        Check(PrefabPsdNaming.GetName(null, null, false, reference, "Panel.img@slice=4|4|4|4") == "Panel.img", "Reference mode removes already-baked slice directives");
        Check(PrefabPsdNaming.GetName(null, null, false, reference, "$Hidden.img") == "Hidden.img", "Reference mode removes promotion/skip directives from real pixels");
        Check(PrefabPsdNaming.GetName(null, null, true, reference, "#Folder") == "Folder.group", "Reference mode keeps structural folders from being skipped on import");
        Check(PrefabPsdNaming.GetName(null, null, false, reference, "Plain Original Name") == "Plain Original Name", "Reference mode keeps an ordinary safe name exactly");
        Check(PrefabPsdNaming.GetName(null, null, false, reference, "Panel.img") == "Panel.img", "Reference mode keeps a safe image name exactly");
        Check(PrefabPsdNaming.SafeFileName("CON") == "_CON", "Windows reserved device name is escaped");
        Check(PrefabPsdNaming.SafeFileName("aux.txt") == "_aux.txt", "Reserved stem with an extension is escaped");
        Check(PrefabPsdNaming.SafeFileName("LPT9") == "_LPT9", "Reserved numbered device is escaped");
        Check(PrefabPsdNaming.SafeFileName("LPT10") == "LPT10", "Nonreserved numbered name is preserved");
        Check(PrefabPsdNaming.SafeFileName(" UI. ") == "UI", "Trailing spaces and periods are removed");
        Check(PrefabPsdNaming.SafeFileName("a/b\\c:d*e?f\"g<h>i|j") == "a_b_c_d_e_f_g_h_i_j", "Invalid characters are sanitized for all target platforms");
        Check(PrefabPsdNaming.SafeFileName("中文 🧩") == "中文 🧩", "Chinese and a valid surrogate pair are preserved");
        Check(PrefabPsdNaming.SafeFileName("\ud800x\udc00") == "_x_", "Malformed Unicode pairs cannot break filenames");
        Check(PrefabPsdNaming.SafeFileName("") == "UI", "Empty file name has a fallback");
        Check(PrefabPsdNaming.SafeFileName("e\u0301") == "é", "Unicode normalization is deterministic");
        string longName = PrefabPsdNaming.SafeFileName(new string('x', 99) + "🧩");
        Check(longName.Length <= 100 && !char.IsHighSurrogate(longName[longName.Length - 1]), "Length limiting never splits a surrogate pair");
    }

    private static BatchSourceIdentity Identity(string name, string guid)
    {
        return new BatchSourceIdentity { SourceName = name, SourcePath = "Assets/" + guid + "/" + name + ".prefab", Guid = guid };
    }

    private static void TestPlans()
    {
        BatchSourceIdentity first = Identity("Main", "11111111aaaaaaaaaaaaaaaaaaaaaaaa");
        BatchSourceIdentity second = Identity("main", "11111111bbbbbbbbbbbbbbbbbbbbbbbb");
        BatchSourceIdentity third = Identity("Main_11111111", "33333333cccccccccccccccccccccccc");
        List<BatchSourceIdentity> identities = new List<BatchSourceIdentity> { first, second, third,
            Identity("../outside", "44444444dddddddddddddddddddddddd"),
            Identity("é", "55555555eeeeeeeeeeeeeeeeeeeeeeee"),
            Identity("e\u0301", "66666666ffffffffffffffffffffffff") };
        List<string> plan = PrefabPsdBatchService.PlanFileNames(identities);
        Check(plan.Count == identities.Count, "Every source receives exactly one output filename");
        HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in plan)
        {
            Check(unique.Add(file), "Batch filenames remain unique ignoring case: " + file);
            Check(Path.GetFileName(file) == file && file.EndsWith(".psd", StringComparison.Ordinal), "Plan contains filenames only: " + file);
        }
        Check(plan[0].Contains("11111111"), "Same-name assets include a stable GUID prefix");
        for (int i = 0; i < identities.Count; i++)
            Check(PrefabPsdBatchService.PlanFileNames(new[] { identities[i] })[0] == plan[i],
                "A source uses the same filename alone or alongside other selections");
        List<BatchSourceIdentity> reverse = new List<BatchSourceIdentity>(identities); reverse.Reverse();
        List<string> reversedPlan = PrefabPsdBatchService.PlanFileNames(reverse);
        for (int i = 0; i < plan.Count; i++)
            Check(plan[i] == reversedPlan[plan.Count - i - 1], "Source selection order does not alter stable output paths");
        List<string> repeat = PrefabPsdBatchService.PlanFileNames(identities);
        for (int i = 0; i < plan.Count; i++) Check(plan[i] == repeat[i], "Repeated planning is deterministic");
        List<string> duplicates = PrefabPsdBatchService.PlanFileNames(new[] { first, first, first });
        Check(new HashSet<string>(duplicates, StringComparer.OrdinalIgnoreCase).Count == 3, "Even duplicate input identities cannot overwrite one another in the pure plan");
        Check(PrefabPsdBatchService.PlanFileNames(new List<BatchSourceIdentity>()).Count == 0, "An empty pure plan is empty");
        Throws<ArgumentNullException>(delegate { PrefabPsdBatchService.PlanFileNames(null); }, "Null identity collection is rejected");
        Throws<ArgumentException>(delegate { PrefabPsdBatchService.PlanFileNames(new BatchSourceIdentity[] { null }); }, "Null identity entry is rejected");
    }

    private static void TestPresetsAndClone()
    {
        int callbacks = 0;
        ExportOptions source = new ExportOptions { Width = 320, Height = 180, MemoryBudgetMB = 768,
            Compression = PsdCompression.Raw, ReferencePsdPath = "/example/reference.psd", Preset = ExportPreset.SourceReference,
            PreserveEmptyGroups = true, AddCanvasMarker = true, VerifyOutput = true,
            PreserveReferenceNames = false, PreserveReferenceIds = false, ReuseIdenticalReferencePixels = false,
            CopySafeReferenceResources = false, AutoDetectReferencePsd = false,
            Progress = delegate { callbacks++; } };
        ExportOptions clone = source.Clone();
        Check(!object.ReferenceEquals(source, clone), "Per-item options are a separate snapshot object");
        Check(clone.Preset == source.Preset && clone.ReferencePsdPath == source.ReferencePsdPath, "Clone retains selected preset and exact reference path");
        Check(clone.Width == source.Width && clone.Height == source.Height && clone.MemoryBudgetMB == source.MemoryBudgetMB,
            "Clone retains canvas and memory settings");
        Check(clone.Compression == source.Compression && clone.PreserveEmptyGroups && clone.AddCanvasMarker && clone.VerifyOutput,
            "Clone retains compression, structure and verification settings");
        Check(!clone.PreserveReferenceNames && !clone.PreserveReferenceIds && !clone.ReuseIdenticalReferencePixels
            && !clone.CopySafeReferenceResources && !clone.AutoDetectReferencePsd, "Clone does not reset explicit reference toggles");
        clone.Progress(0, "x"); Check(callbacks == 1, "Clone retains cancellation/progress callback");
        clone.Width = 999; clone.ReferencePsdPath = "different";
        Check(source.Width == 320 && source.ReferencePsdPath == "/example/reference.psd", "Per-item changes do not mutate the shared window options");
        foreach (ExportPreset preset in Enum.GetValues(typeof(ExportPreset)))
        {
            ExportOptions options = source.Clone();
            PrefabPsdPresets.Apply(options, preset);
            Check(options.Preset == preset && options.WriteMetadata && options.VerifyOutput && options.PreserveHierarchy,
                "Preset selects explicit safe defaults: " + preset);
            Check(options.Width == source.Width && options.Height == source.Height && options.Compression == source.Compression
                && options.MemoryBudgetMB == source.MemoryBudgetMB, "Preset preserves user resolution/budget/compression: " + preset);
            Check(options.AddCanvasMarker == (preset == ExportPreset.RoundTripStructure), "Canvas marker is enabled only for structure preset");
            Check(preset == ExportPreset.SourceReference ? options.ReferencePsdPath == source.ReferencePsdPath : options.ReferencePsdPath == "",
                "Normal presets do not accidentally inherit an active reference path");
        }
        Throws<ArgumentNullException>(delegate { PrefabPsdPresets.Apply(null, ExportPreset.CurrentAppearance); }, "Null preset target is rejected");
        Throws<ArgumentOutOfRangeException>(delegate { PrefabPsdPresets.Apply(new ExportOptions(), (ExportPreset)999); }, "Unknown preset is rejected");
    }

    private static void TestArguments(string output)
    {
        string psd = Path.Combine(output, "single.psd");
        BatchRequest normal = PrefabPsdBatch.ParseArguments(new[] { "Unity", "-batchmode", "-prefabToPsdInput", "Assets/UI/Main.prefab",
            "-prefabToPsdOutput", psd });
        Check(normal.InputPaths.Count == 1 && normal.OutputFile == psd && normal.OutputDirectory == null, "Existing single-export CLI is retained");
        Check(normal.Options.Preset == ExportPreset.CurrentAppearance && normal.Options.VerifyOutput, "Default CLI is current appearance with verification");
        BatchRequest configured = PrefabPsdBatch.ParseArguments(new[] { "-prefabToPsdInput", "Assets\\UI\\Main.prefab",
            "-prefabToPsdOutputDirectory", output, "-prefabToPsdPreset", "SourceReference", "-prefabToPsdReference", "/reference/Main.psd",
            "-prefabToPsdWidth", "640", "-prefabToPsdHeight", "480", "-prefabToPsdMemoryMB", "768",
            "-prefabToPsdNoVerify", "-prefabToPsdFlat", "-prefabToPsdNoMetadata" });
        Check(configured.InputPaths[0] == "Assets/UI/Main.prefab", "Windows separators normalize in CLI input assets");
        Check(configured.Options.Preset == ExportPreset.SourceReference && configured.Options.ReferencePsdPath == "/reference/Main.psd",
            "CLI keeps explicit reference and source preset together");
        Check(configured.Options.Width == 640 && configured.Options.Height == 480 && configured.Options.MemoryBudgetMB == 768,
            "CLI keeps explicit resolution and budget after selecting a preset");
        Check(!configured.Options.VerifyOutput && !configured.Options.PreserveHierarchy && !configured.Options.WriteMetadata,
            "Explicit CLI switches override preset defaults");
        string list = Path.Combine(output, "inputs.txt");
        try
        {
            File.WriteAllText(list, "Assets/UI/First.prefab\n\n Assets/UI/Second.prefab \n", new UTF8Encoding(false));
            BatchRequest multiple = PrefabPsdBatch.ParseArguments(new[] { "-prefabToPsdInputsFile", list, "-prefabToPsdOutputDirectory", output });
            Check(multiple.InputPaths.Count == 2 && multiple.OutputDirectory == output, "CLI list-file batch accepts multiple trimmed assets");
            Throws<ArgumentException>(delegate { PrefabPsdBatch.ParseArguments(new[] { "-prefabToPsdInputsFile", list,
                "-prefabToPsdOutput", psd }); }, "Multiple input CLI cannot overwrite one output file");
        }
        finally { if (File.Exists(list)) File.Delete(list); }
        Throws<ArgumentException>(delegate { PrefabPsdBatch.ParseArguments(new[] { "-prefabToPsdInput", "Assets/UI/Main.prefab", "-prefabToPsdOutput", psd,
            "-prefabToPsdWidth", "640" }); }, "CLI requires width and height together");
        Throws<ArgumentException>(delegate { PrefabPsdBatch.ParseArguments(new[] { "-prefabToPsdInput", "Assets/UI/../Main.prefab", "-prefabToPsdOutput", psd }); }, "Asset path traversal is rejected");
        Throws<ArgumentException>(delegate { PrefabPsdBatch.ParseArguments(new[] { "-prefabToPsdInput", "Assets/Main.prefab", "-prefabToPsdOutput", "relative.psd" }); }, "Relative output path is rejected");
        Throws<ArgumentException>(delegate { PrefabPsdBatch.ParseArguments(new[] { "-prefabToPsdInput", "Assets/Main.prefab", "-prefabToPsdOutput", psd,
            "-prefabToPsdPreset", "999" }); }, "Undefined numeric preset is rejected");
        Throws<ArgumentException>(delegate { PrefabPsdBatch.ParseArguments(new[] { "-prefabToPsdInput", "Assets/Main.prefab", "-prefabToPsdOutput", psd,
            "-prefabToPsdMemoryMB", "8" }); }, "Memory budget outside limits is rejected");
        Throws<ArgumentException>(delegate { PrefabPsdBatch.ParseArguments(new[] { "-prefabToPsdInput", "Assets/Main.prefab", "-prefabToPsdInput", "Assets/Other.prefab",
            "-prefabToPsdOutput", psd }); }, "Duplicate value argument is rejected instead of silently selecting a different source");
        Throws<ArgumentException>(delegate { PrefabPsdBatch.ParseArguments(new[] { "-prefabToPsdInput", "Assets/Main.prefab", "-prefabToPsdOutput", psd,
            "-prefabToPsdOutputDirectory", output }); }, "Conflicting file/directory output flags are rejected");
        Throws<ArgumentException>(delegate { PrefabPsdBatch.ParseArguments(new[] { "-prefabToPsdInput", "-prefabToPsdOutput", psd }); }, "Missing argument value is rejected");
    }
}
