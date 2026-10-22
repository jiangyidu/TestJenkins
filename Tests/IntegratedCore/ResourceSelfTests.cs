using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PrefabToPsd.Core;

// Standalone resource tests, intentionally outside Assets and the legacy test project.
internal static class ResourceSelfTests
{
    private static int assertions;
    private static byte[] profile;
    private static string output;

    public static int Main(string[] args)
    {
        try
        {
            profile = File.ReadAllBytes(args[0]);
            output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(output);
            Check(PsdSafeResources.IsSrgbIccProfile(profile), "Real LittleCMS sRGB matrix/TRC profile is accepted");
            TestProfileRecognition();
            TestResourceValidation();
            TestOutputAndAtomicity();
            File.WriteAllBytes(Path.Combine(output, "expected-srgb.icc"), profile);
            Console.WriteLine("PASS: " + assertions + " resource assertions. No Unity native/GPU calls.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void TestProfileRecognition()
    {
        byte[] renamed = Clone(profile);
        int desc = TagData(profile, "desc");
        // Change the human-readable description while preserving all colorimetric data.
        for (int i = desc + 28; i + 1 < desc + TagLength(profile, "desc"); i += 2)
        { renamed[i] = 0; renamed[i + 1] = (byte)'X'; }
        Check(PsdSafeResources.IsSrgbIccProfile(renamed), "Profile description does not determine sRGB identity");

        byte[] adobeLike = Clone(profile);
        SetXyz(adobeLike, "rXYZ", 0.60974, 0.31111, 0.01947);
        SetXyz(adobeLike, "gXYZ", 0.20528, 0.62567, 0.06087);
        SetXyz(adobeLike, "bXYZ", 0.14919, 0.06322, 0.74457);
        Check(SliceEqual(profile, adobeLike, desc, TagLength(profile, "desc")), "Non-sRGB mutation retains the original sRGB description");
        Reject(adobeLike, "Adobe-RGB-like colorants cannot pass under an unchanged sRGB description");
        byte[] gamma = Clone(profile);
        int trc = TagData(profile, "rTRC");
        Fixed(gamma, trc + 12, 2.2); Fixed(gamma, trc + 16, 1.0);
        Fixed(gamma, trc + 20, 0.0); Fixed(gamma, trc + 24, 0.0); Fixed(gamma, trc + 28, 0.0);
        Reject(gamma, "Gamma 2.2 is not silently treated as the piecewise sRGB transfer");

        MutateReject("Wrong ICC magic", delegate(byte[] b) { Ascii(b, 36, "xxxx"); });
        MutateReject("Wrong RGB data space", delegate(byte[] b) { Ascii(b, 16, "CMYK"); });
        MutateReject("Wrong PCS", delegate(byte[] b) { Ascii(b, 20, "Lab "); });
        MutateReject("Unsupported profile version", delegate(byte[] b) { b[8] = 5; });
        MutateReject("Output/printer profile is not admitted", delegate(byte[] b) { Ascii(b, 12, "prtr"); });
        MutateReject("Declared size must equal actual bytes", delegate(byte[] b) { U32(b, 0, (uint)b.Length + 4); });
        MutateReject("Unknown rendering intent", delegate(byte[] b) { U32(b, 64, 4); });
        MutateReject("Header illuminant must be D50", delegate(byte[] b) { Fixed(b, 68, 0.8); });
        MutateReject("White point tag must match D50", delegate(byte[] b) { SetXyz(b, "wtpt", 0.9505, 1, 1.0890); });
        MutateReject("Duplicate tag signatures are rejected", delegate(byte[] b) { Buffer.BlockCopy(b, 132, b, 144, 4); });
        MutateReject("Unaligned tag offset is rejected", delegate(byte[] b) { int e = TagEntry(b, "rXYZ"); U32(b, e + 4, ReadU32(b, e + 4) + 1); });
        MutateReject("Tag beyond end of profile is rejected", delegate(byte[] b) { U32(b, TagEntry(b, "rXYZ") + 4, (uint)b.Length); });
        MutateReject("Tag offset into the directory is rejected", delegate(byte[] b) { U32(b, TagEntry(b, "rXYZ") + 4, 132); });
        MutateReject("Partially overlapping tag blocks are rejected", delegate(byte[] b) { U32(b, TagEntry(b, "rXYZ") + 4, (uint)(TagData(b, "bXYZ") + 4)); });
        MutateReject("Unbounded tag count is rejected", delegate(byte[] b) { U32(b, 128, 0xffffffff); });
        MutateReject("Missing required tags are rejected", delegate(byte[] b) { U32(b, 128, 6); });
        MutateReject("A2B lookup processing is rejected", delegate(byte[] b) { Ascii(b, 132, "A2B0"); });
        MutateReject("LUT payload cannot hide under an unrelated tag", delegate(byte[] b) { Ascii(b, TagData(b, "chad"), "mft1"); });
        MutateReject("Nonzero XYZ reserved word is rejected", delegate(byte[] b) { U32(b, TagData(b, "rXYZ") + 4, 1); });
        MutateReject("Unknown parametric curve function is rejected", delegate(byte[] b) { U16(b, trc + 8, 0); });
        MutateReject("Negative gamma is rejected", delegate(byte[] b) { Fixed(b, trc + 12, -1); });
        MutateReject("Zero parametric scale is rejected", delegate(byte[] b) { Fixed(b, trc + 16, 0); });

        foreach (int length in new[] { 0, 1, 25, 127, 128, 131, 132, 263, 519, profile.Length - 1 })
        {
            byte[] truncated = new byte[length]; Buffer.BlockCopy(profile, 0, truncated, 0, length);
            Reject(truncated, "Truncated ICC length " + length);
        }
        Reject(null, "Null profile is not accepted");
        Reject(new byte[PsdSafeResources.MaxIccBytes + 1], "Oversized profile is rejected before parsing");

        byte[] table = CurveProfile(257, false);
        Check(PsdSafeResources.IsSrgbIccProfile(table), "Canonical sampled sRGB TRC is supported");
        File.WriteAllBytes(Path.Combine(output, "sampled-srgb.icc"), table);
        Reject(CurveProfile(2, false), "Two endpoints do not prove an sRGB curve");
        Reject(CurveProfile(257, true), "Non-monotonic sampled TRC is rejected");
        byte[] function4 = ParametricFunction4();
        Check(PsdSafeResources.IsSrgbIccProfile(function4), "Canonical type-4 parametric sRGB TRC is supported");
    }

    private static void TestResourceValidation()
    {
        PsdSafeResources.Validate(new PsdResource { Id = 1005, Data = Resolution(300, 300) }); assertions++;
        PsdSafeResources.Validate(new PsdResource { Id = 1064, Data = Aspect(1) }); assertions++;
        PsdSafeResources.Validate(new PsdResource { Id = 1039, Data = profile }); assertions++;
        Throws(delegate { PsdSafeResources.Validate(null); }, "Null resource rejected");
        Throws(delegate { PsdSafeResources.Validate(new PsdResource { Id = 1039 }); }, "Null resource payload rejected");
        Throws(delegate { PsdSafeResources.Validate(new PsdResource { Id = 1057, Data = new byte[16] }); }, "Arbitrary resource IDs cannot be injected");
        Throws(delegate { PsdSafeResources.Validate(new PsdResource { Id = 1005, Data = new byte[15] }); }, "ResolutionInfo must have exact size");
        byte[] resolution = Resolution(300, 300); U32(resolution, 0, 0);
        Throws(delegate { PsdSafeResources.Validate(new PsdResource { Id = 1005, Data = resolution }); }, "Zero resolution rejected");
        resolution = Resolution(300, 300); U16(resolution, 4, 3);
        Throws(delegate { PsdSafeResources.Validate(new PsdResource { Id = 1005, Data = resolution }); }, "Unknown resolution unit rejected");
        resolution = Resolution(300, 300); U16(resolution, 6, 0);
        Throws(delegate { PsdSafeResources.Validate(new PsdResource { Id = 1005, Data = resolution }); }, "Unknown size unit rejected");
        foreach (double value in new[] { 0, -1, Double.NaN, Double.PositiveInfinity, 101 })
        {
            double captured = value;
            Throws(delegate { PsdSafeResources.Validate(new PsdResource { Id = 1064, Data = Aspect(captured) }); }, "Invalid pixel aspect " + value);
        }
        byte[] wrongVersion = Aspect(1); U32(wrongVersion, 0, 2);
        Throws(delegate { PsdSafeResources.Validate(new PsdResource { Id = 1064, Data = wrongVersion }); }, "Unknown pixel-aspect version rejected");
    }

    private static void TestOutputAndAtomicity()
    {
        foreach (PsdCompression compression in new[] { PsdCompression.Raw, PsdCompression.Rle })
        {
            PsdDocument doc = Document();
            doc.SafeResources.Add(new PsdResource { Id = 1005, Data = Resolution(300, 144) });
            doc.SafeResources.Add(new PsdResource { Id = 1039, Data = profile });
            doc.SafeResources.Add(new PsdResource { Id = 1064, Data = Aspect(1.25) });
            string path = Path.Combine(output, compression == PsdCompression.Raw ? "resources-raw.psd" : "resources-rle.psd");
            PsdWriter.Write(path, doc, compression);
            using (PsdReferenceDocument read = PsdReferenceReader.Open(path))
            {
                Check(read.Width == 2 && read.Height == 2 && read.Layers.Count == 1, "Resource fixture dimensions and layer count");
                Check(read.Resources.Count == 3, "Exactly the three safe resource IDs read back");
                foreach (PsdResource expected in doc.SafeResources)
                {
                    PsdResource actual = read.Resources.Find(delegate(PsdResource r) { return r.Id == expected.Id; });
                    Check(actual != null && Equal(actual.Data, expected.Data), "Resource payload exact for ID " + expected.Id + ", " + compression);
                }
                Check(Equal(read.ReadRgba(read.Layers[0]), doc.Layers[0].Rgba), "Pixel channels unchanged by safe resources, " + compression);
            }
        }
        string protectedPath = Path.Combine(output, "protected-output.bin");
        byte[] sentinel = { 14, 28, 42, 56 }; File.WriteAllBytes(protectedPath, sentinel);
        PsdDocument invalid = Document();
        byte[] invalidIcc = Clone(profile); SetXyz(invalidIcc, "rXYZ", 0.60974, 0.31111, 0.01947);
        invalid.SafeResources.Add(new PsdResource { Id = 1039, Data = invalidIcc });
        Throws(delegate { PsdWriter.Write(protectedPath, invalid); }, "Unsafe ICC is rejected before writing final output");
        Check(Equal(File.ReadAllBytes(protectedPath), sentinel), "Unsafe resource validation preserves previous output");
        Check(Directory.GetFiles(output, ".protected-output.bin.*.tmp").Length == 0, "No temporary residue after resource rejection");
        invalid = Document();
        invalid.SafeResources.Add(new PsdResource { Id = 1005, Data = Resolution(72, 72) });
        invalid.SafeResources.Add(new PsdResource { Id = 1005, Data = Resolution(300, 300) });
        Throws(delegate { PsdWriter.Write(protectedPath, invalid); }, "Duplicate safe resource IDs are rejected");
        Check(Equal(File.ReadAllBytes(protectedPath), sentinel), "Duplicate resource validation preserves previous output");
    }

    private static PsdDocument Document()
    {
        byte[] pixels = { 200, 80, 30, 128, 0, 220, 20, 255, 12, 34, 56, 0, 255, 255, 255, 64 };
        PsdDocument d = new PsdDocument { Width = 2, Height = 2, CompositeRgba = pixels };
        d.Layers.Add(new PsdLayer { Name = "资源与像素 🧩", Width = 2, Height = 2, Rgba = pixels, Id = 57 });
        return d;
    }
    private static byte[] CurveProfile(int count, bool descending)
    {
        int offset = (profile.Length + 3) & ~3;
        byte[] result = new byte[offset + 12 + count * 2]; Buffer.BlockCopy(profile, 0, result, 0, profile.Length);
        U32(result, 0, (uint)result.Length); Ascii(result, offset, "curv"); U32(result, offset + 8, (uint)count);
        for (int i = 0; i < count; i++)
        {
            double x = i / (count - 1.0), y = x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            U16(result, offset + 12 + i * 2, (int)Math.Floor(y * 65535 + 0.5));
        }
        if (descending) U16(result, offset + 12 + (count / 2) * 2, 0);
        PointTrcs(result, offset, 12 + count * 2); return result;
    }
    private static byte[] ParametricFunction4()
    {
        int offset = (profile.Length + 3) & ~3;
        byte[] result = new byte[offset + 40]; Buffer.BlockCopy(profile, 0, result, 0, profile.Length);
        U32(result, 0, (uint)result.Length); Ascii(result, offset, "para"); U16(result, offset + 8, 4);
        double[] p = { 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045, 0, 0 };
        for (int i = 0; i < p.Length; i++) Fixed(result, offset + 12 + i * 4, p[i]);
        PointTrcs(result, offset, 40); return result;
    }
    private static void PointTrcs(byte[] b, int offset, int size)
    {
        foreach (string name in new[] { "rTRC", "gTRC", "bTRC" })
        { int e = TagEntry(b, name); U32(b, e + 4, (uint)offset); U32(b, e + 8, (uint)size); }
    }
    private static byte[] Resolution(int horizontal, int vertical)
    {
        byte[] b = new byte[16]; U32(b, 0, (uint)(horizontal << 16)); U16(b, 4, 1); U16(b, 6, 1);
        U32(b, 8, (uint)(vertical << 16)); U16(b, 12, 1); U16(b, 14, 1); return b;
    }
    private static byte[] Aspect(double value)
    {
        byte[] b = new byte[12]; U32(b, 0, 1); byte[] number = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(number); Buffer.BlockCopy(number, 0, b, 4, 8); return b;
    }
    private static void SetXyz(byte[] b, string name, double x, double y, double z)
    { int p = TagData(b, name); Fixed(b, p + 8, x); Fixed(b, p + 12, y); Fixed(b, p + 16, z); }
    private static int TagEntry(byte[] b, string name)
    {
        for (int i = 0; i < (int)ReadU32(b, 128); i++)
        { int p = 132 + i * 12; if (Encoding.ASCII.GetString(b, p, 4) == name) return p; }
        throw new InvalidDataException("Fixture missing ICC tag " + name);
    }
    private static int TagData(byte[] b, string name) { return checked((int)ReadU32(b, TagEntry(b, name) + 4)); }
    private static int TagLength(byte[] b, string name) { return checked((int)ReadU32(b, TagEntry(b, name) + 8)); }
    private static uint ReadU32(byte[] b, int p) { return ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3]; }
    private static void U32(byte[] b, int p, uint v) { b[p] = (byte)(v >> 24); b[p + 1] = (byte)(v >> 16); b[p + 2] = (byte)(v >> 8); b[p + 3] = (byte)v; }
    private static void U16(byte[] b, int p, int v) { b[p] = (byte)(v >> 8); b[p + 1] = (byte)v; }
    private static void Fixed(byte[] b, int p, double v) { U32(b, p, unchecked((uint)(int)Math.Floor(v * 65536 + 0.5))); }
    private static void Ascii(byte[] b, int p, string s) { Buffer.BlockCopy(Encoding.ASCII.GetBytes(s), 0, b, p, 4); }
    private static byte[] Clone(byte[] b) { return (byte[])b.Clone(); }
    private static bool SliceEqual(byte[] a, byte[] b, int offset, int length)
    { for (int i = 0; i < length; i++) if (a[offset + i] != b[offset + i]) return false; return true; }
    private static bool Equal(byte[] a, byte[] b) { return a.Length == b.Length && SliceEqual(a, b, 0, a.Length); }
    private static void MutateReject(string name, Action<byte[]> mutation) { byte[] b = Clone(profile); mutation(b); Reject(b, name); }
    private static void Reject(byte[] b, string name) { Check(!PsdSafeResources.IsSrgbIccProfile(b), name); }
    private static void Throws(Action action, string name)
    {
        bool caught = false; try { action(); } catch (ArgumentException) { caught = true; }
        Check(caught, name);
    }
    private static void Check(bool ok, string name)
    { if (!ok) throw new Exception("FAIL " + name); assertions++; Console.WriteLine("PASS " + name); }
}
