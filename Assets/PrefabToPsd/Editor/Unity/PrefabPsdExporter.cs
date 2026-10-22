#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using PrefabToPsd.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;
#if UNITY_2019_1_OR_NEWER
using CaptureRenderContext = UnityEngine.Rendering.ScriptableRenderContext;
#else
using CaptureRenderContext = UnityEngine.Experimental.Rendering.ScriptableRenderContext;
#endif

namespace PrefabToPsd
{
    /// <summary>
    /// Captures each active UGUI Graphic in an isolated preview scene and writes a layered PSD.
    /// Images, masks, text, effects and group alpha are rasterized by Unity itself.
    /// This does not execute Play Mode or reconstruct Photoshop text/vector documents.
    /// </summary>
    public static class PrefabPsdExporter
    {
        private const long MaxCanvasPixels = 16777216L;
        // Conservative working-set estimate: floating RT + CPU/GPU readback, two
        // float pixel arrays, byte conversion/crop buffers and allocation overhead.
        private const long CaptureBytesPerPixel = 128L;

        public static ExportReport Export(GameObject source, string psdPath, ExportOptions options)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (!(source.transform is RectTransform))
                throw new ArgumentException("The selected root must have a RectTransform (UGUI prefab or UI scene object).");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before exporting.");
            if (source.scene.IsValid() && !source.activeInHierarchy)
                throw new InvalidOperationException("The selected scene UI is inactive. Select an active UI root or a prefab asset.");
            if (string.IsNullOrEmpty(psdPath)) throw new ArgumentException("A PSD output path is required.");
            if (!string.Equals(Path.GetExtension(psdPath), ".psd", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output file must use the .psd extension.");
            options = (options ?? new ExportOptions()).Clone();
            if (!Enum.IsDefined(typeof(ExportPreset), options.Preset))
                throw new ArgumentException("Unknown export preset.");
            if (options.Compression != PsdCompression.Raw && options.Compression != PsdCompression.Rle)
                throw new ArgumentException("Unsupported PSD compression.");
            int width, height;
            ResolveCanvasSize(source, out width, out height);
            if (options.Width < 0 || options.Height < 0)
                throw new ArgumentException("Canvas width and height must be positive, or zero for automatic sizing.");
            if (options.Width > 0) width = options.Width;
            if (options.Height > 0) height = options.Height;
            ValidateSize(width, height, options.MemoryBudgetMB);
            RenderTextureFormat captureFormat = ResolveCaptureFormat();

            ExportReport report = new ExportReport();
            report.Width = width;
            report.Height = height;
            if (captureFormat == RenderTextureFormat.ARGBHalf)
                report.Warnings.Add("ARGBFloat rendering with alpha blending is unavailable on this device. Capture uses blend-capable ARGBHalf with floating-point readback.");
            report.PsdPath = Path.GetFullPath(psdPath);
            if (options.PreserveEmptyGroups && !options.PreserveHierarchy)
                report.Warnings.Add("Empty group retention requires hierarchy; the current flat export omits empty folders. Source layout remains in JSON when enabled.");
            report.MetadataPath = options.WriteMetadata ? Path.ChangeExtension(report.PsdPath, ".prefab.json") : null;
            report.Warnings.Add("Text is rasterized. Text strings, font references and layout data are preserved in the optional JSON sidecar.");
            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                report.Warnings.Add("This project renders in Linear color space. The merged PSD image is captured from Unity; Photoshop's default sRGB layer blending can differ when translucent layers overlap. Use Photoshop linear RGB blending when comparing layer recomposition.");
            if (source.transform.parent != null)
                report.Warnings.Add("Only the selected subtree is exported. Ancestor masks, CanvasGroups and external layout context are not included; select the complete UI root to retain them.");
            string referencePath = PrefabPsdReference.ResolvePath(source, options, report.Warnings);
            if (!string.IsNullOrEmpty(referencePath) && string.Equals(Path.GetFullPath(referencePath),
                report.PsdPath, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Output cannot overwrite the reference PSD. Choose a different file.");
            ReportProgress(options, 0f, "Preparing isolated UI snapshot");
            PrefabPsdMetadataDocument metadata = PrefabPsdMetadata.Create(source, width, height, report.Warnings);

            using (CaptureSession session = new CaptureSession(source, width, height, report,
                captureFormat, options.CollectCaptureDiagnostics))
            {
                session.Prepare();
                List<Graphic> graphics = session.GetVisibleGraphics();
                if (graphics.Count == 0)
                    throw new InvalidOperationException("No active, enabled UGUI Graphic was found. Inactive objects and disabled graphics are intentionally excluded.");
                bool keepHierarchy = options.PreserveHierarchy && !session.HasNestedSortingOverride();
                if (options.PreserveHierarchy && !keepHierarchy)
                    report.Warnings.Add("A nested Canvas overrides sorting. PSD folders were flattened using sorting layer/order and rendered depth; original hierarchy remains in JSON. Canvas distance-based 3D ordering is not supported.");

                PsdDocument document = new PsdDocument();
                document.Width = width;
                document.Height = height;
                ReportProgress(options, 0.04f, "Capturing complete UI");
                document.CompositeRgba = session.CaptureRgba();
                session.RememberRenderedDepths();
                if (!HasNonTransparentPixels(document.CompositeRgba))
                    throw new InvalidOperationException("The source UI capture is fully transparent; no PSD was written or replaced. " +
                        "The UI calibration passed. Check source Canvas/CanvasGroup alpha, masks, materials and layout. " +
                        string.Join(" | ", report.CaptureDiagnostics.ToArray()));
                Dictionary<Graphic, PsdLayer> layers = new Dictionary<Graphic, PsdLayer>();
                long retainedBytes = document.CompositeRgba.LongLength;
                long budget = options.MemoryBudgetMB * 1024L * 1024L;
                int nextLayerId = 1;
                for (int index = 0; index < graphics.Count; index++)
                {
                    Graphic graphic = graphics[index];
                    ReportProgress(options, 0.08f + 0.82f * index / graphics.Count, "Capturing " + graphic.name);
                    session.Isolate(graphic);
                    byte[] rgba = session.CaptureRgba();
                    string layerName = PrefabPsdNaming.GetName(graphic.transform, graphic, false, options, graphic.name);
                    PsdLayer layer = PsdRaster.Crop(layerName, rgba, width, height);
                    layer.Id = nextLayerId++;
                    layers.Add(graphic, layer);
                    retainedBytes += layer.Rgba.LongLength;
                    if (retainedBytes + width * (long)height * CaptureBytesPerPixel > budget)
                        throw new InvalidOperationException("Layer pixel data exceeds the configured memory budget. Reduce the output size or increase MemoryBudgetMB.");
                    PsdLayerMetadata layerMetadata = new PsdLayerMetadata();
                    layerMetadata.psdLayerId = layer.Id;
                    layerMetadata.hierarchyPath = PrefabPsdMetadata.HierarchyPath(graphic.transform, session.Root.transform);
                    layerMetadata.name = layer.Name;
                    layerMetadata.left = layer.Left;
                    layerMetadata.top = layer.Top;
                    layerMetadata.width = layer.Width;
                    layerMetadata.height = layer.Height;
                    metadata.layers.Add(layerMetadata);
                }
                if (keepHierarchy)
                    AddHierarchy(session.Root.transform, layers, document.Layers, ref nextLayerId, options);
                else
                {
                    graphics.Sort(session.CompareGraphics);
                    for (int index = graphics.Count - 1; index >= 0; index--)
                        document.Layers.Add(layers[graphics[index]]);
                }
                if (options.AddCanvasMarker)
                {
                    long markerBytes = (long)width * height * 4L;
                    if (retainedBytes + markerBytes + width * (long)height * CaptureBytesPerPixel > budget)
                        throw new InvalidOperationException("Canvas marker exceeds the memory budget. Disable it or increase the budget.");
                    document.Layers.Add(new PsdLayer { Name = "#Canvas", Id = nextLayerId++,
                        Width = width, Height = height, Rgba = new byte[checked(width * height * 4)], Visible = false });
                }
                report.LayerCount = layers.Count;
                ReportProgress(options, 0.91f, "Checking reference identity and exact pixels");
                PrefabPsdReference.Apply(document, metadata, options, report, referencePath);
                // Folder construction may qualify a parent's own Graphic name.
                // Keep the JSON name identical to its final PSD record, keyed by ID.
                Dictionary<int, string> finalNames = new Dictionary<int, string>();
                foreach (PsdLayer layer in document.Layers)
                    if (layer.Kind == PsdLayerKind.Pixel) finalNames[layer.Id] = layer.Name;
                foreach (PsdLayerMetadata layer in metadata.layers)
                    layer.name = finalNames[layer.psdLayerId];
                ReportProgress(options, 0.94f, "Writing layered PSD");
                Action<string> verify = null;
                if (options.VerifyOutput)
                    verify = delegate(string temporary)
                    {
                        ReportProgress(options, 0.98f, "Verifying saved layers before commit");
                        PsdOutputVerifier.Verify(temporary, document);
                    };
                PsdWriter.Write(report.PsdPath, document, options.Compression, verify);
                report.VerificationPassed = options.VerifyOutput;
                // The PSD is committed before the sidecar. If the sidecar cannot be written,
                // preserve the valid PSD and report the issue instead of reporting export failure.
                if (options.WriteMetadata)
                {
                    try { PrefabPsdMetadata.Save(report.MetadataPath, metadata); }
                    catch (Exception ex)
                    {
                        report.Warnings.Add("PSD was saved, but JSON sidecar writing failed: " + ex.Message);
                        report.MetadataPath = null;
                    }
                }
            }
            // Do not invoke cancellable progress after output has been committed.
            return report;
        }

        private static RenderTextureFormat ResolveCaptureFormat()
        {
            if (!SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
                throw new NotSupportedException("The graphics device cannot read back floating-point RGBA. Use a graphics-enabled Unity Editor (do not pass -nographics).");
            // A renderable format is not necessarily blendable on Direct3D devices.
            // UI/Default needs alpha blending for both black/white recovery passes.
            if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) &&
                SystemInfo.SupportsBlendingOnRenderTextureFormat(RenderTextureFormat.ARGBFloat))
                return RenderTextureFormat.ARGBFloat;
            if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) &&
                SystemInfo.SupportsBlendingOnRenderTextureFormat(RenderTextureFormat.ARGBHalf))
                return RenderTextureFormat.ARGBHalf;
            throw new NotSupportedException("The graphics device does not support floating-point render targets with alpha blending (ARGBFloat/ARGBHalf). Use a compatible graphics-enabled Unity Editor.");
        }

        private static bool HasNonTransparentPixels(byte[] rgba)
        {
            for (int index = 3; index < rgba.Length; index += 4)
                if (rgba[index] != 0) return true;
            return false;
        }

        // A half-transparent red control detects absent geometry, lost blending,
        // double emission and incorrect readback without relying on source content.
        internal static bool CheckCaptureControl(byte[] rgba, int width, int height, out string detail)
        {
            if (width < 1 || height < 1 || rgba == null || rgba.LongLength != (long)width * height * 4L)
            {
                detail = "Invalid control image dimensions.";
                return false;
            }
            int[] xs = { 0, width / 2, width - 1 };
            int[] ys = { 0, height / 2, height - 1 };
            foreach (int y in ys)
                foreach (int x in xs)
                {
                    int pixel = (y * width + x) * 4;
                    if (Math.Abs(rgba[pixel] - 255) > 4 || rgba[pixel + 1] > 4 ||
                        rgba[pixel + 2] > 4 || Math.Abs(rgba[pixel + 3] - 128) > 4)
                    {
                        detail = "Control at (" + x + "," + y + ") expected RGBA=(255,0,0,128), got (" +
                            rgba[pixel] + "," + rgba[pixel + 1] + "," + rgba[pixel + 2] + "," + rgba[pixel + 3] + ").";
                        return false;
                    }
                }
            detail = "Half-transparent red UI control passed.";
            return true;
        }

        public static void ResolveCanvasSize(GameObject source, out int width, out int height)
        {
            width = 1920;
            height = 1080;
            if (source == null) return;
            // The supplied PSD->UGUI importer records the original design canvas size.
            Component[] components = source.GetComponents<Component>();
            foreach (Component component in components)
            {
                if (component == null || component.GetType().FullName != "Mars.PsdTOUgui.PsdGeneratedScreen") continue;
                PropertyInfo property = component.GetType().GetProperty("CanvasSize", BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.PropertyType == typeof(Vector2))
                {
                    Vector2 value = (Vector2)property.GetValue(component, null);
                    if (value.x > 0f && value.y > 0f)
                    {
                        width = Mathf.RoundToInt(value.x);
                        height = Mathf.RoundToInt(value.y);
                        return;
                    }
                }
            }
            CanvasScaler scaler = source.GetComponent<CanvasScaler>();
            if (scaler != null && scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize &&
                scaler.referenceResolution.x > 0f && scaler.referenceResolution.y > 0f)
            {
                width = Mathf.RoundToInt(scaler.referenceResolution.x);
                height = Mathf.RoundToInt(scaler.referenceResolution.y);
                return;
            }
            RectTransform rect = source.transform as RectTransform;
            if (rect != null && rect.rect.width > 0f && rect.rect.height > 0f)
            {
                width = Mathf.RoundToInt(rect.rect.width);
                height = Mathf.RoundToInt(rect.rect.height);
            }
        }

        private static void ValidateSize(int width, int height, int memoryBudgetMB)
        {
            if (memoryBudgetMB < 16 || memoryBudgetMB > 4096)
                throw new ArgumentOutOfRangeException("memoryBudgetMB", "Memory budget must be between 16 and 4096 MB.");
            if (width < 1 || height < 1 || width > 30000 || height > 30000)
                throw new ArgumentOutOfRangeException("width", "PSD dimensions must be between 1 and 30000.");
            if (width > SystemInfo.maxTextureSize || height > SystemInfo.maxTextureSize)
                throw new ArgumentException("Canvas dimensions exceed the current graphics device's maximum texture size.");
            long pixels = width * (long)height;
            if (pixels > MaxCanvasPixels)
                throw new ArgumentException("This exporter limits the canvas to 16,777,216 pixels. Reduce the output dimensions.");
            if (pixels * (CaptureBytesPerPixel + 4L) > memoryBudgetMB * 1024L * 1024L)
                throw new ArgumentException("The canvas alone exceeds the capture memory budget. Reduce dimensions or raise MemoryBudgetMB.");
        }

        private static void ReportProgress(ExportOptions options, float amount, string message)
        {
            if (options.Progress != null) options.Progress(amount, message);
        }

        // Unity hierarchy renders parent graphics first, followed by children in sibling order.
        // The public writer model uses PSD panel order (top first), hence reverse child traversal.
        private static bool AddHierarchy(Transform node, Dictionary<Graphic, PsdLayer> layers,
            List<PsdLayer> output, ref int nextId, ExportOptions options)
        {
            List<PsdLayer> content = new List<PsdLayer>();
            for (int child = node.childCount - 1; child >= 0; child--)
                AddHierarchy(node.GetChild(child), layers, content, ref nextId, options);
            Graphic ownGraphic = node.GetComponent<Graphic>();
            PsdLayer ownLayer;
            bool hasOwnLayer = ownGraphic != null && layers.TryGetValue(ownGraphic, out ownLayer);
            if (hasOwnLayer)
            {
                ownLayer = layers[ownGraphic];
                if (content.Count > 0)
                    ownLayer.Name = PrefabPsdNaming.GetName(node, ownGraphic, false, options, node.name + " [Graphic]");
                content.Add(ownLayer);
            }
            if (content.Count == 0 && !options.PreserveEmptyGroups) return false;
            if (node.childCount == 0 && hasOwnLayer)
            {
                output.AddRange(content);
                return true;
            }
            PsdLayer start = new PsdLayer();
            start.Name = PrefabPsdNaming.GetName(node, null, true, options, node.name);
            start.Kind = PsdLayerKind.GroupStart;
            start.Visible = node.gameObject.activeInHierarchy;
            start.Id = nextId++;
            output.Add(start);
            output.AddRange(content);
            PsdLayer end = new PsdLayer();
            end.Name = "</" + node.name + ">";
            end.Kind = PsdLayerKind.GroupEnd;
            end.Id = nextId++;
            output.Add(end);
            return true;
        }

        private sealed class CaptureSession : IDisposable
        {
            private readonly int width;
            private readonly int height;
            private readonly ExportReport report;
            private readonly GameObject source;
            private readonly RenderTextureFormat captureFormat;
            private readonly bool collectDiagnostics;
            private bool emitUiGeometry;
            private Scene scene;
            private GameObject host;
            private Camera camera;
            private RenderTexture target;
            private Texture2D readback;
            private Graphic[] allGraphics;
            private bool[] graphicEnabled;
            private Mask[] masks;
            private bool[] maskShowsGraphic;
            private bool[] maskEnabled;
            private readonly Dictionary<Graphic, int> originalDepths = new Dictionary<Graphic, int>();
            private readonly Dictionary<Graphic, int> originalIndices = new Dictionary<Graphic, int>();
            private bool disposed;
            public GameObject Root { get; private set; }

            public CaptureSession(GameObject source, int width, int height, ExportReport report,
                RenderTextureFormat captureFormat, bool collectDiagnostics)
            {
                this.source = source;
                this.width = width;
                this.height = height;
                this.report = report;
                this.captureFormat = captureFormat;
                this.collectDiagnostics = collectDiagnostics;
            }

            public void Prepare()
            {
                scene = EditorSceneManager.NewPreviewScene();
                host = new GameObject("PrefabToPsd Preview", typeof(RectTransform), typeof(Canvas));
                host.hideFlags = HideFlags.HideAndDontSave;
                host.SetActive(false);
                SceneManager.MoveGameObjectToScene(host, scene);
                Canvas hostCanvas = host.GetComponent<Canvas>();
                hostCanvas.renderMode = RenderMode.WorldSpace;
                hostCanvas.pixelPerfect = false;
                Canvas sourceCanvas = source.GetComponentInParent<Canvas>();
                hostCanvas.referencePixelsPerUnit = sourceCanvas != null ? sourceCanvas.referencePixelsPerUnit : 100f;
                RectTransform hostRect = (RectTransform)host.transform;
                hostRect.sizeDelta = new Vector2(width, height);
                hostRect.position = Vector3.zero;
                Root = Object.Instantiate(source, host.transform, false);
                Root.name = source.name;
                foreach (Transform transform in Root.GetComponentsInChildren<Transform>(true))
                    transform.gameObject.hideFlags = HideFlags.HideAndDontSave;

                RectTransform rootRect = (RectTransform)Root.transform;
                rootRect.anchorMin = rootRect.anchorMax = new Vector2(0.5f, 0.5f);
                rootRect.localScale = Vector3.one;
                rootRect.localRotation = Quaternion.identity;
                rootRect.sizeDelta = new Vector2(width, height);
                rootRect.anchoredPosition3D = new Vector3((rootRect.pivot.x - 0.5f) * width,
                    (rootRect.pivot.y - 0.5f) * height, 0f);
                Canvas[] sourceCanvases = Root.GetComponentsInChildren<Canvas>(true);
                float[] referencePixels = new float[sourceCanvases.Length];
                for (int index = 0; index < sourceCanvases.Length; index++)
                    referencePixels[index] = sourceCanvases[index].referencePixelsPerUnit;
                DisableDynamicBehaviours();
                for (int index = 0; index < sourceCanvases.Length; index++)
                {
                    Canvas canvas = sourceCanvases[index];
                    canvas.renderMode = RenderMode.WorldSpace;
                    canvas.pixelPerfect = false;
                    // CanvasScaler.OnDisable resets referencePixelsPerUnit to 100.
                    // Restore each original Canvas value so sliced/tiled sprites retain their borders.
                    canvas.referencePixelsPerUnit = referencePixels[index];
                    canvas.scaleFactor = 1f;
                }
                GameObject cameraObject = new GameObject("PrefabToPsd Camera", typeof(Camera));
                cameraObject.hideFlags = HideFlags.HideAndDontSave;
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                camera = cameraObject.GetComponent<Camera>();
                camera.scene = scene;
                camera.enabled = false;
                // Use the ordinary UGUI camera path even though the objects live in
                // an isolated preview scene. Preview cameras can omit UI geometry.
                camera.cameraType = CameraType.Game;
                camera.transform.position = new Vector3(0f, 0f, -10000f);
                camera.transform.rotation = Quaternion.identity;
                camera.orthographic = true;
                camera.orthographicSize = height * 0.5f;
                camera.aspect = width / (float)height;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 20000f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.renderingPath = RenderingPath.Forward;
                camera.useOcclusionCulling = false;
                camera.allowHDR = true;
                camera.allowMSAA = false;
                camera.cullingMask = -1;
                hostCanvas.worldCamera = camera;
                foreach (Canvas canvas in Root.GetComponentsInChildren<Canvas>(true))
                    canvas.worldCamera = camera;
                target = new RenderTexture(width, height, 24, captureFormat, RenderTextureReadWrite.Linear);
                target.hideFlags = HideFlags.HideAndDontSave;
                target.antiAliasing = 1;
                target.useMipMap = false;
                target.filterMode = FilterMode.Point;
                if (!target.Create()) throw new InvalidOperationException("Unity could not allocate the capture RenderTexture.");
                camera.targetTexture = target;
                readback = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
                readback.hideFlags = HideFlags.HideAndDontSave;
                host.SetActive(true);
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(rootRect);
                Canvas.ForceUpdateCanvases();
                FreezeLayouts();
                allGraphics = Root.GetComponentsInChildren<Graphic>(true);
                graphicEnabled = new bool[allGraphics.Length];
                for (int index = 0; index < allGraphics.Length; index++)
                {
                    Graphic graphic = allGraphics[index];
                    graphicEnabled[index] = graphic.enabled;
                    originalDepths[graphic] = graphic.canvasRenderer.absoluteDepth;
                    originalIndices[graphic] = index;
                    if (graphic.material != null && graphic.material != graphic.defaultMaterial)
                        WarnOnce("Custom UI materials are captured as pixels. Non-standard/additive/destination-dependent blending may not be representable as a normal PSD layer.");
                    if (!(graphic is Image) && !(graphic is RawImage) && !(graphic is Text))
                        WarnOnce("Custom Graphic / TextMeshPro components are captured through their Unity renderer but have not been certified by the built-in standard UGUI tests.");
                }
                masks = Root.GetComponentsInChildren<Mask>(true);
                maskShowsGraphic = new bool[masks.Length];
                maskEnabled = new bool[masks.Length];
                for (int index = 0; index < masks.Length; index++)
                {
                    maskShowsGraphic[index] = masks[index].showMaskGraphic;
                    maskEnabled[index] = masks[index].enabled && masks[index].gameObject.activeInHierarchy &&
                        masks[index].graphic != null && masks[index].graphic.enabled;
                }
                ValidateCapturePipeline();
            }

            private void ValidateCapturePipeline()
            {
                bool rootWasActive = Root.activeSelf;
                GameObject control = null;
                List<string> attempts = new List<string>();
                try
                {
                    Root.SetActive(false);
                    control = new GameObject("PrefabToPsd Capture Control", typeof(RectTransform), typeof(Image));
                    control.hideFlags = HideFlags.HideAndDontSave;
                    RectTransform rect = (RectTransform)control.transform;
                    rect.SetParent(host.transform, false);
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.offsetMin = rect.offsetMax = Vector2.zero;
                    Image image = control.GetComponent<Image>();
                    image.raycastTarget = false;
                    image.color = new Color(1f, 0f, 0f, 0.5f);
                    CameraType[] types = { CameraType.Game, CameraType.SceneView, CameraType.SceneView, CameraType.Preview };
                    for (int index = 0; index < types.Length; index++)
                    {
                        camera.cameraType = types[index];
                        emitUiGeometry = index >= 2;
                        control.SetActive(true);
                        image.SetAllDirty();
                        Canvas.ForceUpdateCanvases();
                        string detail;
                        bool valid = CheckCaptureControl(CaptureRgba(), width, height, out detail);
                        string attempt = types[index] + (emitUiGeometry ? " + explicit UI geometry: " : ": ") + detail;
                        attempts.Add(attempt);
                        report.CaptureDiagnostics.Add(attempt);
                        if (!valid) continue;
                        // Explicit emission can persist in a native camera cache. A
                        // negative control ensures the marker cannot leak into the PSD.
                        control.SetActive(false);
                        Canvas.ForceUpdateCanvases();
                        if (HasNonTransparentPixels(CaptureRgba()))
                        {
                            string residue = types[index] + ": hidden UI control still produced pixels.";
                            attempts.Add(residue);
                            report.CaptureDiagnostics.Add(residue);
                            continue;
                        }
                        if (index != 0)
                            WarnOnce("The ordinary UI camera did not pass calibration. Capture uses a calibrated " + types[index] +
                                " camera" + (emitUiGeometry ? " with explicit UI geometry emission." : "."));
                        return;
                    }
                    throw new InvalidOperationException("UI capture calibration failed before PSD writing. " +
                        "Device=" + SystemInfo.graphicsDeviceName + ", API=" + SystemInfo.graphicsDeviceType +
                        ", RT=" + target.format + ". " + string.Join(" | ", attempts.ToArray()) +
                        " Existing output files were not replaced. Run Tools/Prefab to PSD/Run Self Tests and inspect self-test-report.txt.");
                }
                finally
                {
                    if (control != null) Object.DestroyImmediate(control);
                    Root.SetActive(rootWasActive);
                    foreach (Graphic graphic in allGraphics)
                        if (graphic.isActiveAndEnabled) graphic.SetAllDirty();
                    Canvas.ForceUpdateCanvases();
                }
            }

            private void DisableDynamicBehaviours()
            {
                foreach (Animator animator in Root.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
                foreach (Animation animation in Root.GetComponentsInChildren<Animation>(true)) animation.enabled = false;
                foreach (Camera otherCamera in Root.GetComponentsInChildren<Camera>(true)) otherCamera.enabled = false;
                foreach (Renderer renderer in Root.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.enabled = false;
                    WarnOnce("Non-UGUI Renderer components (3D meshes, SpriteRenderer, particles) are excluded. This exporter supports UGUI RectTransform prefabs.");
                }
                foreach (MonoBehaviour behaviour in Root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null) { WarnOnce("The source contains missing scripts."); continue; }
                    if (behaviour is CanvasScaler) { behaviour.enabled = false; continue; }
                    if (behaviour is Graphic || behaviour is Mask || behaviour is RectMask2D ||
                        behaviour is ILayoutController || behaviour is ILayoutElement ||
                        behaviour is IMeshModifier || behaviour is IMaterialModifier) continue;
                    behaviour.enabled = false;
                }
            }

            private void FreezeLayouts()
            {
                RectTransform[] transforms = Root.GetComponentsInChildren<RectTransform>(true);
                List<RectSnapshot> snapshots = new List<RectSnapshot>();
                foreach (RectTransform transform in transforms) snapshots.Add(new RectSnapshot(transform));
                foreach (MonoBehaviour behaviour in Root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (behaviour is ILayoutController) behaviour.enabled = false;
                foreach (RectSnapshot snapshot in snapshots) snapshot.Restore();
                Canvas.ForceUpdateCanvases();
            }

            public List<Graphic> GetVisibleGraphics()
            {
                List<Graphic> result = new List<Graphic>();
                for (int index = 0; index < allGraphics.Length; index++)
                {
                    Graphic graphic = allGraphics[index];
                    if (!graphicEnabled[index] || !graphic.gameObject.activeInHierarchy) continue;
                    if (graphic.canvas == null || !graphic.canvas.isActiveAndEnabled) continue;
                    Mask mask = graphic.GetComponent<Mask>();
                    if (mask != null && mask.isActiveAndEnabled && !mask.showMaskGraphic) continue;
                    result.Add(graphic);
                }
                return result;
            }

            public bool HasNestedSortingOverride()
            {
                foreach (Canvas canvas in Root.GetComponentsInChildren<Canvas>(true))
                    if (canvas.transform != Root.transform && canvas.isActiveAndEnabled && canvas.overrideSorting)
                        return true;
                return false;
            }

            public int CompareGraphics(Graphic left, Graphic right)
            {
                Canvas leftCanvas = SortingCanvas(left);
                Canvas rightCanvas = SortingCanvas(right);
                int compare = SortingLayer.GetLayerValueFromID(leftCanvas.sortingLayerID).CompareTo(
                    SortingLayer.GetLayerValueFromID(rightCanvas.sortingLayerID));
                if (compare != 0) return compare;
                compare = leftCanvas.sortingOrder.CompareTo(rightCanvas.sortingOrder);
                if (compare != 0) return compare;
                compare = originalDepths[left].CompareTo(originalDepths[right]);
                return compare != 0 ? compare : originalIndices[left].CompareTo(originalIndices[right]);
            }

            public void RememberRenderedDepths()
            {
                // The native Canvas batches have now been rendered once. Before that,
                // some Unity versions report -1 for absoluteDepth.
                foreach (Graphic graphic in allGraphics)
                    originalDepths[graphic] = graphic.canvasRenderer.absoluteDepth;
                if (!collectDiagnostics) return;
                report.CaptureDiagnostics.Add("Capture RT=" + target.format + ", size=" + width + "x" + height +
                    ", cameraType=" + camera.cameraType + ", cameraPosition=" + camera.transform.position +
                    ", orthographicSize=" + camera.orthographicSize + ", aspect=" + camera.aspect);
                Vector3[] corners = new Vector3[4];
                foreach (Graphic graphic in allGraphics)
                {
                    CanvasRenderer renderer = graphic.canvasRenderer;
                    Canvas canvas = graphic.canvas;
                    graphic.rectTransform.GetWorldCorners(corners);
                    report.CaptureDiagnostics.Add(string.Format(CultureInfo.InvariantCulture,
                        "Graphic '{0}': enabled={1}, active={2}, layer={3}, canvas={4}, canvasMode={5}, " +
                        "absoluteDepth={6}, relativeDepth={7}, cull={8}, materials={9}, color={10}, " +
                        "rendererColor={11}, worldBottomLeft={12}, worldTopRight={13}",
                        graphic.name, graphic.enabled, graphic.gameObject.activeInHierarchy,
                        graphic.gameObject.layer, canvas == null ? "<none>" : canvas.name,
                        canvas == null ? "<none>" : canvas.renderMode.ToString(), renderer.absoluteDepth,
                        renderer.relativeDepth, renderer.cull, renderer.materialCount, graphic.color,
                        renderer.GetColor(), corners[0], corners[2]));
                }
            }

            private Canvas SortingCanvas(Graphic graphic)
            {
                Transform transform = graphic.transform;
                while (transform != null)
                {
                    Canvas canvas = transform.GetComponent<Canvas>();
                    if (canvas != null && canvas.isActiveAndEnabled && (canvas.overrideSorting || transform == host.transform))
                        return canvas;
                    transform = transform.parent;
                }
                return host.GetComponent<Canvas>();
            }

            public void Isolate(Graphic selected)
            {
                HashSet<Graphic> neededMaskGraphics = new HashSet<Graphic>();
                for (int index = 0; index < masks.Length; index++)
                {
                    Mask mask = masks[index];
                    bool ancestor = maskEnabled[index] &&
                        (selected.transform == mask.transform || selected.transform.IsChildOf(mask.transform));
                    if (ancestor)
                    {
                        neededMaskGraphics.Add(mask.graphic);
                        mask.showMaskGraphic = mask.graphic == selected && maskShowsGraphic[index];
                    }
                    else mask.showMaskGraphic = maskShowsGraphic[index];
                }
                for (int index = 0; index < allGraphics.Length; index++)
                {
                    Graphic graphic = allGraphics[index];
                    graphic.enabled = graphicEnabled[index] &&
                        (graphic == selected || neededMaskGraphics.Contains(graphic));
                    if (graphic.enabled) graphic.SetAllDirty();
                }
                Canvas.ForceUpdateCanvases();
            }

            public byte[] CaptureRgba()
            {
                Color[] black = Render(Color.black);
                Color[] white = Render(Color.white);
                byte[] result = new byte[checked(width * height * 4)];
                bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
                bool invalidBlend = false;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int sourceIndex = y * width + x;
                        Color b = black[sourceIndex];
                        Color w = white[sourceIndex];
                        float deltaR = w.r - b.r;
                        float deltaG = w.g - b.g;
                        float deltaB = w.b - b.b;
                        float alpha = Mathf.Clamp01(1f - (deltaR + deltaG + deltaB) / 3f);
                        if (Mathf.Abs(deltaR - deltaG) > 0.01f || Mathf.Abs(deltaR - deltaB) > 0.01f ||
                            b.r > alpha + 0.01f || b.g > alpha + 0.01f || b.b > alpha + 0.01f)
                            invalidBlend = true;
                        int destination = ((height - 1 - y) * width + x) * 4;
                        if (alpha <= 0.5f / 255f) continue;
                        float red = Mathf.Clamp01(b.r / alpha);
                        float green = Mathf.Clamp01(b.g / alpha);
                        float blue = Mathf.Clamp01(b.b / alpha);
                        if (linear)
                        {
                            red = Mathf.LinearToGammaSpace(red);
                            green = Mathf.LinearToGammaSpace(green);
                            blue = Mathf.LinearToGammaSpace(blue);
                        }
                        result[destination] = ToByte(red);
                        result[destination + 1] = ToByte(green);
                        result[destination + 2] = ToByte(blue);
                        result[destination + 3] = ToByte(alpha);
                    }
                }
                if (invalidBlend)
                    WarnOnce("Capture detected non-standard blending or HDR values. Normal RGBA PSD layers cannot reproduce all custom shader behavior; inspect the merged preview and affected layers.");
                return result;
            }

            private Color[] Render(Color background)
            {
                RenderTexture previous = RenderTexture.active;
                try
                {
                    camera.backgroundColor = background;
                    Canvas.ForceUpdateCanvases();
                    if (emitUiGeometry)
                        CaptureRenderContext.EmitWorldGeometryForSceneView(camera);
                    camera.Render();
                    RenderTexture.active = target;
                    readback.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                    readback.Apply(false, false);
                    return readback.GetPixels();
                }
                finally { RenderTexture.active = previous; }
            }

            private static byte ToByte(float value)
            {
                return (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
            }

            private void WarnOnce(string warning)
            {
                if (!report.Warnings.Contains(warning)) report.Warnings.Add(warning);
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (camera != null) camera.targetTexture = null;
                if (readback != null) Object.DestroyImmediate(readback);
                if (target != null)
                {
                    target.Release();
                    Object.DestroyImmediate(target);
                }
                if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            }

            private sealed class RectSnapshot
            {
                private readonly RectTransform transform;
                private readonly Vector2 anchorMin, anchorMax, pivot, sizeDelta;
                private readonly Vector3 anchoredPosition, scale;
                private readonly Quaternion rotation;
                public RectSnapshot(RectTransform transform)
                {
                    this.transform = transform;
                    anchorMin = transform.anchorMin;
                    anchorMax = transform.anchorMax;
                    pivot = transform.pivot;
                    sizeDelta = transform.sizeDelta;
                    anchoredPosition = transform.anchoredPosition3D;
                    scale = transform.localScale;
                    rotation = transform.localRotation;
                }
                public void Restore()
                {
                    transform.anchorMin = anchorMin;
                    transform.anchorMax = anchorMax;
                    transform.pivot = pivot;
                    transform.sizeDelta = sizeDelta;
                    transform.anchoredPosition3D = anchoredPosition;
                    transform.localScale = scale;
                    transform.localRotation = rotation;
                }
            }
        }
    }
}
#endif
