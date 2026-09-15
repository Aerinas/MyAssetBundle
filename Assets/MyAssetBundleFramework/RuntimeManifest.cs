using System;
using System.Collections.Generic;

namespace MyAssetBundleFramework.Manifest
{
    [Serializable]
    public sealed class RuntimeManifest
    {
        public const string FileName = "runtime_manifest.json";

        public int version = 2;
        public List<ResourceInfo> resources = new();
        public List<BundleInfo> bundles = new();
    }

    [Serializable]
    public sealed class ResourceInfo
    {
        public string address;
        public string assetPath;
        public string bundleName;
        public List<string> directDependencies = new();
    }

    [Serializable]
    public sealed class BundleInfo
    {
        public string bundleName;
        public string hash;
        public long size;
        public uint crc;
        public List<string> directDependencies = new();
    }
}
