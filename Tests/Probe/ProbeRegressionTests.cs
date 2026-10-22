using System;
using System.Collections;
using System.IO;
using System.Reflection;
using PrefabToPsd;
using PrefabToPsd.Core;
using UnityEngine.UI;

// This invokes the actual private reader used by the GPU self-tests. It does
// not copy that reader and does not call Unity native APIs or create a camera.
internal static class ProbeRegressionTests
{
    private const int Width = 96, Height = 64;
    private const string RedName = "红色半透明", BlueName = "Blue front";
    private static int assertions;
    private static readonly BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic;

    public static int Main(string[] args)
    {
        try
        {
            string output = args[0]; Directory.CreateDirectory(output);
            Type probeType = typeof(PrefabPsdSelfTests).GetNestedType("PsdProbe", BindingFlags.NonPublic);
            Check(probeType != null, "The real GPU self-test reader is located by reflection.");
            MethodInfo read = probeType.GetMethod("Read", Members | BindingFlags.Static);
            Check(read != null, "The real private PsdProbe.Read method is available.");
            PsdDocument document = Fixture();
            byte[] previousPixels = null;
            foreach (PsdCompression compression in new[] { PsdCompression.Raw, PsdCompression.Rle })
            {
                string path = Path.Combine(output, "probe-" + compression + ".psd");
                PsdWriter.Write(path, document, compression);
                object image = read.Invoke(null, new object[] { path });
                string prefix = compression + ": ";
                Check(Get<int>(image, "Width") == Width && Get<int>(image, "Height") == Height,
                    prefix + "Non-square 96x64 dimensions are preserved.");
                Check(Get<int>(image, "Channels") == 4, prefix + "Merged transparency channel is present.");
                Check(Get<IList>(image, "Layers").Count == 6,
                    prefix + "Four pixel records and two folder dividers are parsed.");
                IList names = Get<IList>(image, "LayerNames");
                Check(names.Contains("分组 🧩") && names.Contains(RedName),
                    prefix + "Unicode layer and folder names survive before merged image parsing.");
                byte[] pixels = Get<byte[]>(image, "Pixels");
                Pixel(pixels, 2, 2, 0, 0, 0, 0, prefix + "Transparent outside");
                Pixel(pixels, 10, 10, 255, 0, 0, 128, prefix + "Exact reported Red only sample");
                Pixel(pixels, 38, 25, 0, 0, 255, 128, prefix + "Blue only sample");
                Pixel(pixels, 24, 16, 85, 0, 170, 192, prefix + "Blue-over-red white-matte recovery");
                Pixel(pixels, 10, Height - 1 - 10, 0, 0, 0, 0, prefix + "Vertical mirror of red is transparent");
                Pixel(pixels, Width - 1 - 10, 10, 0, 0, 0, 0, prefix + "Horizontal mirror of red is transparent");
                Pixel(pixels, 90, 55, 31, 211, 73, 255, prefix + "Asymmetric lower-right marker");
                Pixel(pixels, 90, Height - 1 - 55, 0, 0, 0, 0,
                    prefix + "Marker cannot silently pass a vertical flip");
                int[] alpha = { 1, 2, 63, 127, 128, 192, 254, 255 };
                for (int index = 0; index < alpha.Length; index++)
                    Pixel(pixels, 3 + index, 60, index % 2 == 0 ? 255 : 0,
                        0, index % 2 == 0 ? 0 : 255, alpha[index],
                        prefix + "Primary color with alpha " + alpha[index]);
                Check(Equal(pixels, document.CompositeRgba),
                    prefix + "Every merged RGBA byte matches the independently specified fixture.");
                foreach (PsdLayer expected in document.Layers)
                {
                    if (expected.Kind != PsdLayerKind.Pixel) continue;
                    object layer = probeType.GetMethod("Layer", Members | BindingFlags.Instance)
                        .Invoke(image, new object[] { expected.Name });
                    Check(Get<int>(layer, "Left") == expected.Left && Get<int>(layer, "Top") == expected.Top
                        && Get<int>(layer, "Width") == expected.Width && Get<int>(layer, "Height") == expected.Height,
                        prefix + "Exact top-left bounds for " + expected.Name);
                    Check(Equal(Get<byte[]>(layer, "Pixels"), expected.Rgba),
                        prefix + "Straight layer RGBA remains exact for " + expected.Name);
                }
                if (previousPixels != null)
                    Check(Equal(pixels, previousPixels), "Raw and RLE yield identical merged bytes through the real private reader.");
                previousPixels = pixels;
            }
            int probeAssertions = assertions;
            TestCaptureControl();
            int controlAssertions = assertions - probeAssertions;
            TestCanvasReferencePpu();
            Console.WriteLine("Coverage: " + probeAssertions + " private-probe assertions; "
                + controlAssertions + " capture-control assertions; "
                + (assertions - probeAssertions - controlAssertions) + " canvas-ppu assertions.");
            Console.WriteLine("PASS: " + assertions + " real-probe regression assertions; no Unity GPU invoked.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception is TargetInvocationException && exception.InnerException != null
                ? exception.InnerException : exception);
            return 1;
        }
    }

    private static void TestCanvasReferencePpu()
    {
        float restored = PpuCase(100f, true, true, CanvasScaler.ScaleMode.ConstantPixelSize,
            200f, CanvasScaler.Unit.Points, 96f, 200f,
            "A persistent prefab's native default 100 cannot replace its configured reference PPU 200.");
        Check(Math.Abs(4f * restored / 100f - 8f) < 0.00001f,
            "The reported 4px sprite border at sprite PPU100 resolves to 8 canvas units at reference PPU200.");
        PpuCase(300f, false, false, CanvasScaler.ScaleMode.ConstantPhysicalSize,
            200f, CanvasScaler.Unit.Points, 96f, 300f,
            "A live scene's authoritative native PPU 300 is preserved.");
        PpuCase(275f, false, false, CanvasScaler.ScaleMode.ConstantPhysicalSize,
            96f, CanvasScaler.Unit.Points, 96f, 275f,
            "A disabled scaler contributes no physical-mode conversion.");
        PpuCase(325f, false, true, CanvasScaler.ScaleMode.ConstantPixelSize,
            200f, CanvasScaler.Unit.Inches, 96f, 325f,
            "A nested scaler that does not apply cannot override the native canvas reference.");
        PpuCase(100f, true, false, CanvasScaler.ScaleMode.ConstantPixelSize,
            200f, CanvasScaler.Unit.Points, 0f, 200f,
            "Screen ConstantPixelSize uses configured PPU and ignores the unused sprite DPI.");
        PpuCase(100f, true, false, CanvasScaler.ScaleMode.ScaleWithScreenSize,
            250f, CanvasScaler.Unit.Points, Single.NaN, 250f,
            "ScaleWithScreenSize preserves configured PPU instead of applying physical units.");
        PpuCase(100f, true, true, CanvasScaler.ScaleMode.ConstantPhysicalSize,
            200f, (CanvasScaler.Unit)999, Single.NaN, 200f,
            "WorldSpace keeps configured PPU even when physical-mode-only inputs are invalid.");
        PpuCase(100f, true, true, (CanvasScaler.ScaleMode)999,
            200f, (CanvasScaler.Unit)999, Single.NaN, 200f,
            "WorldSpace bypasses all screen-mode inputs, including an unused mode enum.");
        PpuCase(300f, false, false, (CanvasScaler.ScaleMode)999,
            Single.NaN, (CanvasScaler.Unit)999, Single.NaN, 300f,
            "An inapplicable scaler does not inspect any invalid scaler-only settings.");
        PpuCase(Single.NaN, true, false, CanvasScaler.ScaleMode.ConstantPixelSize,
            200f, CanvasScaler.Unit.Points, 0f, 200f,
            "An unused native placeholder does not prevent restoration of a valid persistent scaler setting.");
        PpuCase(Single.Epsilon, false, false, CanvasScaler.ScaleMode.ConstantPixelSize,
            200f, CanvasScaler.Unit.Points, 96f, Single.Epsilon,
            "A positive finite native subnormal value is preserved without imposing an arbitrary minimum.");

        CanvasScaler.Unit[] units = { CanvasScaler.Unit.Centimeters, CanvasScaler.Unit.Millimeters,
            CanvasScaler.Unit.Inches, CanvasScaler.Unit.Points, CanvasScaler.Unit.Picas };
        float[] expected = { 2.54f, 25.4f, 1f, 72f, 6f };
        for (int index = 0; index < units.Length; index++)
            PpuCase(100f, true, false, CanvasScaler.ScaleMode.ConstantPhysicalSize,
                96f, units[index], 96f, expected[index],
                "Screen physical " + units[index] + " uses the correct unit DPI rather than device DPI.");
        PpuCase(100f, true, false, CanvasScaler.ScaleMode.ConstantPhysicalSize,
            200f, CanvasScaler.Unit.Points, 100f, 144f,
            "Physical conversion respects both non-default reference PPU and default sprite DPI.");
        PpuCase(100f, true, false, CanvasScaler.ScaleMode.ConstantPhysicalSize,
            Single.MaxValue, CanvasScaler.Unit.Points, Single.MaxValue, 72f,
            "A finite physical result is retained when float-only intermediate multiplication would overflow.");

        float[] invalid = { 0f, -1f, Single.NaN, Single.PositiveInfinity, Single.NegativeInfinity };
        foreach (float value in invalid)
        {
            float bad = value;
            PpuThrows(delegate { PrefabPsdExporter.ResolveCanvasReferencePpu(bad, false, true,
                CanvasScaler.ScaleMode.ConstantPixelSize, 200f, CanvasScaler.Unit.Points, 96f); },
                "An invalid native PPU is rejected when the native value is authoritative: " + value);
            PpuThrows(delegate { PrefabPsdExporter.ResolveCanvasReferencePpu(100f, true, true,
                CanvasScaler.ScaleMode.ConstantPhysicalSize, bad, CanvasScaler.Unit.Points, 96f); },
                "An invalid active scaler reference PPU is rejected: " + value);
            PpuThrows(delegate { PrefabPsdExporter.ResolveCanvasReferencePpu(100f, true, false,
                CanvasScaler.ScaleMode.ConstantPhysicalSize, 200f, CanvasScaler.Unit.Points, bad); },
                "An invalid default sprite DPI is rejected when physical conversion uses it: " + value);
            PpuCase(300f, false, true, CanvasScaler.ScaleMode.ConstantPhysicalSize,
                value, (CanvasScaler.Unit)999, value, 300f,
                "Inactive scaler values are irrelevant to native scene PPU: " + value);
            PpuCase(100f, true, true, CanvasScaler.ScaleMode.ConstantPhysicalSize,
                200f, (CanvasScaler.Unit)999, value, 200f,
                "WorldSpace does not read the unused physical default sprite DPI: " + value);
        }
        PpuThrows(delegate { PrefabPsdExporter.ResolveCanvasReferencePpu(100f, true, false,
            CanvasScaler.ScaleMode.ConstantPhysicalSize, 200f, (CanvasScaler.Unit)999, 96f); },
            "An unknown physical unit is rejected when its conversion factor is needed.");
        PpuThrows(delegate { PrefabPsdExporter.ResolveCanvasReferencePpu(100f, true, false,
            (CanvasScaler.ScaleMode)999, 200f, CanvasScaler.Unit.Points, 96f); },
            "An unknown active screen scale mode is rejected instead of selecting a guessed conversion.");
        PpuThrows(delegate { PrefabPsdExporter.ResolveCanvasReferencePpu(100f, true, false,
            CanvasScaler.ScaleMode.ConstantPhysicalSize, Single.MaxValue, CanvasScaler.Unit.Points, 1f); },
            "A physical conversion result above the finite float range is rejected.");
        PpuThrows(delegate { PrefabPsdExporter.ResolveCanvasReferencePpu(100f, true, false,
            CanvasScaler.ScaleMode.ConstantPhysicalSize, Single.Epsilon, CanvasScaler.Unit.Inches, Single.MaxValue); },
            "A physical conversion that underflows to zero is rejected.");
    }

    private static float PpuCase(float nativePpu, bool scalerApplies, bool worldSpace,
        CanvasScaler.ScaleMode mode, float referencePpu, CanvasScaler.Unit unit, float spriteDpi,
        float expected, string message)
    {
        float actual = PrefabPsdExporter.ResolveCanvasReferencePpu(nativePpu, scalerApplies,
            worldSpace, mode, referencePpu, unit, spriteDpi);
        double tolerance = Math.Max((double)Single.Epsilon * 0.5, Math.Abs((double)expected) * 0.000001);
        Check(actual > 0f && !Single.IsNaN(actual) && !Single.IsInfinity(actual)
            && Math.Abs((double)actual - expected) <= tolerance,
            message + " Expected " + expected + ", got " + actual + ".");
        return actual;
    }

    private static void PpuThrows(Action action, string message)
    {
        bool rejected = false;
        try { action(); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, message);
    }

    private static void TestCaptureControl()
    {
        byte[] valid = ControlImage(Width, Height, 255, 0, 0, 128);
        byte[] before = (byte[])valid.Clone();
        ControlCase(valid, Width, Height, true, "Full-canvas half-transparent red control passes.");
        Check(Equal(valid, before), "Successful calibration validation does not mutate image bytes.");
        ControlCase(ControlImage(1, 1, 255, 0, 0, 128), 1, 1, true,
            "A 1x1 capture is valid even though the nine control samples coincide.");
        ControlCase(ControlImage(2, 3, 255, 0, 0, 128), 2, 3, true,
            "Small non-square dimensions are sampled safely.");
        ControlCase(new byte[Width * Height * 4], Width, Height, false,
            "An absent all-transparent UI control cannot pass calibration.");
        ControlCase(ControlImage(Width, Height, 255, 0, 0, 255), Width, Height, false,
            "An opaque capture cannot be mistaken for the expected half transparency.");
        ControlCase(ControlImage(Width, Height, 255, 0, 0, 191), Width, Height, false,
            "Double emission of half-transparent geometry is rejected.");
        ControlCase(ControlImage(Width, Height, 0, 0, 255, 128), Width, Height, false,
            "A swapped red/blue channel or wrong UI color is rejected.");
        ControlCase(ControlImage(Width, Height, 128, 0, 0, 128), Width, Height, false,
            "Premultiplied RGB cannot masquerade as the straight-red capture control.");

        int[] xs = { 0, Width / 2, Width - 1 };
        int[] ys = { 0, Height / 2, Height - 1 };
        foreach (int y in ys)
            foreach (int x in xs)
            {
                byte[] clipped = (byte[])valid.Clone();
                Set(clipped, Width, x, y, 0, 0, 0, 0);
                byte[] clippedBefore = (byte[])clipped.Clone();
                string detail = ControlCase(clipped, Width, Height, false,
                    "Clipped control sample at (" + x + "," + y + ") is rejected.");
                Check(detail.Contains("(" + x + "," + y + ")") && detail.Contains("got (0,0,0,0)"),
                    "The failed sample reports its actual coordinates and RGBA.");
                Check(Equal(clipped, clippedBefore), "Failed calibration validation does not mutate image bytes.");
            }

        ControlCase(null, Width, Height, false, "A null image fails safely.");
        ControlCase(new byte[0], 0, Height, false, "Zero width fails safely.");
        ControlCase(new byte[0], Width, 0, false, "Zero height fails safely.");
        ControlCase(new byte[0], -1, Height, false, "Negative width fails safely.");
        ControlCase(new byte[0], Width, -1, false, "Negative height fails safely.");
        ControlCase(valid, Width, Height - 1, false, "A mismatched row count fails before sampling.");
        ControlCase(new byte[3], 1, 1, false, "A truncated RGBA pixel fails before sampling.");
        ControlCase(new byte[4], Int32.MaxValue, Int32.MaxValue, false,
            "Huge dimension products cannot index a small supplied buffer.");

        ControlCase(ControlImage(Width, Height, 251, 4, 4, 124), Width, Height, true,
            "Inclusive lower alpha/red and upper green/blue tolerance boundaries pass.");
        ControlCase(ControlImage(Width, Height, 255, 0, 0, 132), Width, Height, true,
            "Inclusive upper alpha tolerance boundary passes.");
        ControlCase(ControlImage(Width, Height, 250, 0, 0, 128), Width, Height, false,
            "Red just outside the tolerance is rejected.");
        ControlCase(ControlImage(Width, Height, 255, 5, 0, 128), Width, Height, false,
            "Green just outside the tolerance is rejected.");
        ControlCase(ControlImage(Width, Height, 255, 0, 5, 128), Width, Height, false,
            "Blue just outside the tolerance is rejected.");
        ControlCase(ControlImage(Width, Height, 255, 0, 0, 123), Width, Height, false,
            "Alpha just below the tolerance is rejected.");
        ControlCase(ControlImage(Width, Height, 255, 0, 0, 133), Width, Height, false,
            "Alpha just above the tolerance is rejected.");
    }

    private static byte[] ControlImage(int width, int height, int red, int green, int blue, int alpha)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) Set(pixels, width, x, y, red, green, blue, alpha);
        return pixels;
    }

    private static string ControlCase(byte[] pixels, int width, int height, bool expected, string message)
    {
        string detail;
        bool actual = PrefabPsdExporter.CheckCaptureControl(pixels, width, height, out detail);
        Check(actual == expected && !String.IsNullOrEmpty(detail), message + " " + detail);
        return detail;
    }

    private static PsdDocument Fixture()
    {
        PsdDocument document = new PsdDocument { Width = Width, Height = Height,
            CompositeRgba = new byte[Width * Height * 4] };
        PsdLayer red = Solid(RedName, 1, 8, 8, 24, 16, 255, 0, 0, 128);
        PsdLayer blue = Solid(BlueName, 2, 20, 12, 20, 20, 0, 0, 255, 128);
        PsdLayer marker = Solid("Lower-right marker", 3, 89, 53, 3, 5, 31, 211, 73, 255);
        PsdLayer strip = Solid("Alpha edge values", 4, 3, 60, 8, 1, 0, 0, 0, 0);
        int[] alphas = { 1, 2, 63, 127, 128, 192, 254, 255 };
        for (int index = 0; index < alphas.Length; index++)
        {
            strip.Rgba[index * 4] = (byte)(index % 2 == 0 ? 255 : 0);
            strip.Rgba[index * 4 + 2] = (byte)(index % 2 == 0 ? 0 : 255);
            strip.Rgba[index * 4 + 3] = (byte)alphas[index];
        }
        // Panel order is top-first. The production compositor is intentionally
        // not used: fixed expected red/blue overlap bytes are specified below.
        document.Layers.Add(marker); document.Layers.Add(strip);
        document.Layers.Add(new PsdLayer { Name = "分组 🧩", Id = 5, Kind = PsdLayerKind.GroupStart });
        document.Layers.Add(blue); document.Layers.Add(red);
        document.Layers.Add(new PsdLayer { Name = "</分组>", Id = 6, Kind = PsdLayerKind.GroupEnd });
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                bool inRed = x >= 8 && x < 32 && y >= 8 && y < 24;
                bool inBlue = x >= 20 && x < 40 && y >= 12 && y < 32;
                if (inRed && inBlue) Set(document.CompositeRgba, Width, x, y, 85, 0, 170, 192);
                else if (inRed) Set(document.CompositeRgba, Width, x, y, 255, 0, 0, 128);
                else if (inBlue) Set(document.CompositeRgba, Width, x, y, 0, 0, 255, 128);
                if (x >= 89 && x < 92 && y >= 53 && y < 58)
                    Set(document.CompositeRgba, Width, x, y, 31, 211, 73, 255);
            }
        Buffer.BlockCopy(strip.Rgba, 0, document.CompositeRgba, (60 * Width + 3) * 4, strip.Rgba.Length);
        return document;
    }

    private static PsdLayer Solid(string name, int id, int left, int top, int width, int height,
        int red, int green, int blue, int alpha)
    {
        PsdLayer layer = new PsdLayer { Name = name, Id = id, Left = left, Top = top,
            Width = width, Height = height, Rgba = new byte[width * height * 4] };
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) Set(layer.Rgba, width, x, y, red, green, blue, alpha);
        return layer;
    }

    private static void Set(byte[] pixels, int width, int x, int y, int red, int green, int blue, int alpha)
    {
        int offset = (y * width + x) * 4;
        pixels[offset] = (byte)red; pixels[offset + 1] = (byte)green;
        pixels[offset + 2] = (byte)blue; pixels[offset + 3] = (byte)alpha;
    }

    private static void Pixel(byte[] pixels, int x, int y, int red, int green, int blue, int alpha, string message)
    {
        int offset = (y * Width + x) * 4;
        Check(pixels[offset] == red && pixels[offset + 1] == green && pixels[offset + 2] == blue
            && pixels[offset + 3] == alpha,
            message + " expected RGBA(" + red + "," + green + "," + blue + "," + alpha
            + ") got(" + pixels[offset] + "," + pixels[offset + 1] + "," + pixels[offset + 2]
            + "," + pixels[offset + 3] + ") at(" + x + "," + y + ").");
    }

    private static T Get<T>(object instance, string name)
    { return (T)instance.GetType().GetField(name, Members | BindingFlags.Instance).GetValue(instance); }
    private static bool Equal(byte[] a, byte[] b)
    { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); assertions++; Console.WriteLine("PASS " + message); }
}
