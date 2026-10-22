#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using PrefabToPsd.Core;

namespace PrefabToPsd
{
    public enum ExportPreset
    {
        CurrentAppearance,
        ArtistDelivery,
        RoundTripStructure,
        SourceReference
    }

    /// <summary>One configuration shared by single, batch and command-line exports.</summary>
    [Serializable]
    public sealed class ExportOptions
    {
        public const string Version = "2.0.1";
        public int Width;
        public int Height;
        public bool PreserveHierarchy = true;
        public bool WriteMetadata = true;
        public int MemoryBudgetMB = 512;
        public PsdCompression Compression = PsdCompression.Rle;
        public ExportPreset Preset = ExportPreset.CurrentAppearance;
        public bool PreserveEmptyGroups;
        public bool AddCanvasMarker;
        public bool VerifyOutput = true;
        public string ReferencePsdPath = "";
        public bool AutoDetectReferencePsd = true;
        public bool PreserveReferenceIds = true;
        public bool PreserveReferenceNames = true;
        public bool ReuseIdenticalReferencePixels = true;
        public bool CopySafeReferenceResources = true;
        /// <summary>Collect native Canvas/Graphic state for graphics integration diagnostics.</summary>
        public bool CollectCaptureDiagnostics;
        /// <summary>Throw OperationCanceledException to cancel before file commit.</summary>
        [NonSerialized] public Action<float, string> Progress;

        public ExportOptions Clone()
        {
            // All settings are value types/immutable strings; the progress callback is
            // intentionally copied, so batch service can wrap it without losing it.
            return (ExportOptions)MemberwiseClone();
        }
    }

    public sealed class ExportReport
    {
        public int Width;
        public int Height;
        /// <summary>Content Graphic layers; excludes optional #Canvas and folder records.</summary>
        public int LayerCount;
        public string PsdPath;
        public string MetadataPath;
        public string ReferencePath;
        public int ReferenceMatchedLayers;
        public int ReferenceReusedLayers;
        public int ReferenceRestoredIds;
        public bool ReferenceColorProfileCopied;
        public bool VerificationPassed;
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> CaptureDiagnostics = new List<string>();
    }
}
#endif
