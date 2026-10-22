using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PrefabToPsd.Core
{
    /// <summary>Read-only metadata for a PSD record. Order is Photoshop panel order.</summary>
    public sealed class PsdReferenceLayer
    {
        public int Id;
        public string Name;
        public int Left, Top, Width, Height;
        public PsdLayerKind Kind;
        public bool Visible;
        public byte Opacity;
        public string BlendMode;
        public bool HasRasterMask, HasVectorMask, HasEffects, IsSmartObject, HasTypeTool, IsClipping;
        public bool AncestorsVisible, AncestorsOpaque, EligibleForPixelReuse;
        public int PhysicalIndex;
        internal bool HasOtherAppearance;
        internal readonly List<PsdReferenceChannel> Channels = new List<PsdReferenceChannel>();
    }

    internal sealed class PsdReferenceChannel
    {
        internal short Id;
        internal long Length, Offset;
        internal int Compression = -1;
    }

    /// <summary>
    /// Holds a read-shared file handle. Metadata is read eagerly; raster channels are decoded
    /// one layer at a time within a caller-defined budget. Dispose before replacing the source.
    /// Only PSD v1 RGB8 is accepted. Unsupported appearance is never declared reusable.
    /// </summary>
    public sealed class PsdReferenceDocument : IDisposable
    {
        public int Width, Height;
        public readonly List<PsdReferenceLayer> Layers = new List<PsdReferenceLayer>();
        public readonly List<PsdResource> Resources = new List<PsdResource>();
        public string SourcePath { get; private set; }
        public int HeaderChannels { get; internal set; }
        public bool HasMergedTransparency { get; internal set; }
        internal FileStream Stream;
        internal long MaxDecodedBytes;
        internal long MergedOffset;
        private bool disposed;

        internal PsdReferenceDocument(string path, FileStream stream, long maxDecodedBytes)
        {
            SourcePath = path; Stream = stream; MaxDecodedBytes = maxDecodedBytes;
        }

        public byte[] ReadRgba(PsdReferenceLayer layer)
        {
            if (disposed) throw new ObjectDisposedException("PsdReferenceDocument");
            if (layer == null || !Layers.Contains(layer)) throw new ArgumentException("Layer does not belong to this reference.");
            if (layer.Kind != PsdLayerKind.Pixel || layer.Width < 1 || layer.Height < 1)
                throw new ArgumentException("A non-empty pixel layer is required.");
            int bytes = PsdRaster.ByteLength(layer.Width, layer.Height);
            // RGBA output plus one decoded scanline and compressed row buffer.
            if ((long)bytes + layer.Height * 4L + layer.Width + 65536L > MaxDecodedBytes)
                throw new InvalidDataException("Reference layer exceeds the decoded-pixel memory budget.");
            byte[] rgba = new byte[bytes];
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
            foreach (PsdReferenceChannel channel in layer.Channels)
            {
                int component = channel.Id == -1 ? 3 : channel.Id;
                if (component < 0 || component > 3) continue;
                // IDs above RGB are spot/auxiliary channels, not output alpha.
                if (channel.Id == 3) continue;
                if (channel.Length < 2) throw new InvalidDataException("Missing layer compression word.");
                Stream.Position = channel.Offset;
                int compression = PsdReferenceReader.U16(Stream);
                long end = channel.Offset + channel.Length;
                if (compression == 0)
                {
                    if (channel.Length != 2L + layer.Width * (long)layer.Height)
                        throw new InvalidDataException("Raw layer channel length does not match its bounds.");
                    byte[] row = new byte[layer.Width];
                    for (int y = 0; y < layer.Height; y++)
                    {
                        PsdReferenceReader.ReadExactly(Stream, row, 0, row.Length);
                        for (int x = 0; x < row.Length; x++) rgba[(y * layer.Width + x) * 4 + component] = row[x];
                    }
                }
                else if (compression == 1)
                {
                    if (channel.Length < 2L + layer.Height * 2L)
                        throw new InvalidDataException("Truncated RLE scanline table.");
                    int[] counts = new int[layer.Height];
                    long total = 0;
                    for (int y = 0; y < counts.Length; y++) { counts[y] = PsdReferenceReader.U16(Stream); total += counts[y]; }
                    if (Stream.Position + total != end) throw new InvalidDataException("RLE channel byte counts are inconsistent.");
                    byte[] row = new byte[layer.Width];
                    for (int y = 0; y < layer.Height; y++)
                    {
                        byte[] compressed = new byte[counts[y]];
                        PsdReferenceReader.ReadExactly(Stream, compressed, 0, compressed.Length);
                        DecodePackBits(compressed, row);
                        for (int x = 0; x < row.Length; x++) rgba[(y * layer.Width + x) * 4 + component] = row[x];
                    }
                }
                else throw new NotSupportedException("Reference pixels require Raw or RLE channels.");
                if (Stream.Position != end) throw new InvalidDataException("Layer channel did not end at its declared boundary.");
            }
            return rgba;
        }

        /// <summary>
        /// Returns interleaved RGB and merged alpha exactly as stored. RGB includes the
        /// PSD white matte when HasMergedTransparency is true; it is not straight RGB.
        /// This method is primarily for verifying writer output, not reference reuse.
        /// </summary>
        public byte[] ReadCompositeStoredRgba()
        {
            if (disposed) throw new ObjectDisposedException("PsdReferenceDocument");
            int size = PsdRaster.ByteLength(Width, Height);
            long rows = (long)HeaderChannels * Height;
            if ((long)size + rows * 4L + Width + 65536L > MaxDecodedBytes)
                throw new InvalidDataException("Merged image exceeds the decoded-pixel memory budget.");
            Stream.Position = MergedOffset;
            int compression = PsdReferenceReader.U16(Stream);
            if (compression != 0 && compression != 1)
                throw new NotSupportedException("Merged verification requires Raw or RLE data.");
            int[] counts = null;
            if (compression == 1)
            {
                counts = new int[checked((int)rows)];
                for (int i = 0; i < counts.Length; i++) counts[i] = PsdReferenceReader.U16(Stream);
            }
            byte[] result = new byte[size];
            for (int i = 3; i < result.Length; i += 4) result[i] = 255;
            byte[] row = new byte[Width];
            for (int channel = 0; channel < HeaderChannels; channel++)
            {
                for (int y = 0; y < Height; y++)
                {
                    if (compression == 0) PsdReferenceReader.ReadExactly(Stream, row, 0, Width);
                    else
                    {
                        byte[] packed = new byte[counts[channel * Height + y]];
                        PsdReferenceReader.ReadExactly(Stream, packed, 0, packed.Length);
                        DecodePackBits(packed, row);
                    }
                    if (channel < 3 || (channel == 3 && HasMergedTransparency))
                        for (int x = 0; x < Width; x++) result[(y * Width + x) * 4 + channel] = row[x];
                }
            }
            if (Stream.Position != Stream.Length)
                throw new InvalidDataException("Merged channel data did not end at the file boundary.");
            return result;
        }

        private static void DecodePackBits(byte[] source, byte[] row)
        {
            int input = 0, output = 0;
            while (input < source.Length)
            {
                int token = unchecked((sbyte)source[input++]);
                if (token >= 0)
                {
                    int count = token + 1;
                    if (input + count > source.Length || output + count > row.Length)
                        throw new InvalidDataException("Malformed PackBits literal.");
                    Buffer.BlockCopy(source, input, row, output, count); input += count; output += count;
                }
                else if (token != -128)
                {
                    int count = 1 - token;
                    if (input >= source.Length || output + count > row.Length)
                        throw new InvalidDataException("Malformed PackBits repeat.");
                    byte value = source[input++];
                    for (int i = 0; i < count; i++) row[output++] = value;
                }
            }
            if (output != row.Length) throw new InvalidDataException("Truncated PackBits row.");
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Stream.Dispose();
        }
    }

    public static class PsdReferenceReader
    {
        private const long MaxResources = 16L * 1024L * 1024L;
        private const long MaxExtraData = 16L * 1024L * 1024L;

        public static PsdReferenceDocument Open(string path, long maxFileBytes = 2147483647L,
            int maxRecords = 32767, long maxDecodedBytes = 268435456L)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Reference PSD path is required.");
            if (maxFileBytes < 26 || maxFileBytes > 2147483647L || maxRecords < 1 || maxRecords > 32767 || maxDecodedBytes < 65536)
                throw new ArgumentOutOfRangeException("maxFileBytes", "Invalid reference safety limits.");
            string absolute = Path.GetFullPath(path);
            FileStream stream = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read);
            PsdReferenceDocument document = new PsdReferenceDocument(absolute, stream, maxDecodedBytes);
            try
            {
                if (stream.Length < 26 || stream.Length > maxFileBytes)
                    throw new InvalidDataException("Reference PSD exceeds the file-size safety limit or is truncated.");
                if (Ascii(stream, 4) != "8BPS" || U16(stream) != 1)
                    throw new NotSupportedException("Only PSD v1 reference files are supported (not PSB).");
                byte[] reserved = Bytes(stream, 6);
                for (int i = 0; i < reserved.Length; i++) if (reserved[i] != 0) throw new InvalidDataException("Invalid PSD reserved header.");
                int channels = U16(stream);
                if (channels < 3 || channels > 56) throw new InvalidDataException("Invalid RGB channel count.");
                document.HeaderChannels = channels;
                document.Height = checked((int)U32(stream)); document.Width = checked((int)U32(stream));
                if (document.Width < 1 || document.Height < 1 || document.Width > 30000 || document.Height > 30000)
                    throw new InvalidDataException("Reference PSD dimensions must be 1..30000.");
                if (U16(stream) != 8 || U16(stream) != 3)
                    throw new NotSupportedException("Reference pixels require RGB 8-bit PSD.");
                SkipSection(stream, stream.Length);
                long resourceEnd = SectionEnd(stream, stream.Length, MaxResources);
                ReadResources(stream, resourceEnd, document.Resources);
                long layerMaskEnd = SectionEnd(stream, stream.Length, maxFileBytes);
                if (stream.Position < layerMaskEnd)
                {
                    long infoEnd = SectionEnd(stream, layerMaskEnd, maxFileBytes);
                    if (stream.Position < infoEnd)
                    {
                        int signedCount = unchecked((short)U16(stream));
                        document.HasMergedTransparency = signedCount < 0;
                        int count = Math.Abs(signedCount);
                        if (count > maxRecords) throw new InvalidDataException("Reference PSD has too many layer records.");
                        List<PsdReferenceLayer> physical = new List<PsdReferenceLayer>();
                        for (int i = 0; i < count; i++) physical.Add(ReadLayer(stream, infoEnd, i));
                        foreach (PsdReferenceLayer layer in physical)
                        {
                            foreach (PsdReferenceChannel channel in layer.Channels)
                            {
                                channel.Offset = stream.Position;
                                long end = EndAt(stream, channel.Length, infoEnd);
                                if (channel.Length >= 2) channel.Compression = U16(stream);
                                stream.Position = end;
                            }
                        }
                        // Layer info may contain exactly one padding byte, never unparsed data.
                        if (infoEnd - stream.Position > 1) throw new InvalidDataException("Unexpected bytes after layer channel data.");
                        stream.Position = infoEnd;
                        physical.Reverse(); document.Layers.AddRange(physical);
                        HashSet<int> layerIds = new HashSet<int>();
                        foreach (PsdReferenceLayer layer in document.Layers)
                            if (layer.Id > 0 && !layerIds.Add(layer.Id))
                                throw new InvalidDataException("Duplicate positive layer ID in reference PSD.");
                        AnalyzeGroups(document.Layers);
                    }
                    if (stream.Position < layerMaskEnd)
                    {
                        SkipSection(stream, layerMaskEnd); // Global layer mask.
                        while (stream.Position < layerMaskEnd)
                        {
                            if (layerMaskEnd - stream.Position < 12) throw new InvalidDataException("Truncated global tagged block.");
                            string signature = Ascii(stream, 4); Ascii(stream, 4);
                            if (signature != "8BIM" && signature != "8B64") throw new InvalidDataException("Invalid global tagged-block signature.");
                            long length = U32(stream);
                            long end = EndAt(stream, length + (length & 1), layerMaskEnd);
                            stream.Position = end;
                        }
                    }
                }
                stream.Position = layerMaskEnd;
                document.MergedOffset = layerMaskEnd;
                ValidateMergedEnvelope(stream, channels, document.Width, document.Height);
                return document;
            }
            catch { document.Dispose(); throw; }
        }

        private static void ReadResources(Stream stream, long end, List<PsdResource> resources)
        {
            HashSet<ushort> seen = new HashSet<ushort>();
            while (stream.Position < end)
            {
                if (end - stream.Position < 12 || Ascii(stream, 4) != "8BIM")
                    throw new InvalidDataException("Invalid image-resource block.");
                ushort id = (ushort)U16(stream);
                int nameLength = Byte(stream);
                long nameEnd = EndAt(stream, nameLength + (((1 + nameLength) & 1) != 0 ? 1 : 0), end);
                stream.Position = nameEnd;
                long size = U32(stream);
                long dataEnd = EndAt(stream, size + (size & 1), end);
                if (id == 1005 || id == 1039 || id == 1064)
                {
                    if (!seen.Add(id)) throw new InvalidDataException("Duplicate reference image resource.");
                    if (size > 4L * 1024L * 1024L) throw new InvalidDataException("Reference resource exceeds its allocation limit.");
                    // An unsupported profile is retained as read-only metadata; the bridge must
                    // call PsdSafeResources.Validate/IsSrgbIccProfile before copying into output.
                    resources.Add(new PsdResource { Id = id, Data = Bytes(stream, (int)size) });
                }
                stream.Position = dataEnd;
            }
        }

        private static PsdReferenceLayer ReadLayer(Stream stream, long limit, int index)
        {
            if (limit - stream.Position < 34) throw new InvalidDataException("Truncated layer record.");
            PsdReferenceLayer layer = new PsdReferenceLayer(); layer.PhysicalIndex = index;
            int top = unchecked((int)U32(stream)), left = unchecked((int)U32(stream));
            int bottom = unchecked((int)U32(stream)), right = unchecked((int)U32(stream));
            long w = (long)right - left, h = (long)bottom - top;
            if (w < 0 || h < 0 || w > 30000 || h > 30000) throw new InvalidDataException("Invalid reference layer bounds.");
            layer.Top = top; layer.Left = left; layer.Width = (int)w; layer.Height = (int)h;
            int count = U16(stream);
            if (count > 56) throw new InvalidDataException("Too many layer channels.");
            HashSet<short> ids = new HashSet<short>();
            for (int i = 0; i < count; i++)
            {
                short id = unchecked((short)U16(stream));
                long length = U32(stream);
                if (!ids.Add(id)) throw new InvalidDataException("Duplicate layer channel ID.");
                if (id == -2 || id == -3) layer.HasRasterMask = true;
                layer.Channels.Add(new PsdReferenceChannel { Id = id, Length = length });
            }
            if (Ascii(stream, 4) != "8BIM") throw new InvalidDataException("Invalid layer blend signature.");
            layer.BlendMode = Ascii(stream, 4); layer.Opacity = (byte)Byte(stream);
            layer.IsClipping = Byte(stream) != 0;
            byte flags = (byte)Byte(stream); layer.Visible = (flags & 2) == 0; Byte(stream);
            long extraEnd = SectionEnd(stream, limit, MaxExtraData);
            long maskEnd = SectionEnd(stream, extraEnd, MaxExtraData);
            layer.HasRasterMask = layer.HasRasterMask || maskEnd > stream.Position; stream.Position = maskEnd;
            SkipSection(stream, extraEnd); // Blending ranges.
            int nameLength = Byte(stream);
            if (nameLength > extraEnd - stream.Position) throw new InvalidDataException("Truncated layer name.");
            layer.Name = Encoding.ASCII.GetString(Bytes(stream, nameLength));
            int padding = (4 - ((nameLength + 1) & 3)) & 3;
            stream.Position = EndAt(stream, padding, extraEnd);
            HashSet<string> identityTags = new HashSet<string>(StringComparer.Ordinal);
            bool sectionSeen = false;
            while (stream.Position < extraEnd)
            {
                if (extraEnd - stream.Position < 12) throw new InvalidDataException("Truncated layer tagged block.");
                string signature = Ascii(stream, 4), key = Ascii(stream, 4);
                if (signature != "8BIM" && signature != "8B64") throw new InvalidDataException("Invalid layer tagged signature.");
                if ((key == "luni" || key == "lyid") && !identityTags.Add(key))
                    throw new InvalidDataException("Duplicate layer identity tag.");
                if (key == "lsct" || key == "lsdk")
                {
                    if (sectionSeen) throw new InvalidDataException("Duplicate layer section-divider tag.");
                    sectionSeen = true;
                }
                long length = U32(stream);
                long dataEnd = EndAt(stream, length, extraEnd);
                long paddedEnd = dataEnd + (length & 1);
                if (paddedEnd > extraEnd) throw new InvalidDataException("Missing tagged-block padding.");
                if (key == "luni")
                {
                    if (length < 4) throw new InvalidDataException("Truncated Unicode layer name.");
                    long units = U32(stream);
                    if (units > 32767 || units * 2L > dataEnd - stream.Position) throw new InvalidDataException("Invalid Unicode layer name length.");
                    // Photoshop/third-party writers may include a terminating Unicode
                    // NUL in the declared count. It is not part of the visible layer name.
                    layer.Name = new UnicodeEncoding(true, false, true).GetString(Bytes(stream, (int)units * 2)).TrimEnd('\0');
                    if (layer.Name.IndexOf('\0') >= 0) throw new InvalidDataException("NUL in layer name.");
                }
                else if (key == "lyid")
                {
                    if (length != 4) throw new InvalidDataException("Invalid layer ID block.");
                    uint id = U32(stream);
                    if (id > Int32.MaxValue) throw new InvalidDataException("Layer ID exceeds the supported integer range.");
                    layer.Id = (int)id;
                }
                else if (key == "lsct" || key == "lsdk")
                {
                    if (length < 4) throw new InvalidDataException("Truncated section divider.");
                    uint type = U32(stream);
                    if (type == 1 || type == 2) layer.Kind = PsdLayerKind.GroupStart;
                    else if (type == 3) layer.Kind = PsdLayerKind.GroupEnd;
                    else if (type != 0) throw new InvalidDataException("Unknown section divider kind.");
                    if (length >= 12)
                    {
                        if (Ascii(stream, 4) != "8BIM") throw new InvalidDataException("Invalid divider blend signature.");
                        string blend = Ascii(stream, 4);
                        if (layer.Kind == PsdLayerKind.GroupStart) layer.BlendMode = blend;
                    }
                }
                else if (key == "vmsk" || key == "vsms") layer.HasVectorMask = true;
                else if (key == "lrFX" || key == "lfx2" || key == "lmfx" || key == "lfxs") layer.HasEffects = true;
                else if (key == "SoLd" || key == "SoLE" || key == "PlLd" || key == "Plcd") layer.IsSmartObject = true;
                else if (key == "TySh" || key == "tySh") layer.HasTypeTool = true;
                else if (key == "iOpa")
                {
                    if (length < 1 || Byte(stream) != 255) layer.HasOtherAppearance = true;
                }
                else if (key == "brit" || key == "levl" || key == "curv" || key == "hue2" || key == "hue " ||
                    key == "vibA" || key == "blnc" || key == "mixr" || key == "grdm" || key == "selc" ||
                    key == "phfl" || key == "expA" || key == "nvrt" || key == "thrs" || key == "post")
                    layer.HasOtherAppearance = true;
                stream.Position = paddedEnd;
            }
            if (layer.Name == null) layer.Name = "";
            return layer;
        }

        private static void AnalyzeGroups(List<PsdReferenceLayer> layers)
        {
            Stack<bool> visibility = new Stack<bool>(), opaque = new Stack<bool>();
            bool parentVisible = true, parentOpaque = true;
            foreach (PsdReferenceLayer layer in layers)
            {
                layer.AncestorsVisible = parentVisible; layer.AncestorsOpaque = parentOpaque;
                if (layer.Kind == PsdLayerKind.GroupStart)
                {
                    visibility.Push(parentVisible); opaque.Push(parentOpaque);
                    parentVisible = parentVisible && layer.Visible;
                    parentOpaque = parentOpaque && layer.Opacity == 255 && layer.BlendMode == "pass" &&
                        !layer.HasRasterMask && !layer.HasVectorMask && !layer.HasEffects && !layer.IsSmartObject &&
                        !layer.IsClipping && !layer.HasOtherAppearance;
                }
                else if (layer.Kind == PsdLayerKind.GroupEnd)
                {
                    if (visibility.Count == 0) throw new InvalidDataException("Reference group end precedes its start.");
                    parentVisible = visibility.Pop(); parentOpaque = opaque.Pop();
                }
                else
                {
                    bool rgb = false, green = false, blue = false, channelsSafe = true;
                    foreach (PsdReferenceChannel channel in layer.Channels)
                    {
                        if (channel.Id == 0) rgb = true;
                        if (channel.Id == 1) green = true;
                        if (channel.Id == 2) blue = true;
                        if ((channel.Id >= -1 && channel.Id <= 2) && channel.Compression != 0 && channel.Compression != 1)
                            channelsSafe = false;
                    }
                    layer.EligibleForPixelReuse = layer.Visible && parentVisible && parentOpaque &&
                        layer.Opacity == 255 && layer.BlendMode == "norm" && !layer.HasRasterMask &&
                        !layer.HasVectorMask && !layer.HasEffects && !layer.IsSmartObject && !layer.IsClipping &&
                        !layer.HasOtherAppearance && layer.Width > 0 && layer.Height > 0 && rgb && green && blue && channelsSafe;
                }
            }
            if (visibility.Count != 0) throw new InvalidDataException("Unclosed reference group.");
        }

        private static void ValidateMergedEnvelope(Stream stream, int channels, int width, int height)
        {
            int compression = U16(stream);
            if (compression == 0)
            {
                if (stream.Length - stream.Position != channels * (long)width * height)
                    throw new InvalidDataException("Truncated or oversized Raw merged image.");
            }
            else if (compression == 1)
            {
                long rows = channels * (long)height;
                if (rows * 2 > stream.Length - stream.Position) throw new InvalidDataException("Truncated merged RLE table.");
                long bytes = 0;
                for (long i = 0; i < rows; i++) bytes += U16(stream);
                if (bytes != stream.Length - stream.Position) throw new InvalidDataException("Merged RLE lengths are inconsistent.");
            }
            else if (compression != 2 && compression != 3)
                throw new NotSupportedException("Unknown merged-image compression.");
            else if (stream.Position == stream.Length) throw new InvalidDataException("Missing ZIP merged image.");
        }

        private static long SectionEnd(Stream stream, long limit, long maximum)
        {
            if (limit - stream.Position < 4) throw new InvalidDataException("Truncated section length.");
            long length = U32(stream);
            if (length > maximum) throw new InvalidDataException("PSD section exceeds its safety limit.");
            return EndAt(stream, length, limit);
        }
        private static void SkipSection(Stream stream, long limit) { stream.Position = SectionEnd(stream, limit, Int32.MaxValue); }
        private static long EndAt(Stream stream, long length, long limit)
        {
            if (length < 0 || stream.Position > limit || length > limit - stream.Position)
                throw new InvalidDataException("PSD section exceeds its enclosing boundary.");
            return stream.Position + length;
        }
        internal static int U16(Stream stream) { return (Byte(stream) << 8) | Byte(stream); }
        internal static uint U32(Stream stream) { return ((uint)U16(stream) << 16) | (uint)U16(stream); }
        private static int Byte(Stream stream) { int value = stream.ReadByte(); if (value < 0) throw new EndOfStreamException(); return value; }
        private static string Ascii(Stream stream, int count) { return Encoding.ASCII.GetString(Bytes(stream, count)); }
        private static byte[] Bytes(Stream stream, int count) { byte[] result = new byte[count]; ReadExactly(stream, result, 0, count); return result; }
        internal static void ReadExactly(Stream stream, byte[] bytes, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(bytes, offset, count);
                if (read <= 0) throw new EndOfStreamException();
                offset += read; count -= read;
            }
        }
    }
}
