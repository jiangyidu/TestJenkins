#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using PrefabToPsd.Core;
using UnityEditor;
using UnityEngine;

namespace PrefabToPsd
{
    public sealed class PrefabPsdWindow : EditorWindow
    {
        private const string SettingsKey = "PrefabToPsd.Integrated.Options";
        [SerializeField] private GameObject source;
        [SerializeField] private ExportOptions options = new ExportOptions();
        [SerializeField] private bool automaticSize = true;
        [SerializeField] private int width = 1920, height = 1080;
        [SerializeField] private bool showReference = true, showPreflight = true, hiddenOnly;
        private Vector2 scroll, treeScroll;
        private string filter = "", result = "";
        private PreflightReport preflight;
        private static readonly string[] PresetNames =
            { "当前外观", "美术交付", "回导结构（像素层）", "原稿参考（严格匹配）" };

        [MenuItem("Tools/Prefab to PSD/Export Window")]
        public static void Open()
        {
            PrefabPsdWindow window = GetWindow<PrefabPsdWindow>("Prefab to PSD 2.0");
            window.minSize = new Vector2(480, 460);
            if (Selection.activeGameObject != null) window.source = Selection.activeGameObject;
            window.RefreshPreview();
        }

        [MenuItem("Assets/Export UGUI Prefab to PSD...", false, 2000)]
        private static void OpenSelected() { Open(); }

        [MenuItem("Assets/Export UGUI Prefab to PSD...", true)]
        private static bool CanOpenSelected() { return SelectedPrefabs().Count > 0; }

        private void OnEnable()
        {
            if (options == null) options = new ExportOptions();
            string saved = EditorPrefs.GetString(SettingsKey, "");
            if (!string.IsNullOrEmpty(saved))
            {
                try
                {
                    ExportOptions restored = new ExportOptions();
                    JsonUtility.FromJsonOverwrite(saved, restored);
                    options = restored;
                }
                catch { options = new ExportOptions(); }
            }
            if (!Enum.IsDefined(typeof(ExportPreset), options.Preset)) options.Preset = ExportPreset.CurrentAppearance;
            options.Progress = null;
            automaticSize = options.Width == 0 && options.Height == 0;
            width = EditorPrefs.GetInt(SettingsKey + ".Width", options.Width > 0 ? options.Width : 1920);
            height = EditorPrefs.GetInt(SettingsKey + ".Height", options.Height > 0 ? options.Height : 1080);
        }

        private void OnDisable() { SaveSettings(); }

        private void SaveSettings()
        {
            if (options == null) return;
            options.Width = automaticSize ? 0 : width;
            options.Height = automaticSize ? 0 : height;
            EditorPrefs.SetString(SettingsKey, JsonUtility.ToJson(options));
            EditorPrefs.SetInt(SettingsKey + ".Width", width);
            EditorPrefs.SetInt(SettingsKey + ".Height", height);
        }

        private bool Busy
        {
            get { return EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling; }
        }

        private void OnGUI()
        {
            if (options == null) options = new ExportOptions();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("UGUI Prefab → PSD  " + ExportOptions.Version, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Unity 渲染像素图层，保留文字和布局 JSON；导出前读回校验临时 PSD。"
                + "\n文字、九宫格、遮罩和效果烘焙为像素，不创建 Photoshop 原生文字或蒙版。", MessageType.Info);
            EditorGUI.BeginChangeCheck();
            source = EditorGUILayout.ObjectField("Prefab / 场景 UI 根节点", source, typeof(GameObject), true) as GameObject;
            if (EditorGUI.EndChangeCheck()) RefreshPreview();
            int preset = EditorGUILayout.Popup("导出预设", (int)options.Preset, PresetNames);
            if (preset != (int)options.Preset)
            {
                PrefabPsdPresets.Apply(options, (ExportPreset)preset);
                SaveSettings();
                RefreshPreview();
            }
            automaticSize = EditorGUILayout.Toggle("自动画布尺寸", automaticSize);
            using (new EditorGUI.DisabledScope(automaticSize))
            {
                width = EditorGUILayout.IntField("宽度 (px)", width);
                height = EditorGUILayout.IntField("高度 (px)", height);
            }
            options.PreserveHierarchy = EditorGUILayout.Toggle("保留文件夹层级", options.PreserveHierarchy);
            options.PreserveEmptyGroups = EditorGUILayout.Toggle("保留空分组", options.PreserveEmptyGroups);
            options.AddCanvasMarker = EditorGUILayout.Toggle("附加隐藏 #Canvas 标记", options.AddCanvasMarker);
            options.WriteMetadata = EditorGUILayout.Toggle("写出文本与布局 JSON", options.WriteMetadata);
            options.VerifyOutput = EditorGUILayout.Toggle("提交前校验 PSD", options.VerifyOutput);
            options.Compression = (PsdCompression)EditorGUILayout.EnumPopup("通道压缩", options.Compression);
            options.MemoryBudgetMB = EditorGUILayout.IntField("工作内存预算 (MB)", options.MemoryBudgetMB);

            showReference = EditorGUILayout.Foldout(showReference, "原稿参考");
            if (showReference)
            {
                EditorGUILayout.BeginHorizontal();
                options.ReferencePsdPath = EditorGUILayout.TextField("参考 PSD", options.ReferencePsdPath ?? "");
                if (GUILayout.Button("选择…", GUILayout.Width(62)))
                {
                    string path = EditorUtility.OpenFilePanel("选择只读参考 PSD", "", "psd");
                    if (!string.IsNullOrEmpty(path)) options.ReferencePsdPath = path;
                }
                EditorGUILayout.EndHorizontal();
                options.AutoDetectReferencePsd = EditorGUILayout.Toggle("参考预设自动查找来源", options.AutoDetectReferencePsd);
                options.PreserveReferenceIds = EditorGUILayout.Toggle("恢复匹配图层 ID", options.PreserveReferenceIds);
                options.PreserveReferenceNames = EditorGUILayout.Toggle("恢复安全来源名称", options.PreserveReferenceNames);
                options.ReuseIdenticalReferencePixels = EditorGUILayout.Toggle("复用完全相同的原稿像素", options.ReuseIdenticalReferencePixels);
                options.CopySafeReferenceResources = EditorGUILayout.Toggle("继承安全分辨率 / sRGB ICC", options.CopySafeReferenceResources);
                EditorGUILayout.HelpBox("当前外观预设不会隐式查找原稿。原稿参考预设或显式路径启用参考。"
                    + "\n保持当前层序；任何字形或像素变化均以当前渲染为准。原稿 PSD 不可作为输出文件。", MessageType.None);
            }

            DrawPreflight();
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(Busy))
            {
                using (new EditorGUI.DisabledScope(source == null))
                    if (GUILayout.Button("导出当前对象…", GUILayout.Height(34))) ExportSingle();
                List<GameObject> selected = SelectedPrefabs();
                using (new EditorGUI.DisabledScope(selected.Count == 0))
                    if (GUILayout.Button("使用当前配置批量导出选中的 " + selected.Count + " 个 Prefab…", GUILayout.Height(30)))
                        ExportBatch(selected);
            }
            if (Busy) EditorGUILayout.HelpBox("请等待编译完成，并退出 Play Mode 后导出。", MessageType.Info);
            if (!string.IsNullOrEmpty(result)) EditorGUILayout.HelpBox(result, MessageType.Info);
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("隐藏 Graphic 的像素不导出；空分组可保留，原状态和布局在 JSON 中。"
                + "\n只选子树不会包含外部祖先的遮罩或 CanvasGroup。副本的编辑器生命周期回调仍可能运行。", MessageType.None);
            using (new EditorGUI.DisabledScope(Busy))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("运行自测")) PrefabPsdSelfTests.RunFromMenu();
                if (GUILayout.Button("创建示例")) PrefabPsdDemo.CreateDemoPrefab();
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawPreflight()
        {
            showPreflight = EditorGUILayout.Foldout(showPreflight, "只读预检与图层定位");
            if (!showPreflight) return;
            if (GUILayout.Button("刷新预检（不重建源对象布局）")) RefreshPreview();
            if (preflight == null) return;
            EditorGUILayout.LabelField(preflight.Width + " × " + preflight.Height + "  | 节点 " + preflight.NodeCount
                + " | 可见 Graphic " + preflight.VisibleGraphicCount + " | 隐藏 " + preflight.HiddenCount);
            EditorGUILayout.LabelField("估算工作缓冲", (preflight.EstimatedWorkingBytes / 1048576.0).ToString("F1") + " MB");
            foreach (string warning in preflight.Warnings) EditorGUILayout.HelpBox(warning, MessageType.Warning);
            filter = EditorGUILayout.TextField("名称 / 路径过滤", filter);
            hiddenOnly = EditorGUILayout.Toggle("只看隐藏节点", hiddenOnly);
            treeScroll = EditorGUILayout.BeginScrollView(treeScroll, GUILayout.Height(180));
            foreach (PreflightNode node in preflight.Nodes)
            {
                if (hiddenOnly && node.Visible) continue;
                if (!string.IsNullOrEmpty(filter) && node.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && node.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(Math.Min(12, node.Depth) * 12);
                GUILayout.Label((node.Visible ? "" : "[隐藏] ") + node.Name + "  (" + node.Type + ")", GUILayout.MinWidth(160));
                if (GUILayout.Button("定位", GUILayout.Width(44)) && node.Source != null)
                {
                    Selection.activeObject = node.Source;
                    EditorGUIUtility.PingObject(node.Source);
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }

        private void RefreshPreview()
        {
            preflight = null;
            if (source == null) return;
            try { preflight = PrefabPsdPreflight.Inspect(source, Snapshot(false)); }
            catch (Exception ex) { result = "预检失败：" + ex.Message; }
        }

        private ExportOptions Snapshot(bool progress)
        {
            ExportOptions copy = options.Clone();
            copy.Width = automaticSize ? 0 : width;
            copy.Height = automaticSize ? 0 : height;
            copy.Progress = null;
            if (progress)
                copy.Progress = delegate(float amount, string message)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Prefab → PSD", message, amount))
                        throw new OperationCanceledException("用户取消。");
                };
            return copy;
        }

        private void ExportSingle()
        {
            string path = EditorUtility.SaveFilePanel("导出 PSD", EditorPrefs.GetString("PrefabToPsd.LastDirectory", ""),
                PrefabPsdNaming.SafeFileName(source.name), "psd");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                SaveSettings();
                ExportReport report = PrefabPsdExporter.Export(source, path, Snapshot(true));
                EditorPrefs.SetString("PrefabToPsd.LastDirectory", Path.GetDirectoryName(path));
                result = Describe(report);
                Debug.Log("[PrefabToPsd] " + result);
                EditorUtility.RevealInFinder(path);
            }
            catch (OperationCanceledException) { result = "已取消；尚未提交的输出不会覆盖旧文件。"; }
            catch (Exception ex) { result = "导出失败：" + ex.Message; Debug.LogException(ex); }
            finally { EditorUtility.ClearProgressBar(); }
        }

        private void ExportBatch(List<GameObject> selected)
        {
            string directory = EditorUtility.OpenFolderPanel("选择批量输出目录", EditorPrefs.GetString("PrefabToPsd.LastDirectory", ""), "");
            if (string.IsNullOrEmpty(directory)) return;
            try
            {
                SaveSettings();
                BatchExportReport batch = PrefabPsdBatchService.Export(selected, directory, Snapshot(true));
                result = "批量完成：成功 " + batch.Results.Count + "，失败 " + batch.Failures.Count
                    + (batch.Cancelled ? "，已取消后续任务。" : "。") + "\n" + directory;
                foreach (BatchExportFailure failure in batch.Failures)
                    result += "\n" + failure.SourceName + "：" + failure.Message;
                Debug.Log("[PrefabToPsd] " + result);
                EditorPrefs.SetString("PrefabToPsd.LastDirectory", directory);
                if (batch.Results.Count > 0) EditorUtility.RevealInFinder(directory);
            }
            catch (Exception ex) { result = "批量导出失败：" + ex.Message; Debug.LogException(ex); }
            finally { EditorUtility.ClearProgressBar(); }
        }

        private static string Describe(ExportReport report)
        {
            string text = report.Width + " × " + report.Height + "，内容像素层 " + report.LayerCount
                + (report.VerificationPassed ? "，提交前校验通过" : "，未启用校验")
                + "\n" + report.PsdPath;
            if (!string.IsNullOrEmpty(report.ReferencePath))
                text += "\n原稿匹配 " + report.ReferenceMatchedLayers + "，复用 " + report.ReferenceReusedLayers;
            foreach (string warning in report.Warnings) text += "\n" + warning;
            return text;
        }

        private static List<GameObject> SelectedPrefabs()
        {
            List<GameObject> result = new List<GameObject>();
            foreach (UnityEngine.Object item in Selection.objects)
            {
                GameObject go = item as GameObject;
                if (go == null) continue;
                string path = AssetDatabase.GetAssetPath(go);
                if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && go.GetComponent<RectTransform>() != null)
                    result.Add(go);
            }
            return result;
        }
    }
}
#endif
