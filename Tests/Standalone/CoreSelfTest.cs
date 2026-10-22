using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PrefabToPsd.Core;

/// <summary>Build with only Core/*.cs. No Unity, NUnit, Photoshop, or third-party C# library required.</summary>
public static class CoreSelfTest
{
    private static int assertions;

    public static int Main(string[] args)
    {
        try
        {
            string output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("fixtures");
            Directory.CreateDirectory(output);
            TestPackBits();
            TestRaster();
            TestMergedMatte();
            TestValidationAndAtomicWrite(output);
            MakeFixtures(output);
            Console.WriteLine("PASS: " + assertions + " core assertions. Fixtures: " + output);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception("ASSERT: " + message);
    }

    private static void Equal(byte[] a, byte[] b, string message)
    {
        Assert(a.Length == b.Length, message + " length");
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) throw new Exception(message + " byte " + i + ": " + a[i] + " != " + b[i]);
        assertions++;
    }

    private static void Throws(Action action, string message)
    {
        bool thrown = false;
        try { action(); }
        catch (ArgumentException) { thrown = true; }
        catch (InvalidDataException) { thrown = true; }
        catch (NotSupportedException) { thrown = true; }
        Assert(thrown, message);
    }

    private static byte[] DecodePackBits(byte[] bytes)
    {
        List<byte> decoded = new List<byte>();
        for (int i = 0; i < bytes.Length;)
        {
            int op = unchecked((sbyte)bytes[i++]);
            if (op >= 0)
            {
                for (int n = 0; n <= op; n++) decoded.Add(bytes[i++]);
            }
            else if (op != -128)
            {
                byte value = bytes[i++];
                for (int n = 0; n < 1 - op; n++) decoded.Add(value);
            }
        }
        return decoded.ToArray();
    }

    private static void TestPackBits()
    {
        int[] widths = { 0, 1, 2, 3, 4, 126, 127, 128, 129, 130, 255, 256, 257, 1024, 30000 };
        Random random = new Random(64073);
        foreach (int width in widths)
        {
            for (int mode = 0; mode < 5; mode++)
            {
                byte[] row = new byte[width];
                for (int i = 0; i < width; i++)
                    row[i] = mode == 0 ? (byte)42 : mode == 1 ? (byte)(i % 251) :
                        mode == 2 ? (byte)random.Next(256) : mode == 3 ? (byte)(i / 2 % 251) :
                        (byte)(i / 3 % 251);
                byte[] encoded = PsdWriter.PackBits(row);
                Equal(row, DecodePackBits(encoded), "PackBits width=" + width + ", mode=" + mode);
                Assert(encoded.Length <= width + (width + 127) / 128, "PackBits worst-case bound");
            }
        }
        Equal(new byte[] { 254, 17 }, PsdWriter.PackBits(new byte[] { 17, 17, 17 }), "3-byte repeat packet");
        Equal(new byte[] { 1, 17, 17 }, PsdWriter.PackBits(new byte[] { 17, 17 }), "2-byte literal packet");
    }

    private static void TestRaster()
    {
        byte[] rows = { 1, 2, 3, 4, 5, 6, 7, 8 };
        Equal(new byte[] { 5, 6, 7, 8, 1, 2, 3, 4 }, PsdRaster.FlipRows(rows, 1, 2), "Flip rows");
        Equal(rows, PsdRaster.FlipRows(PsdRaster.FlipRows(rows, 1, 2), 1, 2), "Double flip");
        byte[] rgba = new byte[5 * 4 * 4];
        rgba[(2 * 5 + 3) * 4 + 3] = 255;
        PsdLayer crop = PsdRaster.Crop("one", rgba, 5, 4);
        Assert(crop.Left == 3 && crop.Top == 2 && crop.Width == 1 && crop.Height == 1, "Crop geometry");
        Assert(crop.Rgba[3] == 255, "Crop alpha");
        crop = PsdRaster.Crop("empty", new byte[24], 3, 2);
        Assert(crop.Width == 1 && crop.Height == 1 && crop.Rgba[3] == 0, "Empty crop");
        rgba[3] = 1;
        crop = PsdRaster.Crop("threshold", rgba, 5, 4, 1);
        Assert(crop.Left == 3 && crop.Top == 2, "Crop threshold");

        PsdDocument doc = new PsdDocument { Width = 1, Height = 1 };
        doc.Layers.Add(Pixel("red", 0, 0, 1, 1, new byte[] { 255, 0, 0, 128 }));
        doc.Layers.Add(Pixel("blue", 0, 0, 1, 1, new byte[] { 0, 0, 255, 255 }));
        Equal(new byte[] { 128, 0, 127, 255 }, PsdRaster.CompositeLayers(doc), "Source-over order");
        doc.Layers.Insert(0, Group("invisible", false));
        doc.Layers.Insert(2, End());
        Equal(new byte[] { 0, 0, 255, 255 }, PsdRaster.CompositeLayers(doc), "Group visibility");
        doc.Layers.Clear();
        doc.Layers.Add(Pixel("partly outside", -1, -1, 2, 2,
            new byte[] { 1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255 }));
        Equal(new byte[] { 10, 11, 12, 255 }, PsdRaster.CompositeLayers(doc), "Canvas clipping");
        doc.Layers[0].Opacity = 128;
        Equal(new byte[] { 10, 11, 12, 128 }, PsdRaster.CompositeLayers(doc), "Layer opacity");
        doc.Layers[0].Left = Int32.MaxValue;
        Equal(new byte[4], PsdRaster.CompositeLayers(doc), "Extreme coordinate clipping without wraparound");
    }

    private static void TestValidationAndAtomicWrite(string output)
    {
        PsdDocument doc = OnePixel();
        PsdWriter.Validate(doc);
        Throws(delegate { PsdWriter.Validate(null); }, "Null document");
        Throws(delegate { PsdWriter.Validate(doc, (PsdCompression)99); }, "Bad compression");
        doc.Width = 30001;
        Throws(delegate { PsdWriter.Validate(doc); }, "PSD dimension limit");
        doc.Width = 1;
        doc.CompositeRgba = new byte[3];
        Throws(delegate { PsdWriter.Validate(doc); }, "Merged buffer length");
        doc.CompositeRgba = new byte[4];
        doc.Layers[0].Rgba = new byte[3];
        Throws(delegate { PsdWriter.Validate(doc); }, "Layer buffer length");
        doc.Layers[0].Rgba = new byte[4];
        doc.Layers[0].Left = Int32.MaxValue;
        Throws(delegate { PsdWriter.Validate(doc); }, "Coordinate overflow");
        doc.Layers[0].Left = 0;
        doc.Layers.Insert(0, End());
        Throws(delegate { PsdWriter.Validate(doc); }, "Early group end");
        doc.Layers.RemoveAt(0);
        doc.Layers.Insert(0, Group("open", true));
        Throws(delegate { PsdWriter.Validate(doc); }, "Unclosed group");
        doc.Layers.Add(End());
        doc.Layers[0].Opacity = 100;
        Throws(delegate { PsdWriter.Validate(doc); }, "Unbaked group opacity");
        doc.Layers[0].Opacity = 255;
        doc.Layers[0].Id = 3;
        doc.Layers[1].Id = 3;
        Throws(delegate { PsdWriter.Validate(doc); }, "Duplicate explicit ID");
        doc.Layers[1].Id = 0;
        doc.Layers[1].Name = "\ud800";
        Throws(delegate { PsdWriter.Validate(doc); }, "Malformed surrogate");
        doc.Layers[1].Name = "ok";
        string path = Path.Combine(output, "atomic.psd");
        File.WriteAllText(path, "previous file");
        doc.CompositeRgba = null;
        Throws(delegate { PsdWriter.Write(path, doc); }, "Invalid write refused");
        Assert(File.ReadAllText(path) == "previous file", "Failed write preserves destination");
        doc.CompositeRgba = new byte[4];
        PsdWriter.Write(path, doc);
        Assert(File.ReadAllBytes(path)[0] == (byte)'8', "Atomic replacement succeeds");
        File.Delete(path);
        Assert(Directory.GetFiles(output, "*.tmp").Length == 0, "Temporary files cleaned");

        using (MemoryStream stream = new MemoryStream())
        {
            stream.WriteByte(77);
            PsdWriter.Write(stream, OnePixel(), PsdCompression.Raw);
            Assert(stream.CanWrite, "Stream left open by default");
            byte[] bytes = stream.ToArray();
            Assert(bytes[0] == 77 && bytes[1] == (byte)'8', "Stream current position respected");
        }
        MemoryStream disposed = new MemoryStream();
        PsdWriter.Write(disposed, OnePixel(), PsdCompression.Raw, false);
        Assert(!disposed.CanWrite, "leaveOpen=false disposes stream");
    }

    private static void TestMergedMatte()
    {
        PsdDocument doc = OnePixel();
        doc.CompositeRgba = new byte[] { 255, 0, 0, 128 };
        doc.Layers[0].Rgba = (byte[])doc.CompositeRgba.Clone();
        using (MemoryStream stream = new MemoryStream())
        {
            PsdWriter.Write(stream, doc, PsdCompression.Raw);
            byte[] bytes = stream.ToArray();
            byte[] tail = new byte[6];
            Buffer.BlockCopy(bytes, bytes.Length - 6, tail, 0, 6);
            Equal(new byte[] { 0, 0, 255, 127, 127, 128 }, tail,
                "Merged RGB is white-matted; layer/API RGBA stays straight");
        }
    }

    private static PsdDocument OnePixel()
    {
        PsdDocument doc = new PsdDocument { Width = 1, Height = 1, CompositeRgba = new byte[4] };
        doc.Layers.Add(Pixel("transparent", 0, 0, 1, 1, new byte[4]));
        return doc;
    }

    private static PsdLayer Pixel(string name, int left, int top, int width, int height, byte[] rgba)
    {
        return new PsdLayer { Name = name, Left = left, Top = top, Width = width, Height = height, Rgba = rgba };
    }

    private static PsdLayer Group(string name, bool visible)
    {
        return new PsdLayer { Kind = PsdLayerKind.GroupStart, Name = name, Visible = visible };
    }

    private static PsdLayer End()
    {
        return new PsdLayer { Kind = PsdLayerKind.GroupEnd, Name = "</Layer group>" };
    }

    private static byte[] Solid(int width, int height, byte r, byte g, byte b, byte a)
    {
        byte[] data = new byte[width * height * 4];
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i] = r; data[i + 1] = g; data[i + 2] = b; data[i + 3] = a;
        }
        return data;
    }

    private static void MakeFixtures(string output)
    {
        PsdDocument doc = new PsdDocument { Width = 257, Height = 7 };
        doc.Layers.Add(Pixel("顶层🚀", 1, 1, 3, 2, new byte[] {
            255,0,0,255, 0,255,0,128, 0,0,255,0,
            7,8,9,64, 11,22,33,1, 40,50,60,254 }));
        doc.Layers.Add(Group("按钮组", true));
        doc.Layers.Add(Group("内层组", true));
        PsdLayer inner = Pixel("蓝色半透", 2, 0, 4, 3, Solid(4, 3, 20, 70, 210, 200));
        inner.Opacity = 173;
        doc.Layers.Add(inner);
        doc.Layers.Add(End());
        PsdLayer hidden = Pixel("隐藏层", 0, 0, 257, 7, Solid(257, 7, 0, 255, 0, 255));
        hidden.Visible = false;
        doc.Layers.Add(hidden);
        doc.Layers.Add(End());
        byte[] varied = new byte[257 * 7 * 4];
        for (int y = 0; y < 7; y++)
            for (int x = 0; x < 257; x++)
            {
                int i = (y * 257 + x) * 4;
                varied[i] = (byte)(x % 251);
                varied[i + 1] = (byte)(y * 30);
                varied[i + 2] = 88;
                varied[i + 3] = (byte)(x % 13 == 0 ? 0 : 180);
            }
        doc.Layers.Add(Pixel("背景", 0, 0, 257, 7, varied));
        doc.CompositeRgba = PsdRaster.CompositeLayers(doc);
        WriteFixture(output, "complex_raw", doc, PsdCompression.Raw);
        WriteFixture(output, "complex_rle", doc, PsdCompression.Rle);
        WriteFixture(output, "empty_rle", OnePixel(), PsdCompression.Rle);

        PsdDocument wide = new PsdDocument { Width = 30000, Height = 1 };
        byte[] widePixels = new byte[120000];
        for (int i = 0; i < widePixels.Length; i++) widePixels[i] = (byte)(i % 251);
        wide.Layers.Add(Pixel("maximum width", 0, 0, 30000, 1, widePixels));
        wide.CompositeRgba = (byte[])widePixels.Clone();
        WriteFixture(output, "maximum_width_rle", wide, PsdCompression.Rle);

        PsdDocument outside = new PsdDocument { Width = 2, Height = 2 };
        outside.Layers.Add(Pixel("off-canvas", -1, -2, 3, 4, Solid(3, 4, 100, 150, 200, 128)));
        outside.CompositeRgba = PsdRaster.CompositeLayers(outside);
        WriteFixture(output, "off_canvas_raw", outside, PsdCompression.Raw);
    }

    private static void WriteFixture(string directory, string name, PsdDocument document, PsdCompression compression)
    {
        PsdWriter.Write(Path.Combine(directory, name + ".psd"), document, compression);
        File.WriteAllBytes(Path.Combine(directory, name + ".merged.rgba"), document.CompositeRgba);
        List<string> manifest = new List<string>();
        manifest.Add(document.Width + "\t" + document.Height + "\t" + document.Layers.Count);
        for (int i = 0; i < document.Layers.Count; i++)
        {
            PsdLayer layer = document.Layers[i];
            manifest.Add(String.Join("\t", new string[] { i.ToString(), layer.Kind.ToString(),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(layer.Name)), layer.Left.ToString(),
                layer.Top.ToString(), layer.Width.ToString(), layer.Height.ToString(),
                layer.Opacity.ToString(), layer.Visible ? "1" : "0" }));
            if (layer.Kind == PsdLayerKind.Pixel)
                File.WriteAllBytes(Path.Combine(directory, name + ".layer" + i + ".rgba"), layer.Rgba);
        }
        File.WriteAllLines(Path.Combine(directory, name + ".manifest.tsv"), manifest.ToArray(), new UTF8Encoding(false));
    }
}
