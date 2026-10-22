using System;
using System.Collections.Generic;
using System.IO;
using PrefabToPsd;
using PrefabToPsd.Core;

internal static class ReferenceSelfTest
{
    private static int assertions;
    private static string output;
    private static byte[] red = { 200, 80, 30, 255 };
    private static byte[] green = { 0, 180, 70, 255 };

    public static int Main(string[] args)
    {
        try
        {
            output = args[0]; Directory.CreateDirectory(output);
            TestExactAndModified(); TestRenamesAndUnsafeNames(); TestOrderAndIds();
            TestAmbiguity(); TestPathProtection(); TestResources(args[1]);
            Console.WriteLine("PASS: " + assertions + " reference-policy assertions; no Unity GPU invoked.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void TestExactAndModified()
    {
        string reference = SaveReference("exact.psd", Pixel("one", 42, 1, red));
        PsdDocument doc = Document(Pixel("one", 1, 1, red));
        PrefabPsdMetadataDocument metadata = Metadata(doc);
        ExportReport report = Report("exact-output.psd");
        PrefabPsdReference.Apply(doc, metadata, Options(), report, reference);
        Check(report.ReferenceMatchedLayers == 1 && report.ReferenceReusedLayers == 1, "Exact pixels are reusable.");
        Check(doc.Layers[0].Id == 42 && metadata.layers[0].psdLayerId == 42, "Reference ID and sidecar stay aligned.");
        Check(metadata.layers[0].referencePixelsReused && metadata.referenceReusedLayers == 1,
            "Reference provenance is recorded in the sidecar.");
        Check(metadata.schemaVersion == "2.0" && metadata.exporterVersion == ExportOptions.Version,
            "Sidecar identifies both schema and exporter version.");

        byte[] edited = (byte[])red.Clone(); edited[0]--;
        doc = Document(Pixel("one", 1, 1, edited)); metadata = Metadata(doc); report = Report("changed-output.psd");
        PrefabPsdReference.Apply(doc, metadata, Options(), report, reference);
        Check(report.ReferenceMatchedLayers == 1 && report.ReferenceReusedLayers == 0, "One changed byte prevents pixel reuse.");
        Check(doc.Layers[0].Rgba[0] == 199, "A real current pixel edit is not restored to the original.");

        doc = Document(Pixel("one", 1, 2, red)); metadata = Metadata(doc);
        metadata.nodes[0].originalPsd = new PrefabPsdOriginalMetadata { sourceLayerId = 42, sourceLayerName = "one" };
        report = Report("moved-output.psd");
        PrefabPsdReference.Apply(doc, metadata, Options(), report, reference);
        Check(report.ReferenceReusedLayers == 0 && doc.Layers[0].Left == 2, "Moving a layer preserves current bounds and forbids reuse.");

        doc = Document(Pixel("one", 1, 1, red)); metadata = Metadata(doc); report = Report("no-reuse-output.psd");
        ExportOptions options = Options(); options.ReuseIdenticalReferencePixels = false;
        PrefabPsdReference.Apply(doc, metadata, options, report, reference);
        Check(report.ReferenceMatchedLayers == 1 && report.ReferenceReusedLayers == 0,
            "Disabling reuse is honored while identity matching remains available.");
    }

    private static void TestRenamesAndUnsafeNames()
    {
        string reference = SaveReference("rename.psd", Pixel("original.img", 42, 1, red));
        PsdDocument doc = Document(Pixel("user renamed", 1, 1, red));
        PrefabPsdMetadataDocument metadata = Metadata(doc);
        metadata.nodes[0].originalPsd = new PrefabPsdOriginalMetadata { sourceLayerId = 42, sourceLayerName = "original.img" };
        ExportReport report = Report("renamed-output.psd");
        PrefabPsdReference.Apply(doc, metadata, Options(), report, reference);
        Check(doc.Layers[0].Name == "user renamed" && report.ReferenceReusedLayers == 1,
            "A user rename remains authoritative even when pixels are identical.");

        doc = Document(Pixel("artist-clean", 1, 1, red)); metadata = Metadata(doc);
        metadata.nodes[0].name = "original.img";
        metadata.nodes[0].originalPsd = new PrefabPsdOriginalMetadata { sourceLayerId = 42, sourceLayerName = "original.img" };
        PrefabPsdReference.Apply(doc, metadata, Options(), Report("restore-name-output.psd"), reference);
        Check(doc.Layers[0].Name == "original.img", "An unchanged source name can be restored after preset naming.");

        reference = SaveReference("unsafe-name.psd", Pixel("Title.txt@slice=4", 51, 1, red));
        doc = Document(Pixel("Title.img", 1, 1, red)); metadata = Metadata(doc);
        metadata.nodes[0].name = "Title.txt@slice=4";
        metadata.nodes[0].originalPsd = new PrefabPsdOriginalMetadata { sourceLayerId = 51, sourceLayerName = "Title.txt@slice=4" };
        report = Report("unsafe-name-output.psd");
        PrefabPsdReference.Apply(doc, metadata, Options(), report, reference);
        Check(doc.Layers[0].Name == "Title.img", "Rasterized text never regains unsafe text/slice importer semantics.");
        Check(metadata.layers[0].referenceName == "Title.txt@slice=4" && report.Warnings.Count > 0,
            "Unsafe original names are retained in JSON with a warning.");
        Check(!PrefabPsdReference.IsSafeRasterName("#skip") && !PrefabPsdReference.IsSafeRasterName("x.col")
            && !PrefabPsdReference.IsSafeRasterName("caption.label") && !PrefabPsdReference.IsSafeRasterName("box.viewport")
            && PrefabPsdReference.IsSafeRasterName("dialog_bg") && PrefabPsdReference.IsSafeRasterName("image.img"),
            "Raster-name safety preserves ordinary business names.");
    }

    private static void TestOrderAndIds()
    {
        string reference = SaveReference("order.psd", Pixel("one", 42, 1, red), Pixel("two", 43, 4, green));
        PsdDocument doc = Document(Pixel("two", 1, 4, green), Pixel("one", 2, 1, red));
        PrefabPsdMetadataDocument metadata = Metadata(doc); ExportReport report = Report("order-output.psd");
        PrefabPsdReference.Apply(doc, metadata, Options(), report, reference);
        Check(doc.Layers[0].Name == "two" && doc.Layers[1].Name == "one", "Current panel order is never restored to reference order.");
        Check(doc.Layers[0].Id == 43 && doc.Layers[1].Id == 42, "Both unique source identities are restored.");

        doc = Document(new PsdLayer { Name = "folder", Kind = PsdLayerKind.GroupStart, Id = 42 },
            Pixel("one", 1, 1, red), new PsdLayer { Name = "end", Kind = PsdLayerKind.GroupEnd, Id = 9 });
        metadata = Metadata(doc); report = Report("id-collision-output.psd");
        PrefabPsdReference.Apply(doc, metadata, Options(), report, reference);
        HashSet<int> ids = new HashSet<int>(); foreach (PsdLayer layer in doc.Layers) Check(ids.Add(layer.Id), "Every final PSD record has a unique ID.");
        Check(doc.Layers[1].Id == 42 && doc.Layers[0].Id != 42,
            "Restored pixel identity takes precedence over a colliding synthetic folder ID.");
        PsdWriter.Write(report.PsdPath, doc);
        using (PsdReferenceDocument reread = PsdReferenceReader.Open(report.PsdPath))
            Check(reread.Layers[1].Id == metadata.layers[0].psdLayerId, "Final PSD and JSON ID remain aligned after a real write/read.");
    }

    private static void TestAmbiguity()
    {
        string reference = SaveReference("ambiguous.psd", Pixel("same", 10, 1, red), Pixel("same", 11, 1, red));
        PsdDocument doc = Document(Pixel("same", 1, 1, red));
        ExportReport report = Report("ambiguous-output.psd");
        PrefabPsdReference.Apply(doc, Metadata(doc), Options(), report, reference);
        Check(report.ReferenceMatchedLayers == 0 && report.ReferenceReusedLayers == 0,
            "Ambiguous same-name/same-bounds candidates are never guessed.");

        reference = SaveReference("duplicate-source-id.psd", Pixel("one", 42, 1, red));
        doc = Document(Pixel("one", 1, 1, red), Pixel("clone", 2, 7, green));
        PrefabPsdMetadataDocument metadata = Metadata(doc);
        foreach (PrefabPsdNodeMetadata node in metadata.nodes)
            node.originalPsd = new PrefabPsdOriginalMetadata { sourceLayerId = 42, sourceLayerName = "one" };
        report = Report("duplicate-source-output.psd");
        PrefabPsdReference.Apply(doc, metadata, Options(), report, reference);
        Check(report.ReferenceMatchedLayers == 1 && report.ReferenceReusedLayers == 1,
            "Cloned source IDs do not cause reference identity or pixels to be assigned twice.");
        Check(doc.Layers[1].Id != 42 && doc.Layers[1].Rgba[1] == 180, "A cloned edited layer keeps its own pixels and unique identity.");
    }

    private static void TestPathProtection()
    {
        string reference = SaveReference("path.psd", Pixel("one", 42, 1, red));
        ExportOptions options = Options(); options.ReferencePsdPath = reference;
        Check(PrefabPsdReference.ResolvePath(null, options, new List<string>()) == Path.GetFullPath(reference),
            "An explicit reference is resolved without scanning the project.");
        options.ReferencePsdPath = ""; options.Preset = ExportPreset.CurrentAppearance;
        Check(PrefabPsdReference.ResolvePath(null, options, new List<string>()) == null,
            "Ordinary appearance export never auto-enables reference assistance.");
        options.Preset = ExportPreset.SourceReference; options.AutoDetectReferencePsd = false;
        Throws<FileNotFoundException>(delegate { PrefabPsdReference.ResolvePath(null, options, new List<string>()); },
            "Missing required reference fails before an output is written.");
        options.ReferencePsdPath = Path.Combine(output, "missing.psd");
        Throws<FileNotFoundException>(delegate { PrefabPsdReference.ResolvePath(null, options, new List<string>()); },
            "A missing explicit reference is not silently replaced with another file.");
        byte[] before = File.ReadAllBytes(reference);
        PsdDocument doc = Document(Pixel("one", 1, 1, red));
        Throws<InvalidOperationException>(delegate {
            PrefabPsdReference.Apply(doc, Metadata(doc), Options(), new ExportReport { PsdPath = reference }, reference);
        }, "The read-only reference cannot also be the output destination.");
        Check(Equal(before, File.ReadAllBytes(reference)), "Reference bytes survive a rejected same-path export.");
    }

    private static void TestResources(string profilePath)
    {
        byte[] profile = File.ReadAllBytes(profilePath);
        Check(PsdSafeResources.IsSrgbIccProfile(profile), "Fixture is a canonical sRGB profile.");
        PsdDocument source = Document(Pixel("one", 42, 1, red));
        source.SafeResources.Add(new PsdResource { Id = 1039, Data = profile });
        string reference = Path.Combine(output, "profile.psd"); PsdWriter.Write(reference, source);
        PsdDocument doc = Document(Pixel("one", 1, 1, red)); ExportReport report = Report("profile-output.psd");
        PrefabPsdMetadataDocument metadata = Metadata(doc);
        PrefabPsdReference.Apply(doc, metadata, Options(), report, reference);
        Check(report.ReferenceColorProfileCopied && metadata.referenceColorProfileCopied,
            "Verified sRGB is copied with matching report and JSON provenance.");
        bool found = false; foreach (PsdResource resource in doc.SafeResources)
            if (resource.Id == 1039) found = Equal(profile, resource.Data);
        Check(found, "ICC resource bytes are copied exactly.");

        byte[] foreign = (byte[])profile.Clone();
        int count = (int)U32(foreign, 128);
        bool changed = false;
        for (int i = 0; i < count; i++)
        {
            int e = 132 + i * 12;
            if (foreign[e] == (byte)'r' && foreign[e + 1] == (byte)'X' && foreign[e + 2] == (byte)'Y' && foreign[e + 3] == (byte)'Z')
            {
                int offset = checked((int)U32(foreign, e + 4)); Write32(foreign, offset + 8, 39322); changed = true;
            }
        }
        Check(changed && !PsdSafeResources.IsSrgbIccProfile(foreign), "A changed RGB colorant is rejected even with an sRGB description.");
        byte[] file = File.ReadAllBytes(reference); int start = Find(file, profile);
        Check(start >= 0, "The reference fixture contains its declared profile payload.");
        Buffer.BlockCopy(foreign, 0, file, start, foreign.Length);
        string unsafeReference = Path.Combine(output, "foreign-profile.psd"); File.WriteAllBytes(unsafeReference, file);
        doc = Document(Pixel("one", 1, 1, red)); report = Report("foreign-output.psd");
        PrefabPsdReference.Apply(doc, Metadata(doc), Options(), report, unsafeReference);
        bool copiedForeign = false; foreach (PsdResource resource in doc.SafeResources) if (resource.Id == 1039) copiedForeign = true;
        Check(!report.ReferenceColorProfileCopied && !copiedForeign && report.Warnings.Count > 0,
            "Foreign RGB profiles cannot reinterpret current Unity pixels.");

        source = Document(Pixel("one", 42, 1, red));
        byte[] aspect = new byte[12]; aspect[3] = 1;
        byte[] aspectNumber = BitConverter.GetBytes(2.0); if (BitConverter.IsLittleEndian) Array.Reverse(aspectNumber);
        Buffer.BlockCopy(aspectNumber, 0, aspect, 4, 8);
        source.SafeResources.Add(new PsdResource { Id = 1064, Data = aspect });
        reference = Path.Combine(output, "non-square-pixels.psd"); PsdWriter.Write(reference, source);
        doc = Document(Pixel("one", 1, 1, red)); report = Report("non-square-output.psd");
        PrefabPsdReference.Apply(doc, Metadata(doc), Options(), report, reference);
        bool aspectCopied = false; foreach (PsdResource resource in doc.SafeResources) if (resource.Id == 1064) aspectCopied = true;
        Check(!aspectCopied && report.Warnings.Exists(delegate(string warning) { return warning.Contains("square pixels"); }),
            "A non-square original pixel aspect cannot distort the current square-pixel Unity canvas.");

        aspectNumber = BitConverter.GetBytes(1.0); if (BitConverter.IsLittleEndian) Array.Reverse(aspectNumber);
        Buffer.BlockCopy(aspectNumber, 0, aspect, 4, 8);
        reference = Path.Combine(output, "square-pixels.psd"); PsdWriter.Write(reference, source);
        doc = Document(Pixel("one", 1, 1, red)); report = Report("square-output.psd");
        PrefabPsdReference.Apply(doc, Metadata(doc), Options(), report, reference);
        aspectCopied = false; foreach (PsdResource resource in doc.SafeResources) if (resource.Id == 1064) aspectCopied = true;
        Check(aspectCopied, "A verified square-pixel aspect can be inherited safely.");
    }

    private static PsdLayer Pixel(string name, int id, int left, byte[] rgba)
    { return new PsdLayer { Name = name, Id = id, Left = left, Top = 2, Width = 1, Height = 1, Rgba = (byte[])rgba.Clone() }; }
    private static PsdDocument Document(params PsdLayer[] layers)
    {
        PsdDocument doc = new PsdDocument { Width = 16, Height = 8 }; doc.Layers.AddRange(layers);
        doc.CompositeRgba = PsdRaster.CompositeLayers(doc); return doc;
    }
    private static string SaveReference(string name, params PsdLayer[] layers)
    { string path = Path.Combine(output, name); PsdWriter.Write(path, Document(layers)); return path; }
    private static PrefabPsdMetadataDocument Metadata(PsdDocument doc)
    {
        PrefabPsdMetadataDocument metadata = new PrefabPsdMetadataDocument();
        foreach (PsdLayer layer in doc.Layers)
        {
            if (layer.Kind != PsdLayerKind.Pixel) continue;
            string key = layer.Name + "[" + layer.Id + "]";
            metadata.layers.Add(new PsdLayerMetadata { psdLayerId = layer.Id, hierarchyPath = key, name = layer.Name });
            metadata.nodes.Add(new PrefabPsdNodeMetadata { name = layer.Name, hierarchyPath = key });
        }
        return metadata;
    }
    private static ExportOptions Options() { return new ExportOptions { Preset = ExportPreset.SourceReference }; }
    private static ExportReport Report(string file) { return new ExportReport { PsdPath = Path.Combine(output, file) }; }
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); assertions++; Console.WriteLine("PASS " + message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    { bool caught = false; try { action(); } catch (T) { caught = true; } Check(caught, message); }
    private static bool Equal(byte[] a, byte[] b)
    { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    private static uint U32(byte[] data, int offset)
    { return (uint)data[offset] << 24 | (uint)data[offset + 1] << 16 | (uint)data[offset + 2] << 8 | data[offset + 3]; }
    private static void Write32(byte[] data, int offset, uint value)
    { data[offset] = (byte)(value >> 24); data[offset + 1] = (byte)(value >> 16); data[offset + 2] = (byte)(value >> 8); data[offset + 3] = (byte)value; }
    private static int Find(byte[] source, byte[] value)
    {
        for (int i = 0; i <= source.Length - value.Length; i++)
        { int p = 0; while (p < value.Length && source[i + p] == value[p]) p++; if (p == value.Length) return i; }
        return -1;
    }
}
