#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PrefabToPsd
{
    // The sidecar is deliberately independent of the original PSD importer.
    // It describes the source prefab, not Photoshop-native editable text or constraints.
    [Serializable]
    public sealed class PrefabPsdMetadataDocument
    {
        public string schemaVersion = "2.0";
        public string exporter = "PrefabToPsd";
        public string exporterVersion = ExportOptions.Version;
        public string exportedUtc;
        public string unityVersion;
        public string sourceName;
        public string sourceAssetPath;
        public string sourceAssetGuid;
        public string sourceScenePath;
        public int sourceRootSiblingIndex;
        public int width;
        public int height;
        public string colorSpace;
        public bool rasterizedText = true;
        public string referencePath;
        public int referenceMatchedLayers;
        public int referenceReusedLayers;
        public int referenceRestoredIds;
        public bool referenceColorProfileCopied;
        public string coordinateConvention = "PSD layer coordinates: top-left origin, pixels. Source RectTransforms: original Unity local values.";
        public List<string> warnings = new List<string>();
        public List<PrefabPsdNodeMetadata> nodes = new List<PrefabPsdNodeMetadata>();
        public List<PsdLayerMetadata> layers = new List<PsdLayerMetadata>();
    }

    [Serializable]
    public sealed class PsdLayerMetadata
    {
        public int psdLayerId;
        public string hierarchyPath;
        public string name;
        public int left;
        public int top;
        public int width;
        public int height;
        public string referenceName;
        public int referenceId;
        public bool referenceMatched;
        public bool referencePixelsReused;
    }

    [Serializable]
    public sealed class PrefabPsdNodeMetadata
    {
        public string hierarchyPath;
        public string name;
        public int siblingIndex;
        public bool activeSelf;
        public bool activeInHierarchy;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
        public List<string> components = new List<string>();
        public bool hasRectTransform;
        public Vector2 anchorMin;
        public Vector2 anchorMax;
        public Vector2 pivot;
        public Vector2 anchoredPosition;
        public Vector3 anchoredPosition3D;
        public Vector2 sizeDelta;
        public Vector2 offsetMin;
        public Vector2 offsetMax;
        public Rect rect;
        public bool hasGraphic;
        public string graphicType;
        public bool graphicEnabled;
        public Color graphicColor;
        public string materialAssetPath;
        public PrefabPsdTextMetadata text;
        public PrefabPsdImageMetadata image;
        public PrefabPsdRawImageMetadata rawImage;
        public PrefabPsdCanvasMetadata canvas;
        public PrefabPsdCanvasGroupMetadata canvasGroup;
        public PrefabPsdMaskMetadata mask;
        public bool hasRectMask2D;
        public bool rectMask2DEnabled;
        public PrefabPsdOriginalMetadata originalPsd;
    }

    [Serializable]
    public sealed class PrefabPsdTextMetadata
    {
        public string componentType;
        public string content;
        public string fontAssetPath;
        public string fontName;
        public float fontSize;
        public string fontStyle;
        public string alignment;
        public Color color;
        public bool richText;
        public bool bestFit;
        public int resizeTextMinSize;
        public int resizeTextMaxSize;
        public float lineSpacing;
        public string horizontalOverflow;
        public string verticalOverflow;
        public bool isTextMeshPro;
        public bool rasterized = true;
    }

    [Serializable]
    public sealed class PrefabPsdImageMetadata
    {
        public string spriteAssetPath;
        public string spriteAssetGuid;
        public string spriteName;
        public string textureAssetPath;
        public Rect spriteRect;
        public Vector2 spritePivot;
        public Vector4 spriteBorder;
        public float spritePixelsPerUnit;
        public string imageType;
        public bool preserveAspect;
        public bool fillCenter;
        public string fillMethod;
        public int fillOrigin;
        public float fillAmount;
        public bool fillClockwise;
        public Color color;
    }

    [Serializable]
    public sealed class PrefabPsdRawImageMetadata
    {
        public string textureAssetPath;
        public string textureName;
        public Rect uvRect;
        public Color color;
    }

    [Serializable]
    public sealed class PrefabPsdCanvasMetadata
    {
        public bool enabled;
        public string renderMode;
        public int sortingLayerId;
        public int sortingOrder;
        public bool overrideSorting;
        public float referencePixelsPerUnit;
        public float scaleFactor;
        public bool hasScaler;
        public string scaleMode;
        public Vector2 referenceResolution;
        public string screenMatchMode;
        public float matchWidthOrHeight;
    }

    [Serializable]
    public sealed class PrefabPsdCanvasGroupMetadata
    {
        public float alpha;
        public bool ignoreParentGroups;
        public bool interactable;
        public bool blocksRaycasts;
    }

    [Serializable]
    public sealed class PrefabPsdMaskMetadata
    {
        public bool enabled;
        public bool showMaskGraphic;
    }

    [Serializable]
    public sealed class PrefabPsdOriginalMetadata
    {
        public string stableId;
        public string sourceLayerPath;
        public string sourceLayerName;
        public string generatedUiType;
        public string generatedVisualMode;
        public int sourceLayerId = -1;
        public string sourceAssetPath;
        public string sourceHash;
        public Vector2 canvasSize;
        public string generatedUtc;
        public string manifestAssetPath;
        public string generatorVersion;
    }

    internal static class PrefabPsdMetadata
    {
        public static PrefabPsdMetadataDocument Create(
            GameObject source, int width, int height, List<string> warnings)
        {
            if (source == null) throw new ArgumentNullException("source");
            PrefabPsdMetadataDocument document = new PrefabPsdMetadataDocument();
            document.exportedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            document.unityVersion = Application.unityVersion;
            document.sourceName = source.name;
            document.sourceAssetPath = AssetDatabase.GetAssetPath(source);
            document.sourceAssetGuid = string.IsNullOrEmpty(document.sourceAssetPath)
                ? "" : AssetDatabase.AssetPathToGUID(document.sourceAssetPath);
            document.sourceScenePath = source.scene.IsValid() ? source.scene.path : "";
            document.sourceRootSiblingIndex = source.transform.GetSiblingIndex();
            document.width = width;
            document.height = height;
            document.colorSpace = QualitySettings.activeColorSpace.ToString();
            document.warnings = warnings ?? new List<string>();
            foreach (Transform transform in source.GetComponentsInChildren<Transform>(true))
                document.nodes.Add(CreateNode(transform, source.transform, document.warnings));
            return document;
        }

        // Use the same root-relative key for original objects and preview clones.
        // The root index is fixed at zero; the original root index is stored separately.
        public static string HierarchyPath(Transform node, Transform root)
        {
            if (node == null || root == null) return "";
            List<string> segments = new List<string>();
            Transform current = node;
            while (current != null)
            {
                int index = current == root ? 0 : current.GetSiblingIndex();
                segments.Add(EscapePathPart(current.name) + "[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                if (current == root) break;
                current = current.parent;
            }
            segments.Reverse();
            return string.Join("/", segments.ToArray());
        }

        public static void Save(string path, PrefabPsdMetadataDocument document)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Metadata path is required.", "path");
            if (document == null) throw new ArgumentNullException("document");
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, JsonUtility.ToJson(document, true), new UTF8Encoding(false));
                // The temporary file is on the same volume. Do not delete a previous
                // sidecar first: if replacement fails, preserve the previous file.
                if (File.Exists(fullPath))
                    File.Replace(temporary, fullPath, null);
                else
                    File.Move(temporary, fullPath);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static PrefabPsdNodeMetadata CreateNode(Transform transform, Transform root, List<string> warnings)
        {
            PrefabPsdNodeMetadata node = new PrefabPsdNodeMetadata();
            GameObject go = transform.gameObject;
            node.hierarchyPath = HierarchyPath(transform, root);
            node.name = go.name;
            node.siblingIndex = transform.GetSiblingIndex();
            node.activeSelf = go.activeSelf;
            node.activeInHierarchy = go.activeInHierarchy;
            node.localPosition = transform.localPosition;
            node.localRotation = transform.localRotation;
            node.localScale = transform.localScale;
            Component[] components = go.GetComponents<Component>();
            foreach (Component component in components)
                node.components.Add(component == null ? "<Missing script>" : component.GetType().FullName);

            RectTransform rect = transform as RectTransform;
            if (rect != null)
            {
                node.hasRectTransform = true;
                node.anchorMin = rect.anchorMin;
                node.anchorMax = rect.anchorMax;
                node.pivot = rect.pivot;
                node.anchoredPosition = rect.anchoredPosition;
                node.anchoredPosition3D = rect.anchoredPosition3D;
                node.sizeDelta = rect.sizeDelta;
                node.offsetMin = rect.offsetMin;
                node.offsetMax = rect.offsetMax;
                node.rect = rect.rect;
            }

            Graphic graphic = go.GetComponent<Graphic>();
            if (graphic != null)
            {
                node.hasGraphic = true;
                node.graphicType = graphic.GetType().FullName;
                node.graphicEnabled = graphic.enabled;
                node.graphicColor = graphic.color;
                node.materialAssetPath = AssetDatabase.GetAssetPath(graphic.material);
            }
            Text text = go.GetComponent<Text>();
            if (text != null) node.text = CreateText(text);
            Image image = go.GetComponent<Image>();
            if (image != null) node.image = CreateImage(image);
            RawImage raw = go.GetComponent<RawImage>();
            if (raw != null)
            {
                node.rawImage = new PrefabPsdRawImageMetadata();
                node.rawImage.textureAssetPath = AssetDatabase.GetAssetPath(raw.texture);
                node.rawImage.textureName = raw.texture != null ? raw.texture.name : "";
                node.rawImage.uvRect = raw.uvRect;
                node.rawImage.color = raw.color;
            }
            Canvas canvas = go.GetComponent<Canvas>();
            if (canvas != null) node.canvas = CreateCanvas(canvas);
            CanvasGroup group = go.GetComponent<CanvasGroup>();
            if (group != null)
            {
                node.canvasGroup = new PrefabPsdCanvasGroupMetadata();
                node.canvasGroup.alpha = group.alpha;
                node.canvasGroup.ignoreParentGroups = group.ignoreParentGroups;
                node.canvasGroup.interactable = group.interactable;
                node.canvasGroup.blocksRaycasts = group.blocksRaycasts;
            }
            Mask mask = go.GetComponent<Mask>();
            if (mask != null)
            {
                node.mask = new PrefabPsdMaskMetadata();
                node.mask.enabled = mask.enabled;
                node.mask.showMaskGraphic = mask.showMaskGraphic;
            }
            RectMask2D rectMask = go.GetComponent<RectMask2D>();
            node.hasRectMask2D = rectMask != null;
            node.rectMask2DEnabled = rectMask != null && rectMask.enabled;
            foreach (Component component in components)
            {
                if (component == null) continue;
                string type = component.GetType().FullName;
                if (type == "TMPro.TextMeshProUGUI") node.text = CreateTmpText(component, warnings);
                if (type == "Mars.PsdTOUgui.PsdGeneratedElement" || type == "Mars.PsdTOUgui.PsdGeneratedScreen")
                    ReadOriginalMetadata(component, node, warnings);
            }
            return node;
        }

        private static PrefabPsdTextMetadata CreateText(Text text)
        {
            PrefabPsdTextMetadata data = new PrefabPsdTextMetadata();
            data.componentType = text.GetType().FullName;
            data.content = text.text;
            data.fontAssetPath = AssetDatabase.GetAssetPath(text.font);
            data.fontName = text.font != null ? text.font.name : "";
            data.fontSize = text.fontSize;
            data.fontStyle = text.fontStyle.ToString();
            data.alignment = text.alignment.ToString();
            data.color = text.color;
            data.richText = text.supportRichText;
            data.bestFit = text.resizeTextForBestFit;
            data.resizeTextMinSize = text.resizeTextMinSize;
            data.resizeTextMaxSize = text.resizeTextMaxSize;
            data.lineSpacing = text.lineSpacing;
            data.horizontalOverflow = text.horizontalOverflow.ToString();
            data.verticalOverflow = text.verticalOverflow.ToString();
            return data;
        }

        private static PrefabPsdImageMetadata CreateImage(Image image)
        {
            PrefabPsdImageMetadata data = new PrefabPsdImageMetadata();
            Sprite sprite = image.overrideSprite;
            if (sprite == null) sprite = image.sprite;
            if (sprite != null)
            {
                data.spriteAssetPath = AssetDatabase.GetAssetPath(sprite);
                data.spriteAssetGuid = string.IsNullOrEmpty(data.spriteAssetPath) ? "" : AssetDatabase.AssetPathToGUID(data.spriteAssetPath);
                data.spriteName = sprite.name;
                data.textureAssetPath = AssetDatabase.GetAssetPath(sprite.texture);
                data.spriteRect = sprite.rect;
                data.spritePivot = sprite.pivot;
                data.spriteBorder = sprite.border;
                data.spritePixelsPerUnit = sprite.pixelsPerUnit;
            }
            data.imageType = image.type.ToString();
            data.preserveAspect = image.preserveAspect;
            data.fillCenter = image.fillCenter;
            data.fillMethod = image.fillMethod.ToString();
            data.fillOrigin = image.fillOrigin;
            data.fillAmount = image.fillAmount;
            data.fillClockwise = image.fillClockwise;
            data.color = image.color;
            return data;
        }

        private static PrefabPsdCanvasMetadata CreateCanvas(Canvas canvas)
        {
            PrefabPsdCanvasMetadata data = new PrefabPsdCanvasMetadata();
            data.enabled = canvas.enabled;
            data.renderMode = canvas.renderMode.ToString();
            data.sortingLayerId = canvas.sortingLayerID;
            data.sortingOrder = canvas.sortingOrder;
            data.overrideSorting = canvas.overrideSorting;
            data.referencePixelsPerUnit = canvas.referencePixelsPerUnit;
            data.scaleFactor = canvas.scaleFactor;
            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler != null)
            {
                data.hasScaler = true;
                data.scaleMode = scaler.uiScaleMode.ToString();
                data.referenceResolution = scaler.referenceResolution;
                data.screenMatchMode = scaler.screenMatchMode.ToString();
                data.matchWidthOrHeight = scaler.matchWidthOrHeight;
            }
            return data;
        }

        private static PrefabPsdTextMetadata CreateTmpText(Component component, List<string> warnings)
        {
            PrefabPsdTextMetadata data = new PrefabPsdTextMetadata();
            data.componentType = component.GetType().FullName;
            data.isTextMeshPro = true;
            data.content = Read<string>(component, "text", "", warnings);
            UnityEngine.Object font = Read<UnityEngine.Object>(component, "font", null, warnings);
            data.fontAssetPath = AssetDatabase.GetAssetPath(font);
            data.fontName = font != null ? font.name : "";
            data.fontSize = Read<float>(component, "fontSize", 0f, warnings);
            data.color = Read<Color>(component, "color", Color.white, warnings);
            data.richText = Read<bool>(component, "richText", false, warnings);
            data.bestFit = Read<bool>(component, "enableAutoSizing", false, warnings);
            data.alignment = ReadString(component, "alignment", warnings);
            data.fontStyle = ReadString(component, "fontStyle", warnings);
            return data;
        }

        private static void ReadOriginalMetadata(Component component, PrefabPsdNodeMetadata node, List<string> warnings)
        {
            if (node.originalPsd == null) node.originalPsd = new PrefabPsdOriginalMetadata();
            PrefabPsdOriginalMetadata data = node.originalPsd;
            if (component.GetType().FullName == "Mars.PsdTOUgui.PsdGeneratedElement")
            {
                data.stableId = Read<string>(component, "StableId", "", warnings);
                data.sourceLayerPath = Read<string>(component, "SourceLayerPath", "", warnings);
                data.sourceLayerName = Read<string>(component, "SourceLayerName", "", warnings);
                data.generatedUiType = Read<string>(component, "GeneratedUiType", "", warnings);
                data.generatedVisualMode = Read<string>(component, "GeneratedVisualMode", "", warnings);
                data.sourceLayerId = Read<int>(component, "SourceLayerId", -1, warnings);
            }
            else
            {
                data.sourceAssetPath = Read<string>(component, "SourceAssetPath", "", warnings);
                data.sourceHash = Read<string>(component, "SourceHash", "", warnings);
                data.canvasSize = Read<Vector2>(component, "CanvasSize", Vector2.zero, warnings);
                data.generatedUtc = Read<string>(component, "GeneratedUtc", "", warnings);
                data.manifestAssetPath = Read<string>(component, "ManifestAssetPath", "", warnings);
                data.generatorVersion = Read<string>(component, "GeneratorVersion", "", warnings);
            }
        }

        private static T Read<T>(Component component, string property, T fallback, List<string> warnings)
        {
            object value = ReadValue(component, property, warnings);
            return value is T ? (T)value : fallback;
        }

        private static string ReadString(Component component, string property, List<string> warnings)
        {
            object value = ReadValue(component, property, warnings);
            return value != null ? value.ToString() : "";
        }

        private static object ReadValue(Component component, string property, List<string> warnings)
        {
            try
            {
                PropertyInfo info = component.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
                return info != null && info.CanRead && info.GetIndexParameters().Length == 0
                    ? info.GetValue(component, null) : null;
            }
            catch (Exception exception)
            {
                warnings.Add("Metadata property unavailable: " + component.GetType().FullName + "." + property + " (" + exception.GetType().Name + ").");
                return null;
            }
        }

        private static string EscapePathPart(string value)
        {
            return value.Replace("\\", "\\\\").Replace("/", "\\/").Replace("[", "\\[").Replace("]", "\\]");
        }
    }
}
#endif
