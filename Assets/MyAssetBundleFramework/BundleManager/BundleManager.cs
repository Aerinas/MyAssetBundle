using System;
using System.Collections.Generic;
using System.IO;
using MyAssetBundleFramework.Manifest;
using UnityEngine;

namespace MyAssetBundleFramework.BundleManager
{
    internal sealed class BundleLoadOperation
    {
        private readonly BundleManager _manager;
        private readonly List<string> _loadOrder;
        private readonly List<BundleManager.BundleEntry> _acquiredEntries = new();
        private int _loadIndex;
        private BundleManager.BundleEntry _currentEntry;

        internal BundleLoadOperation(BundleManager manager, string bundleName, List<string> loadOrder)
        {
            _manager = manager;
            BundleName = bundleName;
            _loadOrder = loadOrder;
        }

        internal string BundleName { get; }
        internal AssetBundle AssetBundle { get; private set; }
        internal Exception Error { get; private set; }
        internal bool IsDone { get; private set; }

        internal void Update()
        {
            while (!IsDone)
            {
                if (_currentEntry == null)
                {
                    if (_loadIndex >= _loadOrder.Count)
                    {
                        AssetBundle = _manager.GetLoadedAssetBundle(BundleName);
                        IsDone = true;
                        return;
                    }

                    try
                    {
                        _currentEntry = _manager.AcquireEntryAsync(_loadOrder[_loadIndex]);
                        _acquiredEntries.Add(_currentEntry);
                    }
                    catch (Exception exception)
                    {
                        Fail(exception);
                        return;
                    }
                }

                _manager.RefreshEntry(_currentEntry);
                if (!_currentEntry.IsDone)
                {
                    return;
                }

                if (_currentEntry.Error != null)
                {
                    Fail(_currentEntry.Error);
                    return;
                }

                _currentEntry = null;
                _loadIndex++;
            }
        }

        private void Fail(Exception exception)
        {
            Error = exception;
            IsDone = true;
            _manager.Rollback(_acquiredEntries);
        }
    }

    public sealed class BundleManager
    {
        internal sealed class BundleEntry : ABundle
        {
        }

        private readonly Dictionary<string, BundleEntry> _loadedBundles =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<BundleEntry> _pendingUnload = new();
        private readonly List<BundleLoadOperation> _asyncOperations = new();

        private string _bundleRootPath;
        private ManifestIndex _index;
        private ulong _offset;

        public int LoadedBundleCount => _loadedBundles.Count;

        public void Initialize(string bundleRootPath = null, ulong offset = 0)
        {
            string root = Path.GetFullPath(string.IsNullOrWhiteSpace(bundleRootPath)
                ? Path.Combine(Application.dataPath, "AssetBundles")
                : bundleRootPath);

            if (_index != null)
            {
                if (!string.Equals(root, _bundleRootPath, StringComparison.Ordinal) || offset != _offset)
                {
                    throw new InvalidOperationException(
                        "UnloadAll must be called before changing the Bundle root or file offset.");
                }

                return;
            }

            string manifestPath = Path.Combine(root, RuntimeManifest.FileName);
            string json = File.ReadAllText(manifestPath);
            ManifestIndex index = new(JsonUtility.FromJson<RuntimeManifest>(json));
            _bundleRootPath = root;
            _offset = offset;
            _index = index;
        }

        public ResourceInfo GetResourceInfo(string address)
        {
            EnsureInitialized();
            return _index.GetResource(address);
        }

        public AssetBundle LoadBundle(string bundleName)
        {
            EnsureInitialized();
            string canonicalName = GetCanonicalBundleName(bundleName);
            AcquireEntry(canonicalName);
            return GetLoadedAssetBundle(canonicalName);
        }

        /// <summary>
        /// 返回可观察状态和引用数的 Bundle 句柄。
        /// 异步句柄实现 CustomYieldInstruction，可直接用于 yield return。
        /// </summary>
        public IBundle Load(string bundleName, bool isAsync)
        {
            EnsureInitialized();
            string canonicalName = GetCanonicalBundleName(bundleName);
            if (isAsync)
            {
                BundleLoadOperation operation = LoadBundleAsync(canonicalName);
                if (operation.Error != null)
                {
                    throw operation.Error;
                }
            }
            else
            {
                LoadBundle(canonicalName);
            }

            return _loadedBundles[canonicalName];
        }

        internal BundleLoadOperation LoadBundleAsync(string bundleName)
        {
            EnsureInitialized();
            string canonicalName = GetCanonicalBundleName(bundleName);
            BundleLoadOperation operation = new(
                this,
                canonicalName,
                new List<string> { canonicalName });
            operation.Update();

            if (!operation.IsDone)
            {
                _asyncOperations.Add(operation);
            }

            return operation;
        }

        public void ReleaseBundle(string bundleName)
        {
            EnsureInitialized();
            string canonicalName = GetCanonicalBundleName(bundleName);
            if (!_loadedBundles.TryGetValue(canonicalName, out BundleEntry entry))
            {
                throw new InvalidOperationException($"Bundle '{canonicalName}' is not loaded.");
            }

            ReleaseEntry(entry);
        }

        /// <summary>通过框架 Bundle 句柄归还一次引用。</summary>
        public void Unload(IBundle bundle)
        {
            EnsureInitialized();
            if (bundle is not BundleEntry entry ||
                !_loadedBundles.TryGetValue(entry.Name, out BundleEntry cachedEntry) ||
                !ReferenceEquals(entry, cachedEntry))
            {
                throw new InvalidOperationException(
                    "The Bundle handle does not belong to this BundleManager.");
            }

            ReleaseEntry(entry);
        }

        [Obsolete("Use Unload(IBundle) instead.")]
        public void UnLoad(IBundle bundle)
        {
            Unload(bundle);
        }

        public int GetReferenceCount(string bundleName)
        {
            EnsureInitialized();
            string canonicalName = GetCanonicalBundleName(bundleName);
            return _loadedBundles.TryGetValue(canonicalName, out BundleEntry entry)
                ? entry.ReferenceCount
                : 0;
        }

        public void Update()
        {
            for (int index = _asyncOperations.Count - 1; index >= 0; index--)
            {
                BundleLoadOperation operation = _asyncOperations[index];
                operation.Update();
                if (operation.IsDone)
                {
                    _asyncOperations.RemoveAt(index);
                }
            }
        }

        public void LateUpdate()
        {
            int checkCount = _pendingUnload.Count;
            for (int index = 0; index < checkCount; index++)
            {
                BundleEntry entry = _pendingUnload.First.Value;
                _pendingUnload.RemoveFirst();

                if (entry.ReferenceCount > 0)
                {
                    entry.WaitingForUnload = false;
                    continue;
                }

                RefreshEntry(entry);
                if (!entry.IsDone)
                {
                    _pendingUnload.AddLast(entry);
                    continue;
                }

                UnloadEntryImmediately(entry, false);
            }
        }

        public void UnloadAll(bool unloadAllLoadedObjects = false)
        {
            foreach (BundleEntry entry in _loadedBundles.Values)
            {
                if (entry.Request != null)
                {
                    AssetBundleCreateRequest request = entry.Request;
                    if (request.isDone)
                    {
                        request.assetBundle?.Unload(unloadAllLoadedObjects);
                    }
                    else
                    {
                        request.completed += _ => request.assetBundle?.Unload(unloadAllLoadedObjects);
                    }
                }
                else
                {
                    entry.AssetBundle?.Unload(unloadAllLoadedObjects);
                }
            }

            _asyncOperations.Clear();
            _pendingUnload.Clear();
            _loadedBundles.Clear();
            _index = null;
            _bundleRootPath = null;
            _offset = 0;
        }

        private BundleEntry AcquireEntry(string bundleName)
        {
            if (_loadedBundles.TryGetValue(bundleName, out BundleEntry cachedEntry))
            {
                if (!cachedEntry.IsDone)
                {
                    throw new InvalidOperationException(
                        $"Bundle '{bundleName}' is loading asynchronously and cannot be loaded synchronously.");
                }

                if (cachedEntry.Error != null)
                {
                    if (cachedEntry.ReferenceCount == 0)
                    {
                        UnloadEntryImmediately(cachedEntry, false);
                    }
                    else
                    {
                        throw cachedEntry.Error;
                    }
                }
                else
                {
                    Reactivate(cachedEntry);
                    cachedEntry.ReferenceCount++;
                    return cachedEntry;
                }
            }

            (string path, BundleInfo info) = GetBundleFile(bundleName);
            AssetBundle assetBundle = AssetBundle.LoadFromFile(path, info.crc, _offset);
            if (assetBundle == null)
            {
                throw new InvalidDataException($"Unable to load bundle or CRC mismatch: {path}");
            }

            BundleEntry entry = new()
            {
                Name = bundleName,
                AssetBundle = assetBundle,
                ReferenceCount = 1
            };
            _loadedBundles.Add(bundleName, entry);
            return entry;
        }

        internal BundleEntry AcquireEntryAsync(string bundleName)
        {
            if (_loadedBundles.TryGetValue(bundleName, out BundleEntry cachedEntry))
            {
                if (cachedEntry.IsDone && cachedEntry.Error != null && cachedEntry.ReferenceCount == 0)
                {
                    UnloadEntryImmediately(cachedEntry, false);
                }
                else
                {
                    Reactivate(cachedEntry);
                    cachedEntry.ReferenceCount++;
                    return cachedEntry;
                }
            }

            (string path, BundleInfo info) = GetBundleFile(bundleName);
            BundleEntry entry = new()
            {
                Name = bundleName,
                ReferenceCount = 1,
                Request = AssetBundle.LoadFromFileAsync(path, info.crc, _offset)
            };
            _loadedBundles.Add(bundleName, entry);
            return entry;
        }

        internal void RefreshEntry(BundleEntry entry)
        {
            if (entry.Request == null || !entry.Request.isDone)
            {
                return;
            }

            entry.AssetBundle = entry.Request.assetBundle;
            entry.Request = null;
            if (entry.AssetBundle == null)
            {
                entry.Error = new InvalidDataException(
                    $"Unable to load bundle or CRC mismatch: {Path.Combine(_bundleRootPath, entry.Name)}");
            }
        }

        internal AssetBundle GetLoadedAssetBundle(string bundleName)
        {
            if (!_loadedBundles.TryGetValue(bundleName, out BundleEntry entry) ||
                !entry.IsDone ||
                entry.Error != null ||
                entry.AssetBundle == null)
            {
                throw new InvalidOperationException($"Bundle '{bundleName}' has not completed loading.");
            }

            return entry.AssetBundle;
        }

        internal void Rollback(IReadOnlyList<BundleEntry> acquiredEntries)
        {
            for (int index = acquiredEntries.Count - 1; index >= 0; index--)
            {
                BundleEntry entry = acquiredEntries[index];
                if (entry.ReferenceCount > 0)
                {
                    entry.ReferenceCount--;
                }

                if (entry.ReferenceCount == 0)
                {
                    RefreshEntry(entry);
                    if (entry.IsDone)
                    {
                        UnloadEntryImmediately(entry, false);
                    }
                    else if (!entry.WaitingForUnload)
                    {
                        entry.WaitingForUnload = true;
                        _pendingUnload.AddLast(entry);
                    }
                }
            }
        }

        private void ReleaseEntry(BundleEntry entry)
        {
            if (entry.ReferenceCount <= 0)
            {
                throw new InvalidOperationException($"Bundle '{entry.Name}' reference count is already zero.");
            }

            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0 && !entry.WaitingForUnload)
            {
                entry.WaitingForUnload = true;
                _pendingUnload.AddLast(entry);
            }
        }

        private void Reactivate(BundleEntry entry)
        {
            if (entry.ReferenceCount == 0 && entry.WaitingForUnload)
            {
                _pendingUnload.Remove(entry);
                entry.WaitingForUnload = false;
            }
        }

        private void UnloadEntryImmediately(BundleEntry entry, bool unloadAllLoadedObjects)
        {
            if (entry.WaitingForUnload)
            {
                _pendingUnload.Remove(entry);
                entry.WaitingForUnload = false;
            }

            if (_loadedBundles.TryGetValue(entry.Name, out BundleEntry cachedEntry) &&
                ReferenceEquals(entry, cachedEntry))
            {
                _loadedBundles.Remove(entry.Name);
            }

            entry.AssetBundle?.Unload(unloadAllLoadedObjects);
            entry.AssetBundle = null;
            entry.Error = null;
        }

        private (string path, BundleInfo info) GetBundleFile(string bundleName)
        {
            BundleInfo info = _index.GetBundle(bundleName);
            string path = Path.Combine(_bundleRootPath, info.bundleName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Bundle file not found.", path);
            }

            if (new FileInfo(path).Length != info.size)
            {
                throw new InvalidDataException($"Bundle size mismatch: {path}");
            }

            return (path, info);
        }

        private string GetCanonicalBundleName(string bundleName)
        {
            return ManifestIndex.Normalize(_index.GetBundle(bundleName).bundleName);
        }

        private void EnsureInitialized()
        {
            if (_index == null)
            {
                throw new InvalidOperationException("Call Initialize() first.");
            }
        }
    }
}
