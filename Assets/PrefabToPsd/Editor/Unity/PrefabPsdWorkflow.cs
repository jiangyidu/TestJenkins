#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

namespace PrefabToPsd
{
    /// <summary>Presets are applied only when explicitly selected by a caller or the window.</summary>
    public static class PrefabPsdPresets
    {
        public static void Apply(ExportOptions options, ExportPreset preset)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (!Enum.IsDefined(typeof(ExportPreset), preset)) throw new ArgumentOutOfRangeException("preset");
            options.Preset = preset;
            options.PreserveHierarchy = true;
            options.WriteMetadata = true;
            options.VerifyOutput = true;
            options.PreserveEmptyGroups = preset == ExportPreset.RoundTripStructure || preset == ExportPreset.SourceReference;
            options.AddCanvasMarker = preset == ExportPreset.RoundTripStructure;
            options.AutoDetectReferencePsd = true;
            options.PreserveReferenceIds = true;
            options.PreserveReferenceNames = true;
            options.ReuseIdenticalReferencePixels = true;
            options.CopySafeReferenceResources = true;
            // An explicit reference path activates reference processing. Clear it when
            // leaving that preset so an earlier path cannot silently affect a normal export.
            if (preset != ExportPreset.SourceReference) options.ReferencePsdPath = "";
            // Width, height, compression and memory budget are user settings, not preset defaults.
        }
    }

    public sealed class PreflightNode
    {
        public GameObject Source;
        public string Path;
        public string Name;
        public string Type;
        public int Depth;
        public int EstimatedWidth;
        public int EstimatedHeight;
        public bool Visible;
        public bool HasGraphic;
        public bool IsGroup;
    }

    public sealed class PreflightReport
    {
        public int Width;
        public int Height;
        public int NodeCount;
        public int ImageCount;
        public int TextCount;
        public int GroupCount;
        public int HiddenCount;
        public int VisibleGraphicCount;
        public long EstimatedWorkingBytes;
        public readonly List<PreflightNode> Nodes = new List<PreflightNode>();
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// A read-only hierarchy inspection. It does not force Canvas/layout updates, clone
    /// objects, access Graphic.material or change source components. Bounds are estimates
    /// from the existing layout; the isolated export may rebuild at a different size.
    /// </summary>
    public static class PrefabPsdPreflight
    {
        public static PreflightReport Inspect(GameObject source, ExportOptions options)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (!(source.transform is RectTransform)) throw new ArgumentException("The root must have a RectTransform.", "source");
            options = options ?? new ExportOptions();
            PreflightReport report = new PreflightReport();
            PrefabPsdExporter.ResolveCanvasSize(source, out report.Width, out report.Height);
            if (options.Width > 0) report.Width = options.Width;
            if (options.Height > 0) report.Height = options.Height;
            long canvasPixels = Math.Max(1L, (long)report.Width * report.Height);
            report.EstimatedWorkingBytes = SaturatingMultiply(canvasPixels, 132L);
            Transform root = source.transform;
            bool rootContextActive = !source.scene.IsValid() || source.activeInHierarchy;
            InspectNode(root, root, 0, rootContextActive, options, report, canvasPixels);
            if (!rootContextActive)
                report.Warnings.Add("所选场景根节点在层级中未激活；导出入口会拒绝该源，请选择活动的完整 UI 根或 Prefab 资产。");
            report.Warnings.Add("预检只读取现有布局，未执行布局重建；尺寸和内存为估算值，实际导出以指定画布上的副本渲染为准。");
            if (report.VisibleGraphicCount == 0)
                report.Warnings.Add("当前子树没有可导出的活动 Graphic。隐藏节点的结构与文本可保留在 JSON，不会伪造启用后的像素。");
            if (report.HiddenCount > 0)
                report.Warnings.Add("有 " + report.HiddenCount + " 个隐藏/禁用节点。其状态可记录在 JSON；不可见 Graphic 不输出可见像素。");
            if (report.TextCount > 0)
                report.Warnings.Add("文字仍为像素图层，文本和样式保存在 JSON。结构预设将其标记为 .img，避免无 TySh 的 .txt 回导丢失实际内容。");
            if (!options.WriteMetadata && (report.TextCount > 0 || report.HiddenCount > 0 || options.PreserveEmptyGroups))
                report.Warnings.Add("已关闭 JSON：文字原文、隐藏节点和空容器的原始布局将没有完整伴随记录。");
            if (source.transform.parent != null)
                report.Warnings.Add("仅导出所选子树，所选根之外的 CanvasGroup、遮罩及布局上下文不包含在输出中。");
            if (options.MemoryBudgetMB > 0 && report.EstimatedWorkingBytes > options.MemoryBudgetMB * 1024L * 1024L)
                report.Warnings.Add("按当前可见边界估算的工作内存超过预算；请减小画布或提高内存预算。此估算不是进程内存硬上限。");
            return report;
        }

        private static void InspectNode(Transform node, Transform root, int depth, bool ancestorsActive,
            ExportOptions options, PreflightReport report, long canvasPixels)
        {
            GameObject go = node.gameObject;
            bool active = ancestorsActive && go.activeSelf;
            Graphic graphic = go.GetComponent<Graphic>();
            Mask mask = go.GetComponent<Mask>();
            bool graphicVisible = active && graphic != null && graphic.enabled && HasEnabledCanvasChain(node, root);
            if (mask != null && mask.enabled && !mask.showMaskGraphic) graphicVisible = false;
            bool isGroup = node.childCount > 0 || graphic == null;
            PreflightNode item = new PreflightNode();
            item.Source = go;
            item.Path = PrefabPsdMetadata.HierarchyPath(node, root);
            item.Name = PrefabPsdNaming.GetName(node, graphic, isGroup, options, go.name);
            item.Type = graphic == null ? "Group" : graphic.GetType().Name;
            item.Depth = depth;
            item.Visible = graphic == null ? active : graphicVisible;
            item.HasGraphic = graphic != null;
            item.IsGroup = isGroup;
            EstimateBounds(node as RectTransform, root, out item.EstimatedWidth, out item.EstimatedHeight);
            report.Nodes.Add(item);
            report.NodeCount++;
            if (isGroup) report.GroupCount++;
            if (!active || (graphic != null && !graphic.enabled)) report.HiddenCount++;
            if (graphic is Image || graphic is RawImage) report.ImageCount++;
            if (graphic is Text || IsTmpText(graphic)) report.TextCount++;
            if (graphicVisible)
            {
                report.VisibleGraphicCount++;
                long pixels = Math.Min(canvasPixels, Math.Max(1L, (long)item.EstimatedWidth * item.EstimatedHeight));
                report.EstimatedWorkingBytes = SaturatingAdd(report.EstimatedWorkingBytes, SaturatingMultiply(pixels, 4L));
            }
            Canvas canvas = go.GetComponent<Canvas>();
            if (canvas != null && canvas.enabled && node != root && canvas.overrideSorting)
                AddOnce(report.Warnings, "存在覆盖排序的子 Canvas；导出会按绘制排序扁平化分组，三维距离排序不保证可重建。");
            if (graphic != null && !(graphic is Image) && !(graphic is RawImage) && !(graphic is Text))
                AddOnce(report.Warnings, "存在自定义 Graphic 或 TMP。使用 Unity 渲染，但复杂子网格/自定义混合仍需项目实机检查。");
            if (go.GetComponent<Renderer>() != null)
                AddOnce(report.Warnings, "存在非 UGUI Renderer；3D、SpriteRenderer 或粒子不会作为 UGUI 图层导出。");
            for (int i = 0; i < node.childCount; i++)
                InspectNode(node.GetChild(i), root, depth + 1, active, options, report, canvasPixels);
        }

        private static bool HasEnabledCanvasChain(Transform node, Transform root)
        {
            // A prefab with no Canvas gets the exporter's host Canvas. A disabled Canvas
            // in the selected subtree must not be presented as currently visible.
            Transform current = node;
            while (current != null)
            {
                Canvas canvas = current.GetComponent<Canvas>();
                if (canvas != null && !canvas.enabled) return false;
                if (current == root) break;
                current = current.parent;
            }
            return true;
        }

        private static bool IsTmpText(Graphic graphic)
        {
            if (graphic == null) return false;
            string fullName = graphic.GetType().FullName;
            return fullName != null && fullName.StartsWith("TMPro.", StringComparison.Ordinal)
                && fullName.IndexOf("Text", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void EstimateBounds(RectTransform rect, Transform root, out int width, out int height)
        {
            width = height = 0;
            if (rect == null) return;
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 point = root.InverseTransformPoint(corners[i]);
                minX = Mathf.Min(minX, point.x); minY = Mathf.Min(minY, point.y);
                maxX = Mathf.Max(maxX, point.x); maxY = Mathf.Max(maxY, point.y);
            }
            width = SafeCeiling(maxX - minX);
            height = SafeCeiling(maxY - minY);
        }

        private static int SafeCeiling(float value)
        {
            if (float.IsNaN(value) || value <= 0f) return 0;
            if (float.IsInfinity(value) || value >= int.MaxValue) return int.MaxValue;
            return (int)Math.Ceiling(value);
        }

        private static long SaturatingMultiply(long value, long multiplier)
        {
            return value > long.MaxValue / multiplier ? long.MaxValue : value * multiplier;
        }

        private static long SaturatingAdd(long left, long right)
        {
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }

        private static void AddOnce(List<string> list, string value)
        {
            if (!list.Contains(value)) list.Add(value);
        }
    }

    /// <summary>Names describe rasterized results; baked text and slicing never regain unsafe native tags.</summary>
    public static class PrefabPsdNaming
    {
        private static readonly HashSet<string> KnownSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "img", "image", "rimg", "rawimg", "rawimage", "tex", "txt", "text", "label", "msk", "mask",
            "col", "color", "fillcolor", "bg", "background", "panel", "btn", "button", "tg", "toggle",
            "sld", "slider", "inp", "input", "sv", "scroll", "prefab", "mark", "viewport", "content", "group"
        };
        private static readonly Regex LayoutTag = new Regex(
            @"@(?:topleft|topright|bottomleft|bottomright|top|bottom|left|right|center|stretchx|stretchy|full|fullscreen|width|height|slice|border|9slice|image)(?=$|[\s@=:|0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string GetName(Transform node, Graphic graphic, bool isGroup,
            ExportOptions options, string fallbackName)
        {
            string fallback = string.IsNullOrEmpty(fallbackName) ? "Layer" : fallbackName;
            if (options == null || options.Preset == ExportPreset.CurrentAppearance) return fallback;
            if (options.Preset == ExportPreset.SourceReference)
            {
                // Reference identity does not turn a baked bitmap back into native text,
                // a mask or a nine-slice source. Ordinary safe names stay exact for identity
                // matching; only known import directives are neutralized.
                bool ownReferenceBackground = !isGroup && fallback.EndsWith(" [Graphic]", StringComparison.Ordinal);
                string referenceBase = ownReferenceBackground
                    ? fallback.Substring(0, fallback.Length - " [Graphic]".Length) : fallback;
                if (!HasUnsafeReferenceName(referenceBase, isGroup)) return fallback;
                return CleanArtistName(referenceBase) + (isGroup ? ".group" : ownReferenceBackground ? ".bg" : ".img");
            }
            string clean = CleanArtistName(fallback);
            if (options.Preset == ExportPreset.ArtistDelivery) return clean;
            if (options.Preset != ExportPreset.RoundTripStructure) return fallback;
            bool ownBackground = !isGroup && fallback.EndsWith(" [Graphic]", StringComparison.Ordinal);
            if (ownBackground) clean = CleanArtistName(fallback.Substring(0, fallback.Length - " [Graphic]".Length));
            string suffix = "img";
            if (isGroup)
            {
                suffix = "group";
                if (node != null)
                {
                    if (node.GetComponent<Button>() != null) suffix = "btn";
                    else if (node.GetComponent<Toggle>() != null) suffix = "tg";
                    else if (node.GetComponent<Slider>() != null) suffix = "sld";
                    else if (node.GetComponent<InputField>() != null) suffix = "inp";
                    else if (node.GetComponent<ScrollRect>() != null) suffix = "sv";
                }
            }
            else if (ownBackground) suffix = "bg";
            else if (graphic is RawImage) suffix = "rimg";
            // All rendered Text / TMP layers remain .img: there is no Photoshop TySh.
            return clean + "." + suffix;
        }

        private static bool HasUnsafeReferenceName(string name, bool isGroup)
        {
            string value = name.Trim();
            if (value.StartsWith("#", StringComparison.Ordinal) || value.StartsWith("$", StringComparison.Ordinal)
                || LayoutTag.IsMatch(value)) return true;
            int separator = Math.Max(value.LastIndexOf('.'), value.LastIndexOf('_'));
            if (separator < 0 || separator == value.Length - 1) return false;
            string suffix = value.Substring(separator + 1);
            if (!KnownSuffixes.Contains(suffix)) return false;
            if (isGroup)
                return !(suffix.Equals("group", StringComparison.OrdinalIgnoreCase)
                    || suffix.Equals("content", StringComparison.OrdinalIgnoreCase)
                    || suffix.Equals("btn", StringComparison.OrdinalIgnoreCase)
                    || suffix.Equals("button", StringComparison.OrdinalIgnoreCase)
                    || suffix.Equals("tg", StringComparison.OrdinalIgnoreCase)
                    || suffix.Equals("toggle", StringComparison.OrdinalIgnoreCase)
                    || suffix.Equals("sld", StringComparison.OrdinalIgnoreCase)
                    || suffix.Equals("slider", StringComparison.OrdinalIgnoreCase)
                    || suffix.Equals("inp", StringComparison.OrdinalIgnoreCase)
                    || suffix.Equals("input", StringComparison.OrdinalIgnoreCase)
                    || suffix.Equals("sv", StringComparison.OrdinalIgnoreCase)
                    || suffix.Equals("scroll", StringComparison.OrdinalIgnoreCase));
            return !(suffix.Equals("img", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("image", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("rimg", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("rawimg", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("rawimage", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("tex", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("bg", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("background", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("panel", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("mark", StringComparison.OrdinalIgnoreCase));
        }

        public static string CleanArtistName(string value)
        {
            string result = (value ?? string.Empty).Trim();
            while (result.StartsWith("#", StringComparison.Ordinal) || result.StartsWith("$", StringComparison.Ordinal))
                result = result.Substring(1).TrimStart();
            Match tag = LayoutTag.Match(result);
            if (tag.Success) result = result.Substring(0, tag.Index).TrimEnd();
            int dot = result.LastIndexOf('.');
            if (dot > 0 && KnownSuffixes.Contains(result.Substring(dot + 1)))
                result = result.Substring(0, dot).TrimEnd();
            return string.IsNullOrEmpty(result) ? "Layer" : result;
        }

        public static string SafeFileName(string value)
        {
            string name = (value ?? string.Empty).Trim();
            StringBuilder result = new StringBuilder();
            const string invalid = "<>:\"/\\|?*";
            for (int i = 0; i < name.Length && result.Length < 100; i++)
            {
                char c = name[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]) && result.Length < 99)
                    { result.Append(c); result.Append(name[++i]); }
                    else result.Append('_');
                }
                else if (char.IsLowSurrogate(c) || char.IsControl(c) || invalid.IndexOf(c) >= 0) result.Append('_');
                else result.Append(c);
            }
            string safe = result.ToString().TrimEnd(' ', '.');
            if (safe.Length == 0 || safe == "." || safe == "..") safe = "UI";
            string device = safe.Split('.')[0].ToUpperInvariant();
            bool numberedDevice = device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal)
                || device.StartsWith("LPT", StringComparison.Ordinal)) && device[3] >= '1' && device[3] <= '9';
            if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" || numberedDevice) safe = "_" + safe;
            return safe.Normalize(NormalizationForm.FormC);
        }
    }
}
#endif
