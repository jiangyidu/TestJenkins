using System;
using System.Collections.Generic;
using System.Text;

namespace PrefabToPsd.Core
{
    /// <summary>One unwrapped PSD image-resource payload; the writer owns framing and padding.</summary>
    public sealed class PsdResource
    {
        public ushort Id;
        public byte[] Data;
    }

    /// <summary>
    /// The exporter produces sRGB values. Reference metadata is admitted only when it
    /// cannot reinterpret those pixels as a different working space. Names and ICC
    /// descriptions are not evidence that a profile implements sRGB.
    /// </summary>
    public static class PsdSafeResources
    {
        public const int MaxIccBytes = 4 * 1024 * 1024;

        public static void Validate(PsdResource resource)
        {
            if (resource == null || resource.Data == null)
                throw new ArgumentException("A PSD resource and its payload are required.");
            byte[] data = resource.Data;
            if (resource.Id == 1005)
            {
                if (data.Length != 16 || !ValidResolution(U32(data, 0)) || !ValidResolution(U32(data, 8))
                    || !ResolutionUnit(U16(data, 4)) || !ResolutionUnit(U16(data, 12))
                    || !DimensionUnit(U16(data, 6)) || !DimensionUnit(U16(data, 14)))
                    throw new ArgumentException("Invalid ResolutionInfo resource.");
            }
            else if (resource.Id == 1039)
            {
                if (!IsSrgbIccProfile(data))
                    throw new ArgumentException("Only a verified sRGB matrix/TRC ICC profile can accompany these sRGB pixels.");
            }
            else if (resource.Id == 1064)
            {
                if (data.Length != 12 || U32(data, 0) != 1)
                    throw new ArgumentException("Invalid PixelAspectRatio resource.");
                byte[] scalar = new byte[8];
                Buffer.BlockCopy(data, 4, scalar, 0, 8);
                if (BitConverter.IsLittleEndian) Array.Reverse(scalar);
                double aspect = BitConverter.ToDouble(scalar, 0);
                if (!Finite(aspect) || aspect <= 0 || aspect > 100)
                    throw new ArgumentException("Pixel aspect ratio must be finite and between 0 and 100.");
            }
            else throw new ArgumentException("Only ResolutionInfo, verified sRGB ICC and PixelAspectRatio resources are supported.");
        }

        /// <summary>
        /// Recognizes conservative RGB/XYZ matrix profiles implementing the canonical
        /// sRGB D50 colorants, D50 white point and IEC transfer curve. LUT profiles,
        /// Adobe RGB, malformed files and unknown encodings return false. This is a
        /// compatibility gate, not an ICC color-conversion implementation.
        /// </summary>
        public static bool IsSrgbIccProfile(byte[] profile)
        {
            try
            {
                if (profile == null || profile.Length < 132 || profile.Length > MaxIccBytes
                    || U32(profile, 0) != (uint)profile.Length
                    || Ascii(profile, 36) != "acsp" || Ascii(profile, 16) != "RGB "
                    || Ascii(profile, 20) != "XYZ " || (profile[8] != 2 && profile[8] != 4)
                    || U32(profile, 64) > 3 || !D50(profile, 68)) return false;
                string profileClass = Ascii(profile, 12);
                if (profileClass != "mntr" && profileClass != "scnr" && profileClass != "spac") return false;

                uint count = U32(profile, 128);
                if (count < 7 || count > 4096 || 132L + count * 12L > profile.Length) return false;
                int tableEnd = checked(132 + (int)count * 12);
                Dictionary<string, IccTag> tags = new Dictionary<string, IccTag>(StringComparer.Ordinal);
                List<IccTag> ranges = new List<IccTag>();
                for (int i = 0; i < (int)count; i++)
                {
                    int entry = 132 + i * 12;
                    string signature = Ascii(profile, entry);
                    if (tags.ContainsKey(signature) || signature.StartsWith("A2B", StringComparison.Ordinal)
                        || signature.StartsWith("B2A", StringComparison.Ordinal)
                        || signature.StartsWith("D2B", StringComparison.Ordinal)
                        || signature.StartsWith("B2D", StringComparison.Ordinal)) return false;
                    uint offset = U32(profile, entry + 4), length = U32(profile, entry + 8);
                    if ((offset & 3) != 0 || offset < tableEnd || length < 8
                        || (long)offset + length > profile.Length) return false;
                    IccTag tag = new IccTag((int)offset, (int)length);
                    string type = Ascii(profile, tag.Offset);
                    if (type == "mAB " || type == "mBA " || type == "mft1" || type == "mft2") return false;
                    foreach (IccTag other in ranges)
                    {
                        bool overlaps = tag.Offset < other.Offset + other.Length && other.Offset < tag.Offset + tag.Length;
                        if (overlaps && (tag.Offset != other.Offset || tag.Length != other.Length)) return false;
                    }
                    ranges.Add(tag);
                    tags.Add(signature, tag);
                }
                return MatrixTag(profile, tags, "rXYZ", 0.43604, 0.22249, 0.01392)
                    && MatrixTag(profile, tags, "gXYZ", 0.38511, 0.71691, 0.09706)
                    && MatrixTag(profile, tags, "bXYZ", 0.14305, 0.06060, 0.71391)
                    && MatrixTag(profile, tags, "wtpt", 0.9642, 1.0, 0.8249)
                    && CurveTag(profile, tags, "rTRC") && CurveTag(profile, tags, "gTRC")
                    && CurveTag(profile, tags, "bTRC");
            }
            catch (ArgumentException) { return false; }
            catch (IndexOutOfRangeException) { return false; }
            catch (OverflowException) { return false; }
        }

        private static bool MatrixTag(byte[] profile, Dictionary<string, IccTag> tags,
            string name, double x, double y, double z)
        {
            IccTag tag;
            return tags.TryGetValue(name, out tag) && tag.Length == 20
                && Ascii(profile, tag.Offset) == "XYZ " && U32(profile, tag.Offset + 4) == 0
                && Near(Fixed(profile, tag.Offset + 8), x, 0.002)
                && Near(Fixed(profile, tag.Offset + 12), y, 0.002)
                && Near(Fixed(profile, tag.Offset + 16), z, 0.002);
        }

        private static bool CurveTag(byte[] profile, Dictionary<string, IccTag> tags, string name)
        {
            IccTag tag;
            if (!tags.TryGetValue(name, out tag) || tag.Length < 12 || U32(profile, tag.Offset + 4) != 0) return false;
            string type = Ascii(profile, tag.Offset);
            if (type == "para")
            {
                int function = U16(profile, tag.Offset + 8);
                if (U16(profile, tag.Offset + 10) != 0 || (function != 3 && function != 4)) return false;
                int parameters = function == 3 ? 5 : 7;
                if (tag.Length != 12 + parameters * 4) return false;
                double[] p = new double[7];
                for (int i = 0; i < parameters; i++) p[i] = Fixed(profile, tag.Offset + 12 + i * 4);
                if (p[0] <= 0 || p[1] <= 0 || p[3] < 0 || p[4] < 0 || p[4] > 1) return false;
                for (int i = 0; i <= 512; i++)
                {
                    double x = i / 512.0;
                    double actual = x >= p[4] ? Math.Pow(p[1] * x + p[2], p[0]) + p[5] : p[3] * x + p[6];
                    if (!Near(actual, SrgbToLinear(x), 0.003)) return false;
                }
                return true;
            }
            if (type == "curv")
            {
                uint count = U32(profile, tag.Offset + 8);
                // Gamma-only and identity curves are not the piecewise sRGB transfer.
                if (count < 2 || count > 65536 || 12L + count * 2L > tag.Length
                    || tag.Length - (12L + count * 2L) > 3) return false;
                int last = -1;
                for (int i = 0; i < (int)count; i++)
                {
                    int value = U16(profile, tag.Offset + 12 + i * 2);
                    if (value < last || !Near(value / 65535.0, SrgbToLinear(i / (count - 1.0)), 0.003)) return false;
                    last = value;
                }
                // A coarse two-point curve passes endpoint checks but is not sRGB.
                for (int i = 0; i <= 512; i++)
                {
                    double x = i / 512.0, table = x * (count - 1);
                    int lower = Math.Min((int)table, (int)count - 2);
                    double fraction = table - lower;
                    double a = U16(profile, tag.Offset + 12 + lower * 2) / 65535.0;
                    double b = U16(profile, tag.Offset + 14 + lower * 2) / 65535.0;
                    if (!Near(a + (b - a) * fraction, SrgbToLinear(x), 0.003)) return false;
                }
                return true;
            }
            return false;
        }

        private static bool D50(byte[] bytes, int offset)
        {
            return Near(Fixed(bytes, offset), 0.9642, 0.002)
                && Near(Fixed(bytes, offset + 4), 1.0, 0.002)
                && Near(Fixed(bytes, offset + 8), 0.8249, 0.002);
        }
        private static double SrgbToLinear(double x) { return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        private static bool Near(double a, double b, double tolerance) { return Finite(a) && Math.Abs(a - b) <= tolerance; }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static bool ValidResolution(uint value) { return value > 0 && value <= 0xFFFF0000U; }
        private static bool ResolutionUnit(int value) { return value == 1 || value == 2; }
        private static bool DimensionUnit(int value) { return value >= 1 && value <= 5; }
        private static double Fixed(byte[] data, int offset) { return unchecked((int)U32(data, offset)) / 65536.0; }
        private static string Ascii(byte[] data, int offset) { return Encoding.ASCII.GetString(data, offset, 4); }
        private static int U16(byte[] data, int offset) { return (data[offset] << 8) | data[offset + 1]; }
        private static uint U32(byte[] data, int offset) { return ((uint)U16(data, offset) << 16) | (uint)U16(data, offset + 2); }
        private struct IccTag
        {
            public readonly int Offset, Length;
            public IccTag(int offset, int length) { Offset = offset; Length = length; }
        }
    }
}
