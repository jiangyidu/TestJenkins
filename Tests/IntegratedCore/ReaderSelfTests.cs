using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PrefabToPsd.Core;

/// <summary>Strict reader, pre-commit verification and failed-write regression tests.</summary>
public static class ReaderSelfTests
{
    private static int assertions;
    private static string output;

    public static int Main(string[] args)
    {
        try
        {
            output = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(output);
            ReadBaseline(Path.GetFullPath(args[1]));
            TestVerificationAndAtomicCommit();
            TestMalformedInput();
            TestEligibility();
            Console.WriteLine("PASS: " + assertions + " integrated reader/verifier assertions.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void ReadBaseline(string fixtures)
    {
        foreach (string manifestPath in Directory.GetFiles(fixtures, "*.manifest.tsv"))
        {
            string stem = manifestPath.Substring(0, manifestPath.Length - ".manifest.tsv".Length);
            string[] lines = File.ReadAllLines(manifestPath);
            string[] dimensions = lines[0].Split('\t');
            PsdDocument expected = new PsdDocument { Width = Int32.Parse(dimensions[0]), Height = Int32.Parse(dimensions[1]),
                CompositeRgba = File.ReadAllBytes(stem + ".merged.rgba") };
            for (int i = 1; i < lines.Length; i++)
            {
                string[] row = lines[i].Split('\t');
                PsdLayer layer = new PsdLayer {
                    Kind = (PsdLayerKind)Enum.Parse(typeof(PsdLayerKind), row[1]),
                    Name = Encoding.UTF8.GetString(Convert.FromBase64String(row[2])),
                    Left = Int32.Parse(row[3]), Top = Int32.Parse(row[4]), Width = Int32.Parse(row[5]), Height = Int32.Parse(row[6]),
                    Opacity = Byte.Parse(row[7]), Visible = row[8] == "1" };
                if (layer.Kind == PsdLayerKind.Pixel) layer.Rgba = File.ReadAllBytes(stem + ".layer" + row[0] + ".rgba");
                expected.Layers.Add(layer);
            }
            PsdOutputVerifier.Verify(stem + ".psd", expected);
            Check(true, "Complete baseline readback: " + Path.GetFileName(stem));
            using (PsdReferenceDocument reference = PsdReferenceReader.Open(stem + ".psd"))
            {
                Check(reference.Layers.Count == expected.Layers.Count, "Baseline layer count");
                for (int i = 0; i < reference.Layers.Count; i++)
                {
                    Check(reference.Layers[i].Name == expected.Layers[i].Name, "Unicode/order preserved");
                    if (expected.Layers[i].Kind == PsdLayerKind.Pixel)
                        Equal(reference.ReadRgba(reference.Layers[i]), expected.Layers[i].Rgba, "Baseline decoded bytes");
                }
            }
        }
    }

    private static PsdDocument Document()
    {
        PsdDocument doc = new PsdDocument { Width = 3, Height = 2 };
        doc.Layers.Add(new PsdLayer { Name = "组🚀", Kind = PsdLayerKind.GroupStart, Id = 30 });
        doc.Layers.Add(new PsdLayer { Name = "前景", Left = 0, Top = 0, Width = 2, Height = 2, Id = 7,
            Rgba = new byte[] { 200,80,30,128, 255,0,0,255, 0,255,0,1, 0,0,255,0 } });
        doc.Layers.Add(new PsdLayer { Name = "</组>", Kind = PsdLayerKind.GroupEnd });
        doc.Layers.Add(new PsdLayer { Name = "background", Left = 0, Top = 0, Width = 3, Height = 2,
            Rgba = new byte[] { 1,2,3,255, 4,5,6,255, 7,8,9,255, 10,11,12,255, 13,14,15,255, 16,17,18,255 } });
        doc.CompositeRgba = PsdRaster.CompositeLayers(doc);
        return doc;
    }

    private static void TestVerificationAndAtomicCommit()
    {
        PsdDocument doc = Document();
        foreach (PsdCompression compression in new[] { PsdCompression.Raw, PsdCompression.Rle })
        {
            string path = Path.Combine(output, "verified-" + compression + ".psd");
            if (File.Exists(path)) File.Delete(path);
            bool called = false;
            PsdWriter.Write(path, doc, compression, delegate(string temporary) {
                Check(!File.Exists(path), "Destination is not visible before commit");
                PsdOutputVerifier.Verify(temporary, doc); called = true;
            });
            Check(called && File.Exists(path), "Verifier called before a committed output");
            PsdOutputVerifier.Verify(path, doc);
            Check(true, "Readback matches explicit and automatic IDs, merged and all layer bytes");
        }
        string protectedPath = Path.Combine(output, "protected.bin");
        byte[] old = { 11,22,33,44,55 };
        File.WriteAllBytes(protectedPath, old);
        Throws(delegate { PsdWriter.Write(protectedPath, doc, PsdCompression.Raw,
            delegate(string temporary) { throw new InvalidDataException("Deliberate pre-commit reject"); }); }, "Verifier exception aborts write");
        Equal(File.ReadAllBytes(protectedPath), old, "Verifier failure preserves prior file");
        Throws(delegate { PsdWriter.Write(protectedPath, doc, PsdCompression.Raw,
            delegate(string temporary) {
                byte[] bytes = File.ReadAllBytes(temporary);
                bytes[bytes.Length - 1] ^= 1; File.WriteAllBytes(temporary, bytes);
                PsdOutputVerifier.Verify(temporary, doc);
            }); }, "Corrupted merged alpha is rejected before commit");
        Equal(File.ReadAllBytes(protectedPath), old, "Corrupt temporary never replaces prior file");
        Check(Directory.GetFiles(output, ".*.tmp").Length == 0, "Failed validation cleans temporary files");

        string valid = Path.Combine(output, "verified-Raw.psd");
        PsdDocument wrong = Document(); wrong.Layers[1].Name = "wrong";
        Throws(delegate { PsdOutputVerifier.Verify(valid, wrong); }, "Wrong expected identity rejected");
        wrong = Document(); wrong.Layers[1].Rgba[0] ^= 1;
        Throws(delegate { PsdOutputVerifier.Verify(valid, wrong); }, "Wrong expected layer pixel rejected");
        wrong = Document(); wrong.CompositeRgba[3] ^= 1;
        Throws(delegate { PsdOutputVerifier.Verify(valid, wrong); }, "Wrong expected merged alpha rejected");
    }

    private static void TestMalformedInput()
    {
        byte[] valid = File.ReadAllBytes(Path.Combine(output, "verified-Raw.psd"));
        foreach (int length in new[] { 0,1,4,25,26,30,34,valid.Length / 2,valid.Length - 2,valid.Length - 1 })
        {
            byte[] truncated = new byte[length]; Buffer.BlockCopy(valid, 0, truncated, 0, length);
            Reject(truncated, "Truncated PSD rejected at " + length);
        }
        byte[] changed = (byte[])valid.Clone(); Put32(changed,18,30001); Reject(changed, "Oversized width rejected");
        changed = (byte[])valid.Clone(); changed[12]=0; changed[13]=2; Reject(changed, "Too few header channels rejected");
        changed = (byte[])valid.Clone(); changed[23]=16; Reject(changed, "RGB16 reference rejected");
        changed = (byte[])valid.Clone(); changed[5]=2; Reject(changed, "PSB does not masquerade as PSD v1");
        List<Record> records = Records(valid);
        changed = (byte[])valid.Clone(); Put32(changed,records[1].IdValue,Read32(changed,records[0].IdValue));
        Reject(changed, "Duplicate positive IDs rejected");
        changed = (byte[])valid.Clone(); Put32(changed,records[2].UnicodeValue,32768); Reject(changed, "Excessive Unicode name length rejected");
        changed = (byte[])valid.Clone(); changed[records[2].UnicodeValue+4]=0xD8;changed[records[2].UnicodeValue+5]=0;
        Reject(changed, "Unpaired UTF16 surrogate rejected");
        changed = (byte[])valid.Clone(); changed[records[2].UnicodeValue+4]=0;changed[records[2].UnicodeValue+5]=0;
        Reject(changed,"Embedded NUL cannot spoof a reference identity");
        changed = (byte[])valid.Clone(); changed[records[2].UnicodeValue+6]=0;changed[records[2].UnicodeValue+7]=0;
        string nulNamePath=Path.Combine(output,"unicode-terminal-nul.psd");File.WriteAllBytes(nulNamePath,changed);
        using(PsdReferenceDocument reference=PsdReferenceReader.Open(nulNamePath))
            Check(reference.Layers[1].Name=="前","A declared trailing Unicode NUL is accepted as a terminator");
        changed = (byte[])valid.Clone(); Put32(changed,records[1].SectionValue,0); Reject(changed, "Unbalanced group rejected");
        changed = (byte[])valid.Clone(); Put32(changed,records[3].SectionValue,3); Reject(changed, "Group end before start rejected");

        byte[] rle = File.ReadAllBytes(Path.Combine(output, "verified-Rle.psd"));
        List<Record> rleRecords = Records(rle);
        // Keep the channel/table lengths valid while corrupting the actual PackBits packet.
        changed = (byte[])rle.Clone();
        int alphaChannel = rleRecords[0].ChannelOffsets[0];
        changed[alphaChannel + 2 + 2 * 2] = 127;
        string badRle = Path.Combine(output, "malformed-rle.psd"); File.WriteAllBytes(badRle,changed);
        using (PsdReferenceDocument reference = PsdReferenceReader.Open(badRle))
            Throws(delegate { reference.ReadRgba(reference.Layers[3]); }, "Oversized PackBits literal rejected on lazy decode");
        changed = (byte[])rle.Clone(); changed[alphaChannel + 3]++;
        File.WriteAllBytes(badRle,changed);
        using (PsdReferenceDocument reference = PsdReferenceReader.Open(badRle))
            Throws(delegate { reference.ReadRgba(reference.Layers[3]); }, "RLE row table must match channel length");
        changed = (byte[])valid.Clone();
        int rawAlpha = records[0].ChannelOffsets[0]; changed[rawAlpha + 1]=2;
        File.WriteAllBytes(badRle,changed);
        using (PsdReferenceDocument reference = PsdReferenceReader.Open(badRle))
        {
            Check(!reference.Layers[3].EligibleForPixelReuse,"Unsupported channel compression excludes reuse");
            Throws(delegate { reference.ReadRgba(reference.Layers[3]); }, "Unsupported channel compression is explicit");
        }

        string wide = Path.GetFullPath(Path.Combine(output, "../baseline/fixtures/maximum_width_rle.psd"));
        using (PsdReferenceDocument reference = PsdReferenceReader.Open(wide, 2147483647L, 32767, 65536))
            Throws(delegate { reference.ReadRgba(reference.Layers[0]); }, "Large decode refused before allocation");
        using (PsdReferenceDocument reference = PsdReferenceReader.Open(Path.Combine(output,"verified-Raw.psd")))
        {
            PsdReferenceLayer layer = reference.Layers[1]; reference.Dispose();
            Throws(delegate { reference.ReadRgba(layer); }, "Disposed read handle refused");
        }
    }

    private static void TestEligibility()
    {
        byte[] valid = File.ReadAllBytes(Path.Combine(output, "verified-Raw.psd"));
        List<Record> records = Records(valid);
        string path = Path.Combine(output, "eligibility.psd");
        File.WriteAllBytes(path,valid);
        using (PsdReferenceDocument reference=PsdReferenceReader.Open(path))
            Check(reference.Layers[1].EligibleForPixelReuse, "Ordinary normal raster in safe pass-through group eligible");
        byte[] changed=(byte[])valid.Clone(); changed[records[2].Opacity]=128;
        Eligibility(changed,false,"Layer opacity must not be applied twice");
        changed=(byte[])valid.Clone(); changed[records[2].Clipping]=1;
        Eligibility(changed,false,"Clipped raster is not blindly reusable");
        changed=(byte[])valid.Clone(); changed[records[2].Flags]|=2;
        Eligibility(changed,false,"Hidden raster is not reusable as visible appearance");
        changed=(byte[])valid.Clone(); changed[records[3].Opacity]=128;
        Eligibility(changed,false,"Ancestor opacity must be baked rather than reapplied");
        changed=(byte[])valid.Clone(); changed[records[3].Flags]|=2;
        Eligibility(changed,false,"Ancestor visibility is propagated");
        changed=(byte[])valid.Clone(); ReplaceTag(changed,records[2],"lyid","vmsk");
        Eligibility(changed,false,"Vector mask is not silently omitted");
        changed=(byte[])valid.Clone(); ReplaceTag(changed,records[2],"lyid","lfx2");
        Eligibility(changed,false,"Layer effects are not silently omitted");
        changed=(byte[])valid.Clone(); ReplaceTag(changed,records[2],"lyid","SoLd");
        Eligibility(changed,false,"Smart object semantics are not silently omitted");
    }

    private static void Eligibility(byte[] bytes,bool expected,string label)
    {
        string path=Path.Combine(output,"eligibility.psd");File.WriteAllBytes(path,bytes);
        using(PsdReferenceDocument reference=PsdReferenceReader.Open(path))
            Check(reference.Layers[1].EligibleForPixelReuse==expected,label);
    }

    private static void Reject(byte[] bytes,string label)
    {
        string path=Path.Combine(output,"malformed.psd");File.WriteAllBytes(path,bytes);
        Throws(delegate{using(PsdReferenceDocument reference=PsdReferenceReader.Open(path)){}},label);
    }

    private sealed class Record
    {
        internal int ExtraStart,ExtraEnd,Opacity,Clipping,Flags,IdValue,UnicodeValue,SectionValue=-1;
        internal readonly List<int> ChannelLengths=new List<int>(),ChannelOffsets=new List<int>();
    }
    private static List<Record> Records(byte[] bytes)
    {
        int position=26;position+=4+(int)Read32(bytes,position);position+=4+(int)Read32(bytes,position);
        position+=8;int count=Math.Abs((int)unchecked((short)Read16(bytes,position)));position+=2;
        List<Record> result=new List<Record>();
        for(int i=0;i<count;i++)
        {
            position+=16;int channels=Read16(bytes,position);position+=2;
            Record r=new Record();
            for(int channel=0;channel<channels;channel++)
            {r.ChannelLengths.Add((int)Read32(bytes,position+2));position+=6;}
            position+=8;r.Opacity=position;r.Clipping=position+1;r.Flags=position+2;position+=4;
            int extra=(int)Read32(bytes,position);position+=4;r.ExtraStart=position;r.ExtraEnd=position+extra;
            int p=position;p+=4+(int)Read32(bytes,p);p+=4+(int)Read32(bytes,p);
            p+=(bytes[p]+1+3)&~3;
            while(p+12<=r.ExtraEnd)
            {
                string key=Encoding.ASCII.GetString(bytes,p+4,4);int length=(int)Read32(bytes,p+8);
                if(key=="lyid")r.IdValue=p+12;
                if(key=="luni")r.UnicodeValue=p+12;
                if(key=="lsct")r.SectionValue=p+12;
                p+=12+length+(length&1);
            }
            result.Add(r);position=r.ExtraEnd;
        }
        foreach(Record record in result)
            foreach(int length in record.ChannelLengths){record.ChannelOffsets.Add(position);position+=length;}
        return result;
    }
    private static void ReplaceTag(byte[] bytes,Record record,string oldKey,string newKey)
    {
        for(int p=record.ExtraStart;p+12<=record.ExtraEnd;p++)
            if(Encoding.ASCII.GetString(bytes,p,8)=="8BIM"+oldKey)
            {Buffer.BlockCopy(Encoding.ASCII.GetBytes(newKey),0,bytes,p+4,4);return;}
        throw new Exception("Tag not found: "+oldKey);
    }
    private static int Read16(byte[] b,int p){return(b[p]<<8)|b[p+1];}
    private static uint Read32(byte[] b,int p){return((uint)Read16(b,p)<<16)|(uint)Read16(b,p+2);}
    private static void Put32(byte[] b,int p,uint value){b[p]=(byte)(value>>24);b[p+1]=(byte)(value>>16);b[p+2]=(byte)(value>>8);b[p+3]=(byte)value;}
    private static void Check(bool ok,string label){if(!ok)throw new Exception("ASSERT: "+label);assertions++;}
    private static void Equal(byte[] a,byte[] b,string label){Check(a.Length==b.Length,label+" length");for(int i=0;i<a.Length;i++)if(a[i]!=b[i])throw new Exception(label+" byte "+i);assertions++;}
    private static void Throws(Action action,string label)
    {
        bool threw=false;try{action();}catch(ArgumentException){threw=true;}catch(InvalidDataException){threw=true;}catch(IOException){threw=true;}
        catch(NotSupportedException){threw=true;}catch(ObjectDisposedException){threw=true;}
        Check(threw,label);
    }
}
