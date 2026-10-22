using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PrefabToPsd.Core
{
    /// <summary>
    /// Self-contained RGB8 PSD v1 writer. Supports normal raster layers and pass-through groups,
    /// Unicode names, straight alpha, and raw/PackBits channels. Does not emit editable text.
    /// Format reference: https://www.adobe.com/devnet-apps/photoshop/fileformatashtml/
    /// </summary>
    public static class PsdWriter
    {
        // PSD v1 compatibility limit; PSB is deliberately not silently substituted.
        private const long MaxFileBytes = 2147483647L;

        public static void Write(string path, PsdDocument document, PsdCompression compression = PsdCompression.Rle)
        {
            Write(path, document, compression, null);
        }

        /// <summary>
        /// Write and optionally validate the complete temporary PSD before committing it.
        /// A validation exception leaves a previous destination intact and removes the temporary.
        /// The callback must close any read handle before returning.
        /// </summary>
        public static void Write(string path, PsdDocument document, PsdCompression compression,
            Action<string> validateTemporary)
        {
            Validate(document, compression);
            if (String.IsNullOrEmpty(path)) throw new ArgumentException("A PSD path is required.", "path");
            string absolute = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(absolute);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            string temporary = Path.Combine(directory, "." + Path.GetFileName(absolute) + "." +
                Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    Write(stream, document, compression, true);
                    stream.Flush();
                }
                if (validateTemporary != null) validateTemporary(temporary);
                // Preserve the previous destination unless a complete replacement is available.
                // If File.Replace is unavailable on a filesystem, fail safely rather than delete it.
                if (File.Exists(absolute)) File.Replace(temporary, absolute, null);
                else File.Move(temporary, absolute);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        /// <summary>Write at the current position of a seekable stream. The caller controls rollback on stream errors.</summary>
        public static void Write(Stream stream, PsdDocument document, PsdCompression compression = PsdCompression.Rle,
            bool leaveOpen = true)
        {
            Validate(document, compression);
            if (stream == null) throw new ArgumentNullException("stream");
            if (!stream.CanWrite || !stream.CanSeek) throw new ArgumentException("A writable, seekable stream is required.");
            try
            {
                BigEndian output = new BigEndian(stream);
                long fileStart = stream.Position;
                output.Ascii("8BPS");
                output.U16(1);
                output.Bytes(new byte[6]);
                output.U16(4);
                output.U32((uint)document.Height);
                output.U32((uint)document.Width);
                output.U16(8);
                output.U16(3);
                output.U32(0); // Color mode data.
                WriteResources(output, document.SafeResources);

                long layerMaskLength = output.Reserve32();
                long layerInfoLength = output.Reserve32();
                long layerInfoStart = stream.Position;
                output.I16((short)-document.Layers.Count); // First merged alpha channel is transparency.
                int count = document.Layers.Count;
                long[,] channelLengthOffsets = new long[count, 4];
                HashSet<int> usedIds = new HashSet<int>();
                for (int i = 0; i < count; i++)
                    if (document.Layers[i].Id > 0) usedIds.Add(document.Layers[i].Id);
                int nextId = 1;
                for (int physicalIndex = 0; physicalIndex < count; physicalIndex++)
                {
                    PsdLayer layer = document.Layers[count - 1 - physicalIndex];
                    int id = layer.Id;
                    if (id == 0)
                    {
                        while (usedIds.Contains(nextId)) nextId++;
                        id = nextId++;
                        usedIds.Add(id);
                    }
                    WriteRecord(output, layer, id, channelLengthOffsets, physicalIndex);
                }
                for (int physicalIndex = 0; physicalIndex < count; physicalIndex++)
                {
                    PsdLayer layer = document.Layers[count - 1 - physicalIndex];
                    for (int channel = 0; channel < 4; channel++)
                    {
                        long start = stream.Position;
                        int component = channel == 0 ? 3 : channel - 1;
                        if (layer.Kind == PsdLayerKind.Pixel)
                            WriteLayerChannel(output, layer.Rgba, layer.Width, layer.Height, component, compression);
                        else output.U16(0); // Empty group channels still have a compression word.
                        output.Patch32(channelLengthOffsets[physicalIndex, channel], stream.Position - start);
                        CheckLength(stream.Position - fileStart);
                    }
                }
                if (((stream.Position - layerInfoStart) & 1) != 0) output.Byte(0);
                output.PatchLength32(layerInfoLength);
                output.U32(0); // Global layer mask info.
                output.PatchLength32(layerMaskLength);
                WriteComposite(output, document, compression);
                CheckLength(stream.Position - fileStart);
            }
            finally
            {
                if (!leaveOpen) stream.Dispose();
            }
        }

        public static void Validate(PsdDocument document, PsdCompression compression = PsdCompression.Rle)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (compression != PsdCompression.Raw && compression != PsdCompression.Rle)
                throw new ArgumentOutOfRangeException("compression");
            PsdRaster.CheckPixels(document.CompositeRgba, document.Width, document.Height);
            HashSet<ushort> resourceIds = new HashSet<ushort>();
            foreach (PsdResource resource in document.SafeResources)
            {
                PsdSafeResources.Validate(resource);
                if (!resourceIds.Add(resource.Id)) throw new ArgumentException("Duplicate safe image resource ID.");
            }
            if (document.Layers.Count < 1 || document.Layers.Count > 32767)
                throw new ArgumentException("PSD requires 1..32767 layer records, including group dividers.");
            int depth = 0;
            HashSet<int> usedIds = new HashSet<int>();
            long minimumSize = 100L + document.CompositeRgba.Length;
            for (int i = 0; i < document.Layers.Count; i++)
            {
                PsdLayer layer = document.Layers[i];
                if (layer == null) throw new ArgumentException("A layer is null.");
                if (layer.Name == null || layer.Name.Length > 32767 || layer.Name.IndexOf('\0') >= 0)
                    throw new ArgumentException("Layer names must be non-null, at most 32767 UTF-16 units, and contain no NUL.");
                // A strict encoder catches malformed isolated surrogates instead of silently replacing them.
                new UnicodeEncoding(true, false, true).GetByteCount(layer.Name);
                if (layer.Id < 0 || (layer.Id > 0 && !usedIds.Add(layer.Id)))
                    throw new ArgumentException("Explicit layer IDs must be unique positive integers.");
                if (layer.Kind == PsdLayerKind.Pixel)
                {
                    PsdRaster.CheckPixels(layer.Rgba, layer.Width, layer.Height);
                    long right = (long)layer.Left + layer.Width, bottom = (long)layer.Top + layer.Height;
                    if (right > Int32.MaxValue || bottom > Int32.MaxValue)
                        throw new ArgumentException("Layer bounds overflow a PSD signed 32-bit coordinate.");
                    minimumSize += 100L + layer.Name.Length * 2L;
                    if (compression == PsdCompression.Raw) minimumSize += layer.Rgba.Length;
                }
                else if (layer.Kind == PsdLayerKind.GroupStart)
                {
                    depth++;
                    if (layer.Opacity != 255) throw new NotSupportedException("Group opacity must be baked into pixels.");
                }
                else if (layer.Kind == PsdLayerKind.GroupEnd)
                {
                    if (--depth < 0) throw new ArgumentException("A group end precedes its group start.");
                }
                else throw new ArgumentException("Unknown layer kind.");
            }
            if (depth != 0) throw new ArgumentException("Unbalanced PSD groups.");
            if (compression == PsdCompression.Raw) CheckLength(minimumSize);
        }

        private static void WriteResources(BigEndian output, IList<PsdResource> resources)
        {
            long length = output.Reserve32();
            bool customResolution = false;
            foreach (PsdResource resource in resources)
                if (resource.Id == 1005) customResolution = true;
            if (!customResolution)
            {
                // ResolutionInfo: 72 pixels/inch, document size in inches.
                output.Ascii("8BIM"); output.U16(1005); output.U16(0); output.U32(16);
                output.U32(72U << 16); output.U16(1); output.U16(1);
                output.U32(72U << 16); output.U16(1); output.U16(1);
            }
            foreach (PsdResource resource in resources)
            {
                output.Ascii("8BIM"); output.U16(resource.Id); output.U16(0);
                output.U32((uint)resource.Data.Length); output.Bytes(resource.Data);
                if ((resource.Data.Length & 1) != 0) output.Byte(0);
            }
            // VersionInfo identifies the merged image as real, rather than a placeholder.
            output.Ascii("8BIM"); output.U16(1057); output.U16(0);
            long versionLength = output.Reserve32();
            output.U32(1);
            output.Byte(1);
            WriteUnicode(output, "PrefabToPsd");
            WriteUnicode(output, "PrefabToPsd");
            output.U32(1);
            long dataSize = output.Position - versionLength - 4;
            output.Patch32(versionLength, dataSize);
            if ((dataSize & 1) != 0) output.Byte(0);
            output.PatchLength32(length);
        }

        private static void WriteRecord(BigEndian output, PsdLayer layer, int id, long[,] offsets, int index)
        {
            bool pixel = layer.Kind == PsdLayerKind.Pixel;
            output.I32(pixel ? layer.Top : 0);
            output.I32(pixel ? layer.Left : 0);
            output.I32(pixel ? layer.Top + layer.Height : 0);
            output.I32(pixel ? layer.Left + layer.Width : 0);
            output.U16(4);
            for (int channel = 0; channel < 4; channel++)
            {
                output.I16((short)(channel == 0 ? -1 : channel - 1));
                offsets[index, channel] = output.Reserve32();
            }
            output.Ascii("8BIM");
            output.Ascii(layer.Kind == PsdLayerKind.GroupStart ? "pass" : "norm");
            output.Byte(layer.Opacity);
            output.Byte(0);
            output.Byte((byte)((layer.Visible ? 0 : 2) | (!pixel ? 24 : 0)));
            output.Byte(0);
            long extraLength = output.Reserve32();
            output.U32(0); // Layer mask.
            output.U32(0); // Blending ranges.
            byte[] legacy = Encoding.ASCII.GetBytes(layer.Name);
            int legacyLength = Math.Min(255, legacy.Length);
            output.Byte((byte)legacyLength);
            output.Bytes(legacy, 0, legacyLength);
            for (int n = (legacyLength + 1) % 4; n != 0 && n < 4; n++) output.Byte(0);

            output.Ascii("8BIM"); output.Ascii("luni");
            long unicodeLength = output.Reserve32();
            WriteUnicode(output, layer.Name);
            output.PatchLength32(unicodeLength);

            output.Ascii("8BIM"); output.Ascii("lyid"); output.U32(4); output.U32((uint)id);
            if (!pixel)
            {
                output.Ascii("8BIM"); output.Ascii("lsct"); output.U32(12);
                output.U32(layer.Kind == PsdLayerKind.GroupStart ? 1U : 3U);
                output.Ascii("8BIM");
                output.Ascii(layer.Kind == PsdLayerKind.GroupStart ? "pass" : "norm");
            }
            output.PatchLength32(extraLength);
        }

        private static void WriteUnicode(BigEndian output, string value)
        {
            output.U32((uint)value.Length); // UTF-16 code units; emoji count as two.
            output.Bytes(Encoding.BigEndianUnicode.GetBytes(value));
        }

        private static void WriteLayerChannel(BigEndian output, byte[] rgba, int width, int height,
            int component, PsdCompression compression)
        {
            output.U16((ushort)compression);
            long counts = output.Position;
            if (compression == PsdCompression.Rle)
                for (int y = 0; y < height; y++) output.U16(0);
            byte[] row = new byte[width];
            for (int y = 0; y < height; y++)
            {
                CopyComponent(rgba, row, y, width, component);
                if (compression == PsdCompression.Raw) output.Bytes(row);
                else
                {
                    byte[] packed = PackBits(row);
                    if (packed.Length > UInt16.MaxValue) throw new InvalidDataException("PSD RLE row exceeds 65535 bytes.");
                    output.Patch16(counts + y * 2L, (ushort)packed.Length);
                    output.Bytes(packed);
                }
            }
        }

        private static void WriteComposite(BigEndian output, PsdDocument document, PsdCompression compression)
        {
            output.U16((ushort)compression);
            long counts = output.Position;
            if (compression == PsdCompression.Rle)
                for (int i = 0; i < document.Height * 4; i++) output.U16(0);
            byte[] row = new byte[document.Width];
            for (int channel = 0; channel < 4; channel++)
            {
                for (int y = 0; y < document.Height; y++)
                {
                    CopyMergedComponent(document.CompositeRgba, row, y, document.Width, channel);
                    if (compression == PsdCompression.Raw) output.Bytes(row);
                    else
                    {
                        byte[] packed = PackBits(row);
                        output.Patch16(counts + (channel * (long)document.Height + y) * 2, (ushort)packed.Length);
                        output.Bytes(packed);
                    }
                }
            }
        }

        private static void CopyComponent(byte[] rgba, byte[] row, int y, int width, int component)
        {
            int offset = y * width * 4 + component;
            for (int x = 0; x < width; x++) row[x] = rgba[offset + x * 4];
        }

        // Photoshop's merged RGB preview uses a white matte, with the original alpha in
        // its fourth channel. Layer RGB channels, unlike the merged preview, stay straight.
        // Storing straight RGB here makes readers that remove that matte darken the preview.
        private static void CopyMergedComponent(byte[] rgba, byte[] row, int y, int width, int component)
        {
            int offset = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int pixel = offset + x * 4;
                int alpha = rgba[pixel + 3];
                row[x] = component == 3 ? (byte)alpha :
                    (byte)((rgba[pixel + component] * alpha + 255 * (255 - alpha) + 127) / 255);
            }
        }

        /// <summary>Encode one scanline. Runs of 3+ bytes use repeat packets; packets never exceed 128 bytes.</summary>
        public static byte[] PackBits(byte[] row)
        {
            if (row == null) throw new ArgumentNullException("row");
            using (MemoryStream result = new MemoryStream())
            {
                int i = 0;
                while (i < row.Length)
                {
                    int run = 1;
                    while (i + run < row.Length && run < 128 && row[i + run] == row[i]) run++;
                    if (run >= 3)
                    {
                        result.WriteByte((byte)(257 - run));
                        result.WriteByte(row[i]);
                        i += run;
                    }
                    else
                    {
                        int start = i;
                        i += run;
                        while (i < row.Length && i - start < 128)
                        {
                            run = 1;
                            while (i + run < row.Length && run < 3 && row[i + run] == row[i]) run++;
                            if (run >= 3) break;
                            i += Math.Min(run, 128 - (i - start));
                        }
                        int count = i - start;
                        result.WriteByte((byte)(count - 1));
                        result.Write(row, start, count);
                    }
                }
                return result.ToArray();
            }
        }

        private static void CheckLength(long length)
        {
            if (length > MaxFileBytes) throw new InvalidDataException("PSD v1 output exceeds 2 GiB. Reduce size or layer count.");
        }

        private sealed class BigEndian
        {
            private readonly Stream stream;
            public long Position { get { return stream.Position; } }
            public BigEndian(Stream value) { stream = value; }
            public void Byte(byte value) { stream.WriteByte(value); }
            public void Bytes(byte[] bytes) { stream.Write(bytes, 0, bytes.Length); }
            public void Bytes(byte[] bytes, int start, int count) { stream.Write(bytes, start, count); }
            public void Ascii(string text) { Bytes(Encoding.ASCII.GetBytes(text)); }
            public void U16(ushort value) { Byte((byte)(value >> 8)); Byte((byte)value); }
            public void I16(short value) { U16(unchecked((ushort)value)); }
            public void U32(uint value)
            {
                Byte((byte)(value >> 24)); Byte((byte)(value >> 16));
                Byte((byte)(value >> 8)); Byte((byte)value);
            }
            public void I32(int value) { U32(unchecked((uint)value)); }
            public long Reserve32() { long at = Position; U32(0); return at; }
            public void Patch16(long offset, ushort value)
            {
                long position = Position;
                stream.Position = offset; U16(value); stream.Position = position;
            }
            public void Patch32(long offset, long value)
            {
                if (value < 0 || value > UInt32.MaxValue) throw new InvalidDataException("PSD section length overflow.");
                long position = Position;
                stream.Position = offset; U32((uint)value); stream.Position = position;
            }
            public void PatchLength32(long offset) { Patch32(offset, Position - offset - 4); }
        }
    }
}
