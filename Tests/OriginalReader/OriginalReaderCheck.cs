// Optional compatibility probe for the separately supplied PsdTOUgui sources.
// Run with Mono: OriginalReaderCheck.exe /path/to/Original.dll /path/to/fixtures
// Uses the original parser/decoder without changing their source. It does not run
// the Unity importer or create a native Unity ScriptableObject.
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;

internal static class OriginalReaderCheck
{
    private static int checks;
    private static object Field(object instance, string name)
    {
        return instance.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
    }
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        checks++;
    }
    public static int Main(string[] args)
    {
        try
        {
            Assembly original = Assembly.LoadFrom(args[0]);
            Type settingsType = original.GetType("Mars.PsdTOUgui.Editor.PsdTOUguiSettings", true);
            // Only three parser guard fields are needed. Avoid invoking Unity's native
            // ScriptableObject constructor in this managed-only binary compatibility test.
            object settings = FormatterServices.GetUninitializedObject(settingsType);
            settingsType.GetField("maxManagedPsdFileMegabytes").SetValue(settings, 2048);
            settingsType.GetField("maxLayerRecords").SetValue(settings, 32767);
            settingsType.GetField("maxDecodedLayerPixels").SetValue(settings, 67108864);
            Type documentType = original.GetType("Mars.PsdTOUgui.Editor.ManagedPsdDocument", true);
            Type decoderType = original.GetType("Mars.PsdTOUgui.Editor.ManagedPsdLayerDecoder", true);
            string[] files = Directory.GetFiles(args[1], "*.psd");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                object document = documentType.GetMethod("Parse").Invoke(null, new object[] { file, settings });
                string stem = Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file));
                string[] manifest = File.ReadAllLines(stem + ".manifest.tsv");
                string[] dimensions = manifest[0].Split('\t');
                Check((int)Field(document, "width") == int.Parse(dimensions[0]), "Document width");
                Check((int)Field(document, "height") == int.Parse(dimensions[1]), "Document height");
                IList layers = (IList)Field(document, "layers");
                Check(layers.Count == int.Parse(dimensions[2]), "Layer count");
                object decoder = Activator.CreateInstance(decoderType, new object[] { file, document, settings });
                try
                {
                    for (int i = 0; i < layers.Count; i++)
                    {
                        string[] expected = manifest[i + 1].Split('\t');
                        object layer = layers[i];
                        string name = Encoding.UTF8.GetString(Convert.FromBase64String(expected[2]));
                        Check((string)Field(layer, "name") == name, "Unicode layer name/order: " + name);
                        int section = expected[1] == "GroupStart" ? 1 : expected[1] == "GroupEnd" ? 3 : 0;
                        Check((int)Field(layer, "sectionType") == section, "Group divider: " + name);
                        if (section != 0) continue;
                        Check((int)Field(layer, "left") == int.Parse(expected[3]), "Left: " + name);
                        Check((int)Field(layer, "top") == int.Parse(expected[4]), "Top: " + name);
                        Check((byte)Field(layer, "opacity") == byte.Parse(expected[7]), "Opacity: " + name);
                        Check((((byte)Field(layer, "flags") & 2) == 0) == (expected[8] == "1"), "Visible: " + name);
                        object[] decode = { layer, null, null };
                        Check((bool)decoderType.GetMethod("TryDecode").Invoke(decoder, decode), "Decode: " + decode[2]);
                        byte[] actual = (byte[])Field(decode[1], "rgbaTopDown");
                        byte[] wanted = File.ReadAllBytes(stem + ".layer" + i + ".rgba");
                        Check(actual.Length == wanted.Length, "Byte count: " + name);
                        for (int b = 0; b < actual.Length; b++)
                            if (actual[b] != wanted[b]) throw new Exception("Pixel mismatch " + name + " at " + b);
                        checks++;
                    }
                }
                finally { ((IDisposable)decoder).Dispose(); }
                Console.WriteLine("PASS original parser + layer decoder: " + Path.GetFileName(file));
            }
            Console.WriteLine("PASS: " + checks + " original-reader checks; " + files.Length + " files. No Unity importer/rendering invoked.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
