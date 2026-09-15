using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MyAssetBundleFramework.Manifest;
using MyAssetBundleFramework.ResourceManager;
using UnityEditor;
using UnityEngine;
using ResourceService = MyAssetBundleFramework.ResourceManager.ResourceManager;

namespace MyAssetBundleFramework.PackageBuilder.Editor
{
    public static class FrameworkTests
    {
        private static int _passed;

        [MenuItem("Build/Run Framework Tests")]
        public static void Run()
        {
            _passed = 0;
            TestIndex();
            TestBundles();
            Debug.Log($"Framework tests passed: {_passed}");
        }

        private static RuntimeManifest CreateManifest()
        {
            RuntimeManifest manifest = new();
            manifest.bundles.Add(new BundleInfo { bundleName = "common.bundle", hash = "test", size = 1 });
            manifest.bundles.Add(new BundleInfo
            {
                bundleName = "main.bundle", hash = "test", size = 1,
                directDependencies = new List<string> { "common.bundle" }
            });
            manifest.resources.Add(new ResourceInfo
            {
                address = "Assets/Common.mat", assetPath = "Assets/Common.mat", bundleName = "common.bundle"
            });
            manifest.resources.Add(new ResourceInfo
            {
                address = "Assets/Main.prefab", assetPath = "Assets/Main.prefab", bundleName = "main.bundle",
                directDependencies = new List<string> { "Assets/Common.mat" }
            });
            return manifest;
        }

        private static void TestIndex()
        {
            ManifestIndex index = new(CreateManifest());
            Check(index.GetResource("assets\\MAIN.prefab").bundleName == "main.bundle", "normalized lookup");
            Check(string.Join(",", index.GetLoadOrder("main.bundle")) == "common.bundle,main.bundle", "dependency first");
            Check(string.Join(",", index.GetResourceLoadOrder("Assets/Main.prefab")) ==
                  "Assets/Common.mat,Assets/Main.prefab", "resource dependency first");
            Expect<KeyNotFoundException>(() => index.GetResource("missing"));
            Expect<InvalidDataException>(() => ManifestIndex.Normalize("../escape"));
            RuntimeManifest manifest = CreateManifest();
            manifest.resources.Add(manifest.resources[0]);
            Expect<InvalidDataException>(() => new ManifestIndex(manifest));
            manifest = CreateManifest();
            manifest.bundles.RemoveAt(0);
            Expect<InvalidDataException>(() => new ManifestIndex(manifest));
            manifest = CreateManifest();
            manifest.version = 99;
            Expect<InvalidDataException>(() => new ManifestIndex(manifest));
            manifest = CreateManifest();
            manifest.bundles.Add(manifest.bundles[0]);
            Expect<InvalidDataException>(() => new ManifestIndex(manifest));
            Expect<InvalidDataException>(() => new ManifestIndex(null));
            manifest = CreateManifest();
            manifest.bundles.Add(new BundleInfo
            {
                bundleName = "other.bundle", hash = "test", size = 1,
                directDependencies = new List<string> { "common.bundle" }
            });
            manifest.bundles[1].directDependencies.Add("other.bundle");
            Check(new ManifestIndex(manifest).GetLoadOrder("main.bundle").Count == 3, "diamond deduplication");
            manifest = CreateManifest();
            manifest.resources[0].directDependencies.Add("Assets/Main.prefab");
            Expect<InvalidDataException>(() => new ManifestIndex(manifest));
            manifest = CreateManifest();
            manifest.bundles[0].directDependencies.Add("main.bundle");
            Expect<InvalidDataException>(() => new ManifestIndex(manifest));
        }

        private static void TestBundles()
        {
            string folderName = "FrameworkTest_" + Guid.NewGuid().ToString("N");
            string assetRoot = "Assets/" + folderName;
            string output = Path.GetFullPath(Path.Combine("Temp", folderName));
            AssetDatabase.CreateFolder("Assets", folderName);
            Directory.CreateDirectory(output);
            ResourceService manager = new();
            try
            {
                Expect<InvalidOperationException>(() => manager.LoadResource<GameObject>("missing"));
                Expect<FileNotFoundException>(() => manager.Initialize(output));
                string shaderPath = assetRoot + "/Test.shader";
                File.WriteAllText(shaderPath, "Shader \"Framework/Test\" { SubShader { Pass {} } }");
                AssetDatabase.ImportAsset(shaderPath);
                Material material = new(AssetDatabase.LoadAssetAtPath<Shader>(shaderPath));
                string materialPath = assetRoot + "/Shared.mat";
                AssetDatabase.CreateAsset(material, materialPath);
                string firstPath = assetRoot + "/First.prefab";
                string secondPath = assetRoot + "/Second.prefab";
                foreach (string path in new[] { firstPath, secondPath })
                {
                    GameObject instance = new("Fixture");
                    try
                    {
                        instance.AddComponent<MeshRenderer>().sharedMaterial = material;
                        PrefabUtility.SaveAsPrefabAsset(instance, path);
                    }
                    finally { UnityEngine.Object.DestroyImmediate(instance); }
                }
                AssetDatabase.SaveAssets();
                manager.Initialize(null, true);
                bool editorCallbackInvoked = false;
                manager.LoadResourceWithCallback<GameObject>(firstPath, asset =>
                {
                    editorCallbackInvoked = asset != null;
                });
                Check(editorCallbackInvoked, "editor AssetDatabase callback loading");
                var editorTask = manager.LoadResourceAsync<GameObject>(firstPath);
                Check(editorTask.IsCompleted && editorTask.Result != null, "editor awaitable loading");
                IResource editorHandle = manager.Load(firstPath, false);
                Check(editorHandle.Asset != null && editorHandle.IsDone, "observable resource handle");
                Check(manager.GetResourceReferenceCount(firstPath) == 3, "editor resource cache reuse");
                manager.Unload(editorHandle);
                manager.ReleaseResource(firstPath);
                manager.ReleaseResource(firstPath);
                manager.LateUpdate();
                Check(manager.LoadedResourceCount == 0, "editor delayed resource unload");
                manager.UnloadAll();
                List<AssetBundleBuild> builds = new()
                {
                    new() { assetBundleName = "first.bundle", assetNames = new[] { firstPath } },
                    new() { assetBundleName = "second.bundle", assetNames = new[] { secondPath } }
                };
                var dependencies = (Dictionary<string, HashSet<string>>)InvokeBuilder("CollectDependencyList", builds);
                Check(dependencies["first.bundle"].Contains(materialPath) && dependencies["first.bundle"].Contains(shaderPath),
                    "recursive direct dependency collection");
                InvokeBuilder("AddSharedDependencyBundle", builds, dependencies);
                Check(builds.Count == 3 && Array.IndexOf(builds[2].assetNames, materialPath) >= 0, "common extraction");
                var map = (Dictionary<string, string>)InvokeBuilder("CollectResourceBundleMap", builds);
                AssetBundleManifest built = BuildPipeline.BuildAssetBundles(output, builds.ToArray(),
                    BuildAssetBundleOptions.ChunkBasedCompression, EditorUserBuildSettings.activeBuildTarget);
                Check(built != null, "real bundle build");
                Builder.WriteRuntimeManifest(output, map, built);
                string catalogPath = Path.Combine(output, RuntimeManifest.FileName);
                string json = File.ReadAllText(catalogPath);
                RuntimeManifest catalog = JsonUtility.FromJson<RuntimeManifest>(json);
                Check(catalog.resources.Count == 4 && catalog.bundles.Count == 3, "catalog contents");
                Check(new ManifestIndex(catalog).GetLoadOrder("first.bundle")[0] == "shared_dependencies.bundle", "built dependencies");
                Check(new ManifestIndex(catalog).GetResourceLoadOrder(firstPath).Count == 3,
                    "built resource dependency graph");
                manager.Initialize(output);
                GameObject first = manager.LoadResource<GameObject>(firstPath);
                Check(first != null && manager.LoadedBundleCount == 2, "resource and dependency loading");
                Check(manager.LoadResource<GameObject>(firstPath) == first && manager.LoadedBundleCount == 2, "cache reuse");
                Check(manager.GetResourceReferenceCount(firstPath) == 2, "resource reference increment");
                manager.ReleaseResource(firstPath);
                Check(manager.GetResourceReferenceCount(firstPath) == 1, "resource reference decrement");
                manager.ReleaseResource(firstPath);
                Check(manager.GetResourceReferenceCount(firstPath) == 0 && manager.LoadedBundleCount == 2,
                    "zero reference waits for LateUpdate");
                Check(manager.LoadResource<GameObject>(firstPath) == first &&
                      manager.GetResourceReferenceCount(firstPath) == 1,
                    "pending unload reactivation");
                manager.LateUpdate();
                Check(manager.LoadedResourceCount == 3 && manager.LoadedBundleCount == 2,
                    "reactivated resource survives LateUpdate");
                manager.ReleaseResource(firstPath);
                DrainLateUpdates(manager);
                Check(manager.LoadedResourceCount == 0 && manager.LoadedBundleCount == 0,
                    "resource and bundle delayed unload");
                first = manager.LoadResource<GameObject>(firstPath);
                GameObject second = manager.LoadResource<GameObject>(secondPath);
                Check(first.GetComponent<MeshRenderer>().sharedMaterial == second.GetComponent<MeshRenderer>().sharedMaterial,
                    "shared material identity");
                Check(manager.LoadedBundleCount == 3, "common loaded once");
                Expect<KeyNotFoundException>(() => manager.LoadResource<GameObject>("missing"));
                manager.UnloadAll(true);
                Check(manager.LoadedBundleCount == 0, "unload resets cache");
                manager.Initialize(output);
                Check(manager.LoadResource<GameObject>(firstPath) != null, "reinitialize");
                manager.UnloadAll(true);
                catalog.bundles.Find(bundle => bundle.bundleName == "first.bundle").size++;
                File.WriteAllText(catalogPath, JsonUtility.ToJson(catalog));
                manager.Initialize(output);
                Expect<InvalidDataException>(() => manager.LoadResource<GameObject>(firstPath));
                DrainLateUpdates(manager);
                Check(manager.LoadedBundleCount == 0, "failed load rollback");
                Check(manager.LoadResource<GameObject>(secondPath) != null, "other resource after failure");
                Expect<InvalidDataException>(() => manager.LoadResource<GameObject>(firstPath));
                Check(manager.LoadedBundleCount == 2, "rollback preserves existing cache");
                manager.UnloadAll(true);
                catalog = JsonUtility.FromJson<RuntimeManifest>(json);
                BundleInfo firstInfo = catalog.bundles.Find(bundle => bundle.bundleName == "first.bundle");
                firstInfo.crc = firstInfo.crc == 1 ? 2u : 1u;
                File.WriteAllText(catalogPath, JsonUtility.ToJson(catalog));
                manager.Initialize(output);
                Expect<InvalidDataException>(() => manager.LoadResource<GameObject>(firstPath));
                DrainLateUpdates(manager);
                Check(manager.LoadedBundleCount == 0, "CRC failure rollback");
                manager.UnloadAll(true);
                File.WriteAllText(catalogPath, json);
                File.Move(Path.Combine(output, "first.bundle"), Path.Combine(output, "first.saved"));
                manager.Initialize(output);
                Expect<FileNotFoundException>(() => manager.LoadResource<GameObject>(firstPath));
                DrainLateUpdates(manager);
                Check(manager.LoadedBundleCount == 0, "missing file rollback");
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

        private static object InvokeBuilder(string name, params object[] arguments)
        {
            return typeof(Builder).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, arguments);
        }

        private static void DrainLateUpdates(ResourceService manager)
        {
            for (int index = 0; index < 8; index++)
            {
                manager.LateUpdate();
            }
        }

        private static void Check(bool condition, string name)
        {
            if (!condition)
                throw new Exception($"Test failed: {name}");
            _passed++;
        }

        private static void Expect<TException>(Action action) where TException : Exception
        {
            try { action(); }
            catch (TException) { _passed++; return; }
            throw new Exception($"Expected {typeof(TException).Name}");
        }
    }
}
