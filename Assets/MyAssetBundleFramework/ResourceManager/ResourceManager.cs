using System;
using UnityEngine;
using BundleService = MyAssetBundleFramework.BundleManager.BundleManager;

namespace MyAssetBundleFramework.ResourceManager
{
    public sealed class ResourceManager
    {
        private readonly BundleService _bundleManager = new();

        public void Initialize(string bundleRootPath = null)
        {
            _bundleManager.Initialize(bundleRootPath);
        }

        public T LoadResource<T>(string bundleName, string assetName)
            where T : UnityEngine.Object
        {
            if (string.IsNullOrWhiteSpace(assetName))
            {
                throw new ArgumentException(
                    "Asset name cannot be null or empty.", nameof(assetName));
            }

            AssetBundle bundle = _bundleManager.LoadBundle(bundleName);
            T asset = bundle.LoadAsset<T>(assetName);

            if (asset == null)
            {
                Debug.LogError(
                    $"Asset '{assetName}' was not found in bundle '{bundleName}'.");
            }

            return asset;
        }

        public void UnloadAll(bool unloadAllLoadedObjects = false)
        {
            _bundleManager.UnloadAll(unloadAllLoadedObjects);
        }
    }
}
