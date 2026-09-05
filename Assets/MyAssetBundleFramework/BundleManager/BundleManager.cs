using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MyAssetBundleFramework.BundleManager
{
    public sealed class BundleManager
    {
        private readonly Dictionary<string, AssetBundle> _loadedBundles =
            new(StringComparer.OrdinalIgnoreCase);

        private string _bundleRootPath;
        private AssetBundle _manifestBundle;
        private AssetBundleManifest _manifest;

        public void Initialize(string bundleRootPath = null)
        {
            if (_manifest != null)
            {
                return;
            }

            _bundleRootPath = string.IsNullOrWhiteSpace(bundleRootPath)
                ? Path.Combine(Application.dataPath, "AssetBundles")
                : bundleRootPath;

            string normalizedRootPath = _bundleRootPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string manifestBundleName = Path.GetFileName(normalizedRootPath);
            string manifestBundlePath = Path.Combine(normalizedRootPath, manifestBundleName);

            _manifestBundle = AssetBundle.LoadFromFile(manifestBundlePath);
            if (_manifestBundle == null)
            {
                throw new FileNotFoundException(
                    $"Unable to load the AssetBundle manifest: {manifestBundlePath}");
            }

            _manifest = _manifestBundle.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
            if (_manifest == null)
            {
                _manifestBundle.Unload(true);
                _manifestBundle = null;
                throw new InvalidDataException(
                    $"AssetBundleManifest was not found in: {manifestBundlePath}");
            }
        }

        public AssetBundle LoadBundle(string bundleName)
        {
            if (_manifest == null)
            {
                throw new InvalidOperationException(
                    "BundleManager is not initialized. Call Initialize() first.");
            }

            if (string.IsNullOrWhiteSpace(bundleName))
            {
                throw new ArgumentException(
                    "Bundle name cannot be null or empty.", nameof(bundleName));
            }

            if (_loadedBundles.TryGetValue(bundleName, out AssetBundle loadedBundle))
            {
                return loadedBundle;
            }

            foreach (string dependencyName in _manifest.GetAllDependencies(bundleName))
            {
                LoadBundleFile(dependencyName);
            }

            return LoadBundleFile(bundleName);
        }

        public void UnloadAll(bool unloadAllLoadedObjects = false)
        {
            foreach (AssetBundle bundle in _loadedBundles.Values)
            {
                bundle.Unload(unloadAllLoadedObjects);
            }

            _loadedBundles.Clear();

            if (_manifestBundle != null)
            {
                _manifestBundle.Unload(unloadAllLoadedObjects);
                _manifestBundle = null;
                _manifest = null;
            }
        }

        private AssetBundle LoadBundleFile(string bundleName)
        {
            if (_loadedBundles.TryGetValue(bundleName, out AssetBundle loadedBundle))
            {
                return loadedBundle;
            }

            string bundlePath = Path.Combine(_bundleRootPath, bundleName);
            AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                throw new FileNotFoundException(
                    $"Unable to load AssetBundle: {bundlePath}");
            }

            _loadedBundles.Add(bundleName, bundle);
            return bundle;
        }
    }
}
