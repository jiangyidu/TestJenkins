#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace PrefabToPsd
{
    /// <summary>Creates editable UGUI fixtures without any dependency on the supplied PSD importer.</summary>
    public static class PrefabPsdDemo
    {
        [MenuItem("Tools/Prefab to PSD/Create Demo Prefab")]
        public static void CreateDemoPrefab()
        {
            string path = EditorUtility.SaveFilePanelInProject("Create Prefab to PSD demo",
                "PrefabToPsdDemo", "prefab", "Select a location for the demo prefab.");
            if (String.IsNullOrEmpty(path)) return;
            Scene scene = EditorSceneManager.NewPreviewScene();
            GameObject root = null;
            try
            {
                string directory = Path.GetDirectoryName(path).Replace('\\', '/');
                string texturePath = AssetDatabase.GenerateUniqueAssetPath(
                    directory + "/" + Path.GetFileNameWithoutExtension(path) + "_NineSlice.asset");
                Sprite sprite = CreateSlicedSpriteAsset(texturePath);
                root = BuildDemo(sprite);
                SceneManager.MoveGameObjectToScene(root, scene);
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
                AssetDatabase.SaveAssets();
                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);
                Debug.Log("Created Prefab to PSD demo: " + path +
                    ". Export at 640 × 480, then run Tools/Prefab to PSD/Run Self Tests.");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        internal static GameObject Root(string name, int width, int height)
        {
            GameObject root = new GameObject(name, typeof(RectTransform));
            RectTransform rect = root.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(width, height);
            rect.pivot = new Vector2(0.5f, 0.5f);
            return root;
        }

        // Coordinates are deliberately top-left based, like the PSD output.
        internal static RectTransform Rect(Transform parent, string name,
            float left, float top, float width, float height)
        {
            GameObject child = new GameObject(name, typeof(RectTransform));
            RectTransform rect = child.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(left, -top);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        internal static Image Image(Transform parent, string name,
            float left, float top, float width, float height, Color color)
        {
            RectTransform rect = Rect(parent, name, left, top, width, height);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        internal static Sprite CreateSlicedSpriteAsset(string assetPath)
        {
            const int size = 24;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.name = "NineSliceTexture";
            texture.filterMode = FilterMode.Point;
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; ++y)
                for (int x = 0; x < size; ++x)
                    pixels[y * size + x] = (x < 4 || x >= size - 4 || y < 4 || y >= size - 4)
                        ? new Color32(55, 202, 192, 255) : new Color32(24, 58, 77, 255);
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            AssetDatabase.CreateAsset(texture, assetPath);
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(4, 4, 4, 4));
            sprite.name = "NineSliceSprite";
            AssetDatabase.AddObjectToAsset(sprite, texture);
            AssetDatabase.SaveAssets();
            return sprite;
        }

        internal static GameObject BuildDemo(Sprite slicedSprite)
        {
            GameObject root = Root("Prefab → PSD 示例", 640, 480);
            Image(root.transform, "Background", 0, 0, 640, 480, new Color(0.045f, 0.08f, 0.14f, 1));
            RectTransform group = Rect(root.transform, "内容组 中文", 24, 24, 592, 432);
            Image card = Image(group, "Nine sliced panel", 0, 0, 592, 104, Color.white);
            card.sprite = slicedSprite;
            card.type = UnityEngine.UI.Image.Type.Sliced;
            Text(group, "Title", 20, 12, 552, 38, "Prefab to PSD", 28, Color.white);
            Text subtitle = Text(group, "Raster text", 20, 58, 552, 32,
                "Layers, clipping, alpha and hierarchy", 19, new Color(0.70f, 0.87f, 0.97f, 1));
            Shadow shadow = subtitle.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0, 0, 0, 0.7f);
            shadow.effectDistance = new Vector2(2, -2);

            RectTransform alphaGroup = Rect(group, "CanvasGroup alpha 0.6", 0, 128, 180, 96);
            alphaGroup.gameObject.AddComponent<CanvasGroup>().alpha = 0.6f;
            Image(alphaGroup, "Red", 0, 0, 104, 72, new Color(1, 0.2f, 0.15f, 0.8f));
            Image(alphaGroup, "Blue", 56, 24, 104, 72, new Color(0.1f, 0.5f, 1, 0.8f));

            Image maskImage = Image(group, "Mask (hidden)", 208, 128, 136, 96, Color.white);
            Mask mask = maskImage.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = false;
            Image(maskImage.transform, "Clipped yellow", -24, -16, 200, 128,
                new Color(1, 0.7f, 0.12f, 1));
            RectTransform clip = Rect(group, "RectMask2D", 380, 128, 160, 96);
            clip.gameObject.AddComponent<RectMask2D>();
            Image tilted = Image(clip, "Rotated clipped image", -12, 4, 190, 80,
                new Color(0.2f, 0.85f, 0.65f, 1));
            tilted.rectTransform.localRotation = Quaternion.Euler(0, 0, -18);

            Image stretch = Image(group, "Stretch anchors", 0, 258, 592, 56,
                new Color(0.14f, 0.23f, 0.36f, 1));
            stretch.rectTransform.anchorMin = new Vector2(0, 1);
            stretch.rectTransform.anchorMax = new Vector2(1, 1);
            stretch.rectTransform.sizeDelta = new Vector2(0, 56);
            Text label = Text(stretch.transform, "Outline text", 16, 10, 548, 36,
                "Editable hierarchy / rasterized appearance", 18, Color.white);
            Outline outline = label.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0, 0, 0, 0.7f);
            outline.effectDistance = new Vector2(1, -1);
            Image hidden = Image(group, "Inactive - must not appear", 0, 0, 592, 432, Color.magenta);
            hidden.gameObject.SetActive(false);
            return root;
        }

        private static Text Text(Transform parent, string name, float left, float top,
            float width, float height, string text, int size, Color color)
        {
            Text result = Rect(parent, name, left, top, width, height).gameObject.AddComponent<Text>();
            result.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            result.text = text;
            result.fontSize = size;
            result.color = color;
            result.alignment = TextAnchor.MiddleLeft;
            result.horizontalOverflow = HorizontalWrapMode.Overflow;
            result.verticalOverflow = VerticalWrapMode.Overflow;
            result.raycastTarget = false;
            return result;
        }
    }
}
#endif
