using System;
using System.Collections.Generic;

namespace PrefabToPsd.Core
{
    public enum PsdLayerKind { Pixel, GroupStart, GroupEnd }
    public enum PsdCompression { Raw = 0, Rle = 1 }

    /// <summary>A Photoshop RGB8 document. Pixels are straight (unassociated) RGBA, top row first.</summary>
    public sealed class PsdDocument
    {
        public int Width;
        public int Height;

        /// <summary>
        /// Photoshop panel order: topmost first. A group is GroupStart, its children,
        /// then GroupEnd. The writer converts this to PSD's bottom-to-top physical order.
        /// </summary>
        public readonly List<PsdLayer> Layers = new List<PsdLayer>();
        /// <summary>
        /// Straight RGBA at the API boundary. The writer stores merged RGB over a white
        /// matte for Photoshop preview compatibility; individual layer RGB stays straight.
        /// </summary>
        public byte[] CompositeRgba;
        /// <summary>Validated Resolution/known-sRGB ICC/PixelAspect resources only.</summary>
        public readonly List<PsdResource> SafeResources = new List<PsdResource>();
    }

    public sealed class PsdLayer
    {
        public string Name = "Layer";
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        public byte[] Rgba;
        public PsdLayerKind Kind;
        public bool Visible = true;
        public byte Opacity = 255;

        /// <summary>Positive explicit ID, or zero for a unique automatically assigned ID.</summary>
        public int Id;
    }

    /// <summary>Small, deterministic helpers, deliberately independent of UnityEngine.</summary>
    public static class PsdRaster
    {
        public static int ByteLength(int width, int height)
        {
            if (width < 1 || height < 1 || width > 30000 || height > 30000)
                throw new ArgumentOutOfRangeException("width", "PSD dimensions must be 1..30000.");
            long size = (long)width * height * 4;
            if (size > int.MaxValue)
                throw new ArgumentOutOfRangeException("width", "RGBA buffer exceeds the managed array limit.");
            return (int)size;
        }

        public static void CheckPixels(byte[] rgba, int width, int height)
        {
            int size = ByteLength(width, height);
            if (rgba == null || rgba.Length != size)
                throw new ArgumentException("RGBA buffer must contain exactly width * height * 4 bytes.");
        }

        public static byte[] FlipRows(byte[] rgba, int width, int height)
        {
            CheckPixels(rgba, width, height);
            byte[] result = new byte[rgba.Length];
            int stride = width * 4;
            for (int row = 0; row < height; row++)
                Buffer.BlockCopy(rgba, row * stride, result, (height - 1 - row) * stride, stride);
            return result;
        }

        /// <summary>Crop to pixels whose alpha exceeds the threshold. Empty images become a transparent 1x1 layer.</summary>
        public static PsdLayer Crop(string name, byte[] rgba, int width, int height, byte alphaThreshold = 0)
        {
            CheckPixels(rgba, width, height);
            int left = width, top = height, right = -1, bottom = -1;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (rgba[(y * width + x) * 4 + 3] <= alphaThreshold) continue;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
            if (right < left)
                return new PsdLayer { Name = name, Width = 1, Height = 1, Rgba = new byte[4] };
            int w = right - left + 1, h = bottom - top + 1;
            byte[] result = new byte[w * h * 4];
            for (int row = 0; row < h; row++)
                Buffer.BlockCopy(rgba, ((top + row) * width + left) * 4, result, row * w * 4, w * 4);
            return new PsdLayer { Name = name, Left = left, Top = top, Width = w, Height = h, Rgba = result };
        }

        /// <summary>
        /// Normal source-over in byte/sRGB space, matching this writer's normal pixel layers.
        /// Group visibility is honored. Group opacity is baked by the caller and must be 255.
        /// </summary>
        public static byte[] CompositeLayers(PsdDocument document)
        {
            if (document == null) throw new ArgumentNullException("document");
            byte[] result = new byte[ByteLength(document.Width, document.Height)];
            bool[] visible = new bool[document.Layers.Count];
            Stack<bool> parents = new Stack<bool>();
            bool parentVisible = true;
            for (int i = 0; i < document.Layers.Count; i++)
            {
                PsdLayer layer = document.Layers[i];
                if (layer == null) throw new ArgumentException("A layer is null.");
                if (layer.Kind == PsdLayerKind.GroupStart)
                {
                    if (layer.Opacity != 255) throw new NotSupportedException("Group opacity must be baked into pixels.");
                    parents.Push(parentVisible);
                    parentVisible = parentVisible && layer.Visible;
                }
                else if (layer.Kind == PsdLayerKind.GroupEnd)
                {
                    if (parents.Count == 0) throw new ArgumentException("Unbalanced PSD groups.");
                    parentVisible = parents.Pop();
                }
                else visible[i] = parentVisible && layer.Visible;
            }
            if (parents.Count != 0) throw new ArgumentException("Unbalanced PSD groups.");
            for (int i = document.Layers.Count - 1; i >= 0; i--)
            {
                PsdLayer layer = document.Layers[i];
                if (layer.Kind != PsdLayerKind.Pixel || !visible[i]) continue;
                CheckPixels(layer.Rgba, layer.Width, layer.Height);
                for (int y = 0; y < layer.Height; y++)
                {
                    long dy = (long)layer.Top + y;
                    if (dy < 0 || dy >= document.Height) continue;
                    for (int x = 0; x < layer.Width; x++)
                    {
                        long dx = (long)layer.Left + x;
                        if (dx < 0 || dx >= document.Width) continue;
                        int source = (y * layer.Width + x) * 4;
                        int target = ((int)dy * document.Width + (int)dx) * 4;
                        double sa = layer.Rgba[source + 3] / 255.0 * layer.Opacity / 255.0;
                        double da = result[target + 3] / 255.0;
                        double oa = sa + da * (1 - sa);
                        if (oa <= 0) continue;
                        for (int c = 0; c < 3; c++)
                            result[target + c] = RoundByte((layer.Rgba[source + c] * sa +
                                result[target + c] * da * (1 - sa)) / oa);
                        result[target + 3] = RoundByte(oa * 255);
                    }
                }
            }
            return result;
        }

        private static byte RoundByte(double value)
        {
            return (byte)Math.Max(0, Math.Min(255, (int)Math.Floor(value + 0.5)));
        }
    }
}
