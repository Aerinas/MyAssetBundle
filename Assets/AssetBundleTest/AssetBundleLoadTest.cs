using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using MyAssetBundleFramework.ResourceManager;
using UnityEngine;

/// <summary>
/// 游戏内 AssetBundle 冒烟测试。
/// 默认读取 Application.dataPath/AssetBundles 中的真实 Bundle；
/// 也可以在 Editor Inspector 中切换为 AssetDatabase 调试模式。
/// </summary>
public sealed class AssetBundleLoadTest : MonoBehaviour
{
    [Header("运行方式")]
    [SerializeField] private bool runOnStart = true;
    [SerializeField] private bool useAssetDatabaseInEditor;
    [Tooltip("相对 Application.dataPath 的目录，也可以填写绝对路径。")]
    [SerializeField] private string bundleRoot = "AssetBundles";

    [Header("测试资源地址")]
    [SerializeField] private string syncAddress =
        "Assets/AssetBundleTest/UI/Login.txt";
    [SerializeField] private string callbackAddress =
        "Assets/AssetBundleTest/UI/Popup.txt";
    [SerializeField] private string coroutineAddress =
        "Assets/AssetBundleTest/Characters/Hero.txt";

    private ResourceManager resourceManager;
    private Coroutine testCoroutine;
    private int passedCount;

    private void Start()
    {
        if (runOnStart)
        {
            RunTests();
        }
    }

    [ContextMenu("Run AssetBundle Runtime Tests")]
    public void RunTests()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("AssetBundle runtime tests can only run in Play Mode.", this);
            return;
        }

        if (testCoroutine != null)
        {
            Debug.LogWarning("AssetBundle runtime tests are already running.", this);
            return;
        }

        testCoroutine = StartCoroutine(RunTestsCoroutine());
    }

    private IEnumerator RunTestsCoroutine()
    {
        passedCount = 0;
        resourceManager?.UnloadAll(true);
        resourceManager = new ResourceManager();

        try
        {
            InitializeResourceManager();
            Debug.Log($"[AssetBundle Test] Root: {GetBundleRootPath()}", this);
        }
        catch (Exception exception)
        {
            Fail("Initialize", exception);
            testCoroutine = null;
            yield break;
        }

        if (!RunSynchronousCacheTest())
        {
            Cleanup();
            yield break;
        }

        // 归零资源会在帧末卸载。重新加载可以验证待卸载资源会被直接复用。
        yield return null;
        if (!Check(resourceManager.LoadedResourceCount == 0,
                "LateUpdate releases synchronous resources") ||
            !Check(resourceManager.LoadedBundleCount == 0,
                "LateUpdate releases synchronous bundles"))
        {
            Cleanup();
            yield break;
        }

        Task<TextAsset> firstAwaitTask;
        Task<TextAsset> secondAwaitTask;
        try
        {
            firstAwaitTask = LoadWithAwait(syncAddress);
            secondAwaitTask = LoadWithAwait(syncAddress);
        }
        catch (Exception exception)
        {
            Fail("Start await loading", exception);
            Cleanup();
            yield break;
        }

        while (!firstAwaitTask.IsCompleted || !secondAwaitTask.IsCompleted)
        {
            yield return null;
        }

        try
        {
            TextAsset first = firstAwaitTask.GetAwaiter().GetResult();
            TextAsset second = secondAwaitTask.GetAwaiter().GetResult();
            if (!Check(first != null && ReferenceEquals(first, second),
                    "await requests share the cached asset") ||
                !Check(resourceManager.GetResourceReferenceCount(syncAddress) == 2,
                    "await requests increment resource references"))
            {
                Cleanup();
                yield break;
            }

            resourceManager.ReleaseResource(syncAddress);
            resourceManager.ReleaseResource(syncAddress);
        }
        catch (Exception exception)
        {
            Fail("Await loading", exception);
            Cleanup();
            yield break;
        }

        bool callbackFinished = false;
        TextAsset callbackAsset = null;
        Exception callbackError = null;
        resourceManager.LoadResourceWithCallback<TextAsset>(
            callbackAddress,
            asset =>
            {
                callbackAsset = asset;
                callbackFinished = true;
            },
            exception =>
            {
                callbackError = exception;
                callbackFinished = true;
            });

        while (!callbackFinished)
        {
            yield return null;
        }

        if (callbackError != null)
        {
            Fail("Callback loading", callbackError);
            Cleanup();
            yield break;
        }

        if (!Check(callbackAsset != null, "callback loads a TextAsset"))
        {
            Cleanup();
            yield break;
        }

        resourceManager.ReleaseResource(callbackAddress);

        TextAsset coroutineAsset = null;
        Exception coroutineError = null;
        yield return resourceManager.LoadResourceCoroutine<TextAsset>(
            coroutineAddress,
            asset => coroutineAsset = asset,
            exception => coroutineError = exception);

        if (coroutineError != null)
        {
            Fail("Coroutine loading", coroutineError);
            Cleanup();
            yield break;
        }

        if (!Check(coroutineAsset != null, "coroutine loads a TextAsset"))
        {
            Cleanup();
            yield break;
        }

        resourceManager.ReleaseResource(coroutineAddress);

        // 给 Resource 和 Bundle 两层延迟队列足够的帧数完成释放。
        yield return null;
        yield return null;
        bool resourcesReleased = Check(resourceManager.LoadedResourceCount == 0,
            "all resource references return to zero");
        bool bundlesReleased = Check(resourceManager.LoadedBundleCount == 0,
            "all bundle references return to zero");
        if (!resourcesReleased || !bundlesReleased)
        {
            Cleanup();
            yield break;
        }

        Debug.Log($"[AssetBundle Test] Passed: {passedCount}", this);
        Cleanup();
    }

    private bool RunSynchronousCacheTest()
    {
        try
        {
            TextAsset first = resourceManager.LoadResource<TextAsset>(syncAddress);
            TextAsset second = resourceManager.LoadResource<TextAsset>(syncAddress);
            if (!Check(first != null && !string.IsNullOrEmpty(first.text),
                    "synchronous load returns content") ||
                !Check(ReferenceEquals(first, second),
                    "repeated synchronous loads reuse the asset") ||
                !Check(resourceManager.GetResourceReferenceCount(syncAddress) == 2,
                    "repeated synchronous loads increment references"))
            {
                return false;
            }

            resourceManager.ReleaseResource(syncAddress);
            if (!Check(resourceManager.GetResourceReferenceCount(syncAddress) == 1,
                    "release decrements the resource reference"))
            {
                return false;
            }

            resourceManager.ReleaseResource(syncAddress);
            TextAsset reactivated = resourceManager.LoadResource<TextAsset>(syncAddress);
            if (!Check(ReferenceEquals(first, reactivated),
                    "same-frame reload cancels delayed unload"))
            {
                return false;
            }

            resourceManager.ReleaseResource(syncAddress);
            return true;
        }
        catch (Exception exception)
        {
            Fail("Synchronous loading", exception);
            return false;
        }
    }

    private async Task<TextAsset> LoadWithAwait(string address)
    {
        return await resourceManager.LoadResourceAsync<TextAsset>(address);
    }

    private void InitializeResourceManager()
    {
#if UNITY_EDITOR
        if (useAssetDatabaseInEditor)
        {
            resourceManager.Initialize(null, true);
            return;
        }
#endif

        resourceManager.Initialize(GetBundleRootPath(), false);
    }

    private string GetBundleRootPath()
    {
        if (string.IsNullOrWhiteSpace(bundleRoot))
        {
            return Path.Combine(Application.dataPath, "AssetBundles");
        }

        return Path.GetFullPath(Path.IsPathRooted(bundleRoot)
            ? bundleRoot
            : Path.Combine(Application.dataPath, bundleRoot));
    }

    private bool Check(bool condition, string description)
    {
        if (!condition)
        {
            Debug.LogError($"[AssetBundle Test] Failed: {description}", this);
            return false;
        }

        passedCount++;
        Debug.Log($"[AssetBundle Test] Passed: {description}", this);
        return true;
    }

    private void Fail(string stage, Exception exception)
    {
        Debug.LogError($"[AssetBundle Test] {stage} failed:\n{exception}", this);
    }

    private void Cleanup()
    {
        resourceManager?.UnloadAll();
        resourceManager = null;
        testCoroutine = null;
    }

    private void OnDestroy()
    {
        if (testCoroutine != null)
        {
            StopCoroutine(testCoroutine);
            testCoroutine = null;
        }

        resourceManager?.UnloadAll();
        resourceManager = null;
    }
}
