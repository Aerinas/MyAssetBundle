using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MyAssetBundleFramework.Manifest;
using MyAssetBundleFramework.ResourceManager;
using UnityEditor;
using UnityEngine;

namespace MyAssetBundleFramework.PackageBuilder.Editor
{
    public static class FrameworkAsyncTests
    {
        private static IEnumerator _routine;
        private static double _deadline;

        [MenuItem("Build/Run Framework Async Tests")]
        public static void Run()
        {
            if (_routine != null)
                throw new InvalidOperationException("Async tests are already running.");
            _routine = Validate();
            _deadline = EditorApplication.timeSinceStartup + 120;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup > _deadline)
                    throw new TimeoutException("Async framework tests timed out.");
                if (_routine.MoveNext())
                    return;
                Finish(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Finish(1);
            }
        }

        private static void Finish(int exitCode)
        {
            EditorApplication.update -= Tick;
            (_routine as IDisposable)?.Dispose();
            _routine = null;
            if (Application.isBatchMode)
                EditorApplication.Exit(exitCode);
        }

        private static IEnumerator Validate()
        {
            string folderName = "FrameworkAsync_" + Guid.NewGuid().ToString("N");
            string assetRoot = "Assets/" + folderName;
            string output = Path.GetFullPath(Path.Combine("Temp", folderName));
            var manager = new MyAssetBundleFramework.ResourceManager.ResourceManager();
            AssetDatabase.CreateFolder("Assets", folderName);
            Directory.CreateDirectory(output);
            try
            {
                string mainPath = assetRoot + "/Main.txt";
                string dependencyPath = assetRoot + "/Dependency.txt";
                File.WriteAllText(mainPath, "main");
                File.WriteAllText(dependencyPath, "dependency");
                AssetDatabase.Refresh();
                AssetBundleBuild[] builds =
                {
                    new() { assetBundleName = "main.bundle", assetNames = new[] { mainPath } },
                    new() { assetBundleName = "dependency.bundle", assetNames = new[] { dependencyPath } }
                };
                AssetBundleManifest built = BuildPipeline.BuildAssetBundles(output, builds,
                    BuildAssetBundleOptions.ChunkBasedCompression, EditorUserBuildSettings.activeBuildTarget);
                Builder.WriteRuntimeManifest(output, new Dictionary<string, string>
                {
                    [mainPath] = "main.bundle", [dependencyPath] = "dependency.bundle"
                }, built);
                string manifestPath = Path.Combine(output, RuntimeManifest.FileName);
                RuntimeManifest manifest = JsonUtility.FromJson<RuntimeManifest>(File.ReadAllText(manifestPath));
                manifest.resources.Find(resource => resource.address == mainPath).directDependencies.Add(dependencyPath);
                File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest));
                manager.Initialize(output, false);

                IResource first = manager.Load(mainPath, true);
                IResource duplicate = manager.Load(mainPath, true);
                Require(ReferenceEquals(first, duplicate) && first.ReferenceCount == 2, "in-flight request sharing");
                manager.Unload(first);
                manager.Unload(duplicate);
                IResource revived = manager.Load(mainPath, true);
                Require(ReferenceEquals(first, revived), "same-frame request reactivation");
                while (!revived.IsDone)
                {
                    manager.Update();
                    yield return null;
                }
                Require(revived.GetAwaiter().GetResult() is TextAsset, "handle await result");
                manager.Unload(revived);
                for (int frame = 0; frame < 4; frame++)
                    manager.LateUpdate();

                IResource canceled = manager.Load(mainPath, true);
                manager.Unload(canceled);
                while (!canceled.IsDone)
                {
                    manager.Update();
                    yield return null;
                }
                Require(canceled.Error is OperationCanceledException, "observable cancellation");
                try { canceled.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                IResource retried = manager.Load(mainPath, true);
                Require(!ReferenceEquals(canceled, retried), "completed cancellation creates new handle");
                while (!retried.IsDone)
                {
                    manager.Update();
                    yield return null;
                }
                Require(retried.GetAwaiter().GetResult() != null, "retry succeeds");
                manager.Unload(retried);
                for (int frame = 0; frame < 4; frame++)
                    manager.LateUpdate();
                Require(manager.LoadedResourceCount == 0 && manager.LoadedBundleCount == 0,
                    "canceled retry releases all dependency references");
                Debug.Log("Framework async tests passed: sharing, reactivation, await, cancellation, retry, dependency cleanup");
            }
            finally
            {
                manager.UnloadAll(true);
                AssetDatabase.DeleteAsset(assetRoot);
                if (Directory.Exists(output) && Path.GetFileName(output) == folderName &&
                    Path.GetDirectoryName(output) == Path.GetFullPath("Temp"))
                    Directory.Delete(output, true);
            }
        }

        private static void Require(bool condition, string name)
        {
            if (!condition)
                throw new Exception("Async test failed: " + name);
        }
    }
}
