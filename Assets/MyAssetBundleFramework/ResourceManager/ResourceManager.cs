using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MyAssetBundleFramework.Manifest;
using UnityEngine;
using BundleLoadOperation = MyAssetBundleFramework.BundleManager.BundleLoadOperation;
using BundleService = MyAssetBundleFramework.BundleManager.BundleManager;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MyAssetBundleFramework.ResourceManager
{
    public sealed class ResourceManager
    {
        private readonly BundleService _bundleManager = new();
        private readonly Dictionary<string, ResourceEntry> _resources =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<ResourceEntry> _pendingUnload = new();
        private readonly List<ResourceEntry> _asyncResources = new();
        private readonly HashSet<string> _loadingAddresses =
            new(StringComparer.OrdinalIgnoreCase);

        private ResourceManagerDriver _driver;
        private bool _editorMode;
        private bool _initialized;

        public int LoadedBundleCount => _editorMode ? 0 : _bundleManager.LoadedBundleCount;
        public int LoadedResourceCount => _resources.Count;

        public void Initialize(string bundleRootPath = null)
        {
#if UNITY_EDITOR
            bool useAssetDatabase = string.IsNullOrWhiteSpace(bundleRootPath);
#else
            const bool useAssetDatabase = false;
#endif
            Initialize(bundleRootPath, useAssetDatabase);
        }

        public void Initialize(string bundleRootPath, bool useAssetDatabase, ulong offset = 0)
        {
            if (_resources.Count > 0)
            {
                throw new InvalidOperationException(
                    "Unload all resources before reinitializing ResourceManager.");
            }

            if (_initialized)
            {
                UnloadAll();
            }

#if !UNITY_EDITOR
            if (useAssetDatabase)
            {
                throw new NotSupportedException("AssetDatabase mode is only available in the Unity Editor.");
            }
#endif

            _resources.Clear();
            _pendingUnload.Clear();
            _asyncResources.Clear();
            _loadingAddresses.Clear();
            _editorMode = useAssetDatabase;

            if (!_editorMode)
            {
                _bundleManager.Initialize(bundleRootPath, offset);
            }

            _initialized = true;
            AttachDriver();
        }

        public T LoadResource<T>(string address) where T : UnityEngine.Object
        {
            EnsureInitialized();
            string normalizedAddress = NormalizeAddress(address);

            if (_editorMode)
            {
                return LoadResourceInternal<T>(
                    normalizedAddress,
                    normalizedAddress,
                    null,
                    Array.Empty<string>());
            }

            ResourceInfo resource = _bundleManager.GetResourceInfo(normalizedAddress);
            return LoadResourceInternal<T>(
                normalizedAddress,
                resource.assetPath,
                resource.bundleName,
                resource.directDependencies);
        }

        public T LoadResource<T>(string bundleName, string assetName)
            where T : UnityEngine.Object
        {
            EnsureInitialized();
            string normalizedAssetName = NormalizeAddress(assetName);
            return LoadResourceInternal<T>(
                normalizedAssetName,
                normalizedAssetName,
                _editorMode ? null : ManifestIndex.Normalize(bundleName),
                Array.Empty<string>());
        }

        public Task<T> LoadResourceAsync<T>(string address) where T : UnityEngine.Object
        {
            EnsureInitialized();
            string normalizedAddress = NormalizeAddress(address);
            ResourceEntry entry;

            if (_editorMode)
            {
                entry = LoadResourceEntryAsync(
                    normalizedAddress,
                    normalizedAddress,
                    null,
                    typeof(T),
                    Array.Empty<string>());
            }
            else
            {
                ResourceInfo resource = _bundleManager.GetResourceInfo(normalizedAddress);
                entry = LoadResourceEntryAsync(
                    normalizedAddress,
                    resource.assetPath,
                    resource.bundleName,
                    typeof(T),
                    resource.directDependencies);
            }

            return ConvertTask<T>(entry.Completion.Task);
        }

        public Task<T> LoadResourceAsync<T>(string bundleName, string assetName)
            where T : UnityEngine.Object
        {
            EnsureInitialized();
            string normalizedAssetName = NormalizeAddress(assetName);
            ResourceEntry entry = LoadResourceEntryAsync(
                normalizedAssetName,
                normalizedAssetName,
                _editorMode ? null : ManifestIndex.Normalize(bundleName),
                typeof(T),
                Array.Empty<string>());
            return ConvertTask<T>(entry.Completion.Task);
        }

        /// <summary>
        /// 返回可观察引用数、完成状态和错误的资源句柄。
        /// 句柄实现了 CustomYieldInstruction，可直接用于 yield return。
        /// </summary>
        public IResource Load(string address, bool isAsync)
        {
            string normalizedAddress = NormalizeAddress(address);
            if (!isAsync)
            {
                LoadResource<UnityEngine.Object>(normalizedAddress);
                return _resources[normalizedAddress];
            }

            EnsureInitialized();
            if (_editorMode)
            {
                return LoadResourceEntryAsync(
                    normalizedAddress,
                    normalizedAddress,
                    null,
                    typeof(UnityEngine.Object),
                    Array.Empty<string>());
            }

            ResourceInfo resource = _bundleManager.GetResourceInfo(normalizedAddress);
            return LoadResourceEntryAsync(
                normalizedAddress,
                resource.assetPath,
                resource.bundleName,
                typeof(UnityEngine.Object),
                resource.directDependencies);
        }

        public void LoadResourceWithCallback<T>(
            string address,
            Action<T> completed,
            Action<Exception> failed = null)
            where T : UnityEngine.Object
        {
            Task<T> task;
            try
            {
                task = LoadResourceAsync<T>(address);
            }
            catch (Exception exception)
            {
                failed?.Invoke(exception);
                return;
            }

            void InvokeCallback()
            {
                try
                {
                    completed?.Invoke(task.GetAwaiter().GetResult());
                }
                catch (Exception exception)
                {
                    failed?.Invoke(exception);
                }
            }

            if (task.IsCompleted)
            {
                InvokeCallback();
            }
            else
            {
                task.GetAwaiter().OnCompleted(InvokeCallback);
            }
        }

        public IEnumerator LoadResourceCoroutine<T>(
            string address,
            Action<T> completed,
            Action<Exception> failed = null)
            where T : UnityEngine.Object
        {
            Task<T> task;
            try
            {
                task = LoadResourceAsync<T>(address);
            }
            catch (Exception exception)
            {
                failed?.Invoke(exception);
                yield break;
            }

            while (!task.IsCompleted)
            {
                yield return null;
            }

            try
            {
                completed?.Invoke(task.GetAwaiter().GetResult());
            }
            catch (Exception exception)
            {
                failed?.Invoke(exception);
            }
        }

        public void ReleaseResource(string address)
        {
            EnsureInitialized();
            string normalizedAddress = NormalizeAddress(address);
            if (!_resources.TryGetValue(normalizedAddress, out ResourceEntry entry))
            {
                throw new InvalidOperationException($"Resource '{normalizedAddress}' is not cached.");
            }

            ReleaseEntry(entry);
        }

        /// <summary>通过框架资源句柄归还一次引用。</summary>
        public void Unload(IResource resource)
        {
            EnsureInitialized();
            if (resource is not ResourceEntry entry ||
                !_resources.TryGetValue(entry.Address, out ResourceEntry cachedEntry) ||
                !ReferenceEquals(entry, cachedEntry))
            {
                throw new InvalidOperationException(
                    "The resource handle does not belong to this ResourceManager.");
            }

            ReleaseEntry(entry);
        }

        [Obsolete("Use Unload(IResource) instead.")]
        public void UnLoad(IResource resource)
        {
            Unload(resource);
        }

        public void UnloadResource(string address)
        {
            ReleaseResource(address);
        }

        public int GetResourceReferenceCount(string address)
        {
            EnsureInitialized();
            string normalizedAddress = NormalizeAddress(address);
            return _resources.TryGetValue(normalizedAddress, out ResourceEntry entry)
                ? entry.ReferenceCount
                : 0;
        }

        public void Update()
        {
            if (!_initialized)
            {
                return;
            }

            if (!_editorMode)
            {
                _bundleManager.Update();
            }

            for (int index = _asyncResources.Count - 1; index >= 0; index--)
            {
                ResourceEntry entry = _asyncResources[index];
                AdvanceAsyncResource(entry);
                if (entry.Done)
                {
                    _asyncResources.Remove(entry);
                }
            }
        }

        public void LateUpdate()
        {
            if (!_initialized)
            {
                return;
            }

            int checkCount = _pendingUnload.Count;
            for (int index = 0; index < checkCount; index++)
            {
                ResourceEntry entry = _pendingUnload.First.Value;
                _pendingUnload.RemoveFirst();

                if (entry.ReferenceCount > 0)
                {
                    entry.WaitingForUnload = false;
                    continue;
                }

                if (!entry.Done)
                {
                    _pendingUnload.AddLast(entry);
                    continue;
                }

                entry.WaitingForUnload = false;
                if (_resources.TryGetValue(entry.Address, out ResourceEntry cachedEntry) &&
                    ReferenceEquals(entry, cachedEntry))
                {
                    _resources.Remove(entry.Address);
                }

                entry.Asset = null;
                if (entry.BundleAcquired)
                {
                    _bundleManager.ReleaseBundle(entry.BundleName);
                    entry.BundleAcquired = false;
                }

                ReleaseDependencies(entry);
            }

            if (!_editorMode)
            {
                _bundleManager.LateUpdate();
            }
        }

        public void UnloadAll(bool unloadAllLoadedObjects = false)
        {
            foreach (ResourceEntry entry in _resources.Values)
            {
                if (!entry.Done)
                {
                    entry.Completion.TrySetCanceled();
                }

                entry.Asset = null;
                entry.ReferenceCount = 0;
            }

            _asyncResources.Clear();
            _pendingUnload.Clear();
            _resources.Clear();

            if (!_editorMode)
            {
                _bundleManager.UnloadAll(unloadAllLoadedObjects);
            }

            _initialized = false;
            DetachDriver();
        }

        private T LoadResourceInternal<T>(
            string address,
            string assetPath,
            string bundleName,
            IReadOnlyList<string> dependencyAddresses)
            where T : UnityEngine.Object
        {
            ValidateAssetPath(assetPath);
            if (!_loadingAddresses.Add(address))
            {
                throw new InvalidOperationException(
                    $"Circular resource load detected at '{address}'.");
            }

            ResourceEntry entry = null;
            try
            {
                if (_resources.TryGetValue(address, out ResourceEntry cachedEntry))
                {
                    if (IsCanceledEntry(cachedEntry))
                    {
                        DiscardCanceledEntry(cachedEntry);
                    }
                    else
                    {
                        EnsureSynchronousEntry<T>(cachedEntry);
                        Reactivate(cachedEntry);
                        cachedEntry.ReferenceCount++;
                        return (T)cachedEntry.Asset;
                    }
                }

                entry = CreateEntry(address, assetPath, bundleName, typeof(T));
                entry.ReferenceCount = 1;
                _resources.Add(address, entry);

                // 与示例一致：资源第一次进入缓存时只取得一次直接依赖引用。
                // 后续命中缓存只增加当前资源引用，不重复增加依赖引用。
                entry.Dependencies = AcquireDependencies(dependencyAddresses);

#if UNITY_EDITOR
                if (_editorMode)
                {
                    entry.Asset = AssetDatabase.LoadAssetAtPath(assetPath, typeof(T));
                }
                else
#endif
                {
                    AssetBundle bundle = _bundleManager.LoadBundle(bundleName);
                    entry.BundleAcquired = true;
                    entry.Asset = bundle.LoadAsset<T>(assetPath);
                }

                if (entry.Asset == null)
                {
                    throw new InvalidDataException($"Asset '{assetPath}' could not be loaded.");
                }

                entry.Done = true;
                entry.Completion.TrySetResult(entry.Asset);
                return (T)entry.Asset;
            }
            catch
            {
                if (entry != null)
                {
                    _resources.Remove(address);
                    entry.ReferenceCount = 0;
                    if (entry.BundleAcquired)
                    {
                        _bundleManager.ReleaseBundle(bundleName);
                        _bundleManager.LateUpdate();
                    }

                    ReleaseDependencies(entry);
                }

                throw;
            }
            finally
            {
                _loadingAddresses.Remove(address);
            }
        }

        private ResourceEntry LoadResourceEntryAsync(
            string address,
            string assetPath,
            string bundleName,
            Type requestedType,
            IReadOnlyList<string> dependencyAddresses)
        {
            ValidateAssetPath(assetPath);
            if (!_loadingAddresses.Add(address))
            {
                throw new InvalidOperationException(
                    $"Circular resource load detected at '{address}'.");
            }

            try
            {
                if (_resources.TryGetValue(address, out ResourceEntry cachedEntry))
                {
                    if (IsCanceledEntry(cachedEntry))
                    {
                        DiscardCanceledEntry(cachedEntry);
                    }
                    else
                    {
                        EnsureCompatibleType(cachedEntry, requestedType);
                        Reactivate(cachedEntry);
                        cachedEntry.ReferenceCount++;
                        return cachedEntry;
                    }
                }

                ResourceEntry entry = CreateEntry(address, assetPath, bundleName, requestedType);
                entry.ReferenceCount = 1;
                _resources.Add(address, entry);

                try
                {
                    // 示例实现中异步主资源的依赖仍同步取得；这里保持相同语义，
                    // 确保主资源异步完成时所有直接和传递依赖都已可用。
                    entry.Dependencies = AcquireDependencies(dependencyAddresses);
#if UNITY_EDITOR
                    if (_editorMode)
                    {
                        entry.Asset = AssetDatabase.LoadAssetAtPath(assetPath, requestedType);
                        if (entry.Asset == null)
                        {
                            throw new InvalidDataException($"Asset '{assetPath}' could not be loaded.");
                        }

                        entry.Done = true;
                        entry.Completion.TrySetResult(entry.Asset);
                        return entry;
                    }
#endif

                    entry.BundleOperation = _bundleManager.LoadBundleAsync(bundleName);
                    _asyncResources.Add(entry);
                    AdvanceAsyncResource(entry);
                    if (entry.Done)
                    {
                        _asyncResources.Remove(entry);
                    }

                    return entry;
                }
                catch (Exception exception)
                {
                    FailEntry(entry, exception);
                    return entry;
                }
            }
            finally
            {
                _loadingAddresses.Remove(address);
            }
        }

        private static bool IsCanceledEntry(ResourceEntry entry)
        {
            return entry.ReferenceCount == 0 && entry.Done && entry.Completion.Task.IsCanceled;
        }

        private void DiscardCanceledEntry(ResourceEntry entry)
        {
            if (entry.WaitingForUnload)
            {
                _pendingUnload.Remove(entry);
                entry.WaitingForUnload = false;
            }

            _resources.Remove(entry.Address);
            if (entry.BundleAcquired)
            {
                _bundleManager.ReleaseBundle(entry.BundleName);
                entry.BundleAcquired = false;
            }
        }

        private void AdvanceAsyncResource(ResourceEntry entry)
        {
            if (entry.Done)
            {
                return;
            }

            if (entry.BundleOperation != null)
            {
                if (!entry.BundleOperation.IsDone)
                {
                    return;
                }

                if (entry.BundleOperation.Error != null)
                {
                    FailEntry(entry, entry.BundleOperation.Error);
                    return;
                }

                entry.BundleAcquired = true;
                if (entry.CancelRequested)
                {
                    CancelEntry(entry);
                    return;
                }

                entry.AssetRequest = entry.BundleOperation.AssetBundle.LoadAssetAsync(
                    entry.AssetPath,
                    entry.RequestedType);
                entry.BundleOperation = null;
            }

            if (entry.AssetRequest == null || !entry.AssetRequest.isDone)
            {
                return;
            }

            if (entry.CancelRequested)
            {
                CancelEntry(entry);
                return;
            }

            entry.Asset = entry.AssetRequest.asset;
            entry.AssetRequest = null;
            if (entry.Asset == null)
            {
                FailEntry(entry, new InvalidDataException(
                    $"Asset '{entry.AssetPath}' could not be loaded from bundle '{entry.BundleName}'."));
                return;
            }

            entry.Done = true;
            entry.Completion.TrySetResult(entry.Asset);
        }

        private void FailEntry(ResourceEntry entry, Exception exception)
        {
            entry.Error = exception;
            entry.Done = true;
            entry.ReferenceCount = 0;
            entry.AssetRequest = null;

            if (entry.WaitingForUnload)
            {
                _pendingUnload.Remove(entry);
                entry.WaitingForUnload = false;
            }

            if (_resources.TryGetValue(entry.Address, out ResourceEntry cachedEntry) &&
                ReferenceEquals(entry, cachedEntry))
            {
                _resources.Remove(entry.Address);
            }

            if (entry.BundleAcquired)
            {
                _bundleManager.ReleaseBundle(entry.BundleName);
                entry.BundleAcquired = false;
            }

            ReleaseDependencies(entry);

            entry.Completion.TrySetException(exception);
        }

        private void CancelEntry(ResourceEntry entry)
        {
            entry.Done = true;
            entry.AssetRequest = null;
            entry.Completion.TrySetCanceled();
        }

        private void ReleaseEntry(ResourceEntry entry)
        {
            if (entry.ReferenceCount <= 0)
            {
                throw new InvalidOperationException(
                    $"Resource '{entry.Address}' reference count is already zero.");
            }

            entry.ReferenceCount--;
            if (entry.ReferenceCount != 0)
            {
                return;
            }

            if (!entry.Done)
            {
                entry.CancelRequested = true;
            }

            if (!entry.WaitingForUnload)
            {
                entry.WaitingForUnload = true;
                _pendingUnload.AddLast(entry);
            }
        }

        private void Reactivate(ResourceEntry entry)
        {
            if (entry.ReferenceCount == 0 && entry.WaitingForUnload)
            {
                _pendingUnload.Remove(entry);
                entry.WaitingForUnload = false;
                entry.CancelRequested = false;
            }
        }

        private ResourceEntry[] AcquireDependencies(IReadOnlyList<string> dependencyAddresses)
        {
            if (_editorMode || dependencyAddresses == null || dependencyAddresses.Count == 0)
            {
                return null;
            }

            List<ResourceEntry> acquired = new(dependencyAddresses.Count);
            try
            {
                foreach (string dependencyAddress in dependencyAddresses)
                {
                    ResourceInfo dependency = _bundleManager.GetResourceInfo(dependencyAddress);
                    LoadResourceInternal<UnityEngine.Object>(
                        dependency.address,
                        dependency.assetPath,
                        dependency.bundleName,
                        dependency.directDependencies);
                    acquired.Add(_resources[dependency.address]);
                }

                return acquired.ToArray();
            }
            catch
            {
                for (int index = acquired.Count - 1; index >= 0; index--)
                {
                    ReleaseEntry(acquired[index]);
                }

                throw;
            }
        }

        private void ReleaseDependencies(ResourceEntry entry)
        {
            if (entry.Dependencies == null)
            {
                return;
            }

            foreach (ResourceEntry dependency in entry.Dependencies)
            {
                if (dependency != null)
                {
                    ReleaseEntry(dependency);
                }
            }

            entry.Dependencies = null;
        }

        private static ResourceEntry CreateEntry(
            string address,
            string assetPath,
            string bundleName,
            Type requestedType)
        {
            return new ResourceEntry
            {
                Address = address,
                AssetPath = assetPath,
                BundleName = bundleName,
                RequestedType = requestedType
            };
        }

        private static void EnsureSynchronousEntry<T>(ResourceEntry entry)
            where T : UnityEngine.Object
        {
            if (!entry.Done)
            {
                throw new InvalidOperationException(
                    $"Resource '{entry.Address}' is loading asynchronously and cannot be loaded synchronously.");
            }

            if (entry.Error != null)
            {
                throw entry.Error;
            }

            if (entry.Asset is not T)
            {
                throw new InvalidCastException(
                    $"Resource '{entry.Address}' was loaded as '{entry.RequestedType.Name}', not '{typeof(T).Name}'.");
            }
        }

        private static void EnsureCompatibleType(ResourceEntry entry, Type requestedType)
        {
            if (entry.Done && entry.Asset != null)
            {
                if (!requestedType.IsInstanceOfType(entry.Asset))
                {
                    throw new InvalidCastException(
                        $"Resource '{entry.Address}' is not compatible with '{requestedType.Name}'.");
                }

                return;
            }

            if (entry.RequestedType != requestedType)
            {
                throw new InvalidOperationException(
                    $"Resource '{entry.Address}' is already loading as '{entry.RequestedType.Name}'.");
            }
        }

        private static async Task<T> ConvertTask<T>(Task<UnityEngine.Object> task)
            where T : UnityEngine.Object
        {
            UnityEngine.Object asset = await task;
            return (T)asset;
        }

        private static string NormalizeAddress(string address)
        {
            return ManifestIndex.Normalize(address);
        }

        private static void ValidateAssetPath(string assetPath)
        {
            if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    "Load the scene bundle, then use SceneManager.LoadSceneAsync for scenes.");
            }
        }

        private void EnsureInitialized()
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("Call Initialize() first.");
            }
        }

        private void AttachDriver()
        {
            if (!Application.isPlaying || _driver != null)
            {
                return;
            }

            GameObject driverObject = new("MyAssetBundleFramework.ResourceManager");
            driverObject.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(driverObject);
            _driver = driverObject.AddComponent<ResourceManagerDriver>();
            _driver.Initialize(this);
        }

        private void DetachDriver()
        {
            if (_driver == null)
            {
                return;
            }

            ResourceManagerDriver driver = _driver;
            _driver = null;
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(driver.gameObject);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(driver.gameObject);
            }
        }

        internal void OnDriverDestroyed(ResourceManagerDriver driver)
        {
            if (ReferenceEquals(_driver, driver))
            {
                _driver = null;
            }
        }
    }

    internal sealed class ResourceManagerDriver : MonoBehaviour
    {
        private ResourceManager _manager;

        internal void Initialize(ResourceManager manager)
        {
            _manager = manager;
        }

        private void Update()
        {
            _manager?.Update();
        }

        private void LateUpdate()
        {
            _manager?.LateUpdate();
        }

        private void OnDestroy()
        {
            ResourceManager manager = _manager;
            _manager = null;
            manager?.OnDriverDestroyed(this);
        }
    }
}
