using System;
using System.Collections.Generic;
using System.IO;
using AssetBundleFrameWork.Editor;
using MyAssetBundleFramework.Manifest;
using UnityEditor;
using UnityEngine;

namespace MyAssetBundleFramework.PackageBuilder.Editor
{
    /// <summary>
    /// AssetBundle 构建入口。
    /// 构建过程先收集主资源，再把所有依赖资源显式归属到唯一 Bundle，
    /// 最终同时生成资源表、Bundle 表和资源直接依赖表。
    /// </summary>
    public static class Builder
    {
        private const string SharedBundleName = "shared_dependencies.bundle";

        [MenuItem("Build/Build Asset Bundles")]
        public static void BuildBundles()
        {
            try
            {
                BuildSetting setting = BuildSetting.LoadFromXml("Assets/BuildSetting.xml");
                List<AssetBundleBuild> builds = CollectAssets(setting);
                if (builds.Count == 0)
                {
                    Debug.LogWarning("No assets were collected for AssetBundle build.");
                    return;
                }

                // 依赖分析必须基于初始主资源，避免后续显式加入的依赖改变拥有者统计。
                Dictionary<string, HashSet<string>> dependencyList =
                    CollectDependencyList(builds);
                AddSharedDependencyBundle(builds, dependencyList);

                // 依赖归属完成后再生成映射，保证每个依赖资源也能按路径独立查询。
                Dictionary<string, string> resourceBundleMap =
                    CollectResourceBundleMap(builds);

                AssetBundleManifest unityManifest = BuildBundles(setting, builds);
                if (unityManifest == null)
                {
                    throw new InvalidDataException("AssetBundle build returned a null manifest.");
                }

                WriteRuntimeManifest(setting.buildRoot, resourceBundleMap, unityManifest);
                LogManifestDependencies(unityManifest);
                AssetDatabase.Refresh();
                Debug.Log($"AssetBundle build completed. Resources: {resourceBundleMap.Count}, " +
                          $"Bundles: {unityManifest.GetAllAssetBundles().Length}.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                throw;
            }
        }

        /// <summary>按 XML 中的目录、后缀和分包方式收集初始主资源。</summary>
        private static List<AssetBundleBuild> CollectAssets(BuildSetting setting)
        {
            List<AssetBundleBuild> builds = new();
            foreach (BuildItem item in setting.buildItems)
            {
                List<string> assetPaths = FindAssetPaths(item, setting.suffixList);
                if (assetPaths.Count == 0)
                {
                    Debug.LogWarning($"No matching assets found under '{item.assetPath}'.");
                    continue;
                }

                switch (item.bundleType)
                {
                    case EBundleType.File:
                        foreach (string assetPath in assetPaths)
                        {
                            builds.Add(new AssetBundleBuild
                            {
                                assetBundleName = CreateBundleName(assetPath, item.suffix),
                                assetNames = new[] { assetPath }
                            });
                        }
                        break;

                    case EBundleType.Directory:
                    case EBundleType.All:
                        builds.Add(new AssetBundleBuild
                        {
                            assetBundleName = CreateBundleName(item.assetPath, item.suffix),
                            assetNames = assetPaths.ToArray()
                        });
                        break;

                    default:
                        throw new InvalidDataException(
                            $"Unsupported BundleType '{item.bundleType}' for '{item.assetPath}'.");
                }
            }

            return builds;
        }

        private static List<string> FindAssetPaths(BuildItem item, string globalSuffixList)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.assetPath))
            {
                throw new InvalidDataException("BuildItem and AssetPath are required.");
            }

            HashSet<string> suffixes = new(StringComparer.OrdinalIgnoreCase);
            if (item.suffixes != null)
            {
                foreach (string suffix in item.suffixes)
                {
                    AddSuffix(suffixes, suffix);
                }
            }

            if (suffixes.Count == 0 && !string.IsNullOrWhiteSpace(globalSuffixList))
            {
                foreach (string suffix in globalSuffixList.Split(';'))
                {
                    AddSuffix(suffixes, suffix);
                }
            }

            string normalizedRoot = item.assetPath.Replace('\\', '/').TrimEnd('/');
            string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { normalizedRoot });
            HashSet<string> assets = new(StringComparer.OrdinalIgnoreCase);

            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid).Replace('\\', '/');
                if (AssetDatabase.IsValidFolder(assetPath) ||
                    !IsAllowedAsset(assetPath, suffixes) ||
                    IsIgnored(assetPath, item.ignorePaths))
                {
                    continue;
                }

                assets.Add(assetPath);
            }

            List<string> result = new(assets);
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static void AddSuffix(HashSet<string> suffixes, string suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix))
            {
                return;
            }

            string normalized = suffix.Trim();
            suffixes.Add(normalized.StartsWith(".") ? normalized : "." + normalized);
        }

        private static bool IsAllowedAsset(string assetPath, HashSet<string> suffixes)
        {
            if (suffixes.Count == 0)
            {
                return true;
            }

            return suffixes.Contains(Path.GetExtension(assetPath));
        }

        private static bool IsIgnored(string assetPath, List<string> ignorePaths)
        {
            if (ignorePaths == null)
            {
                return false;
            }

            foreach (string source in ignorePaths)
            {
                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                string ignoredPath = source.Replace('\\', '/').TrimEnd('/');
                if (string.Equals(assetPath, ignoredPath, StringComparison.OrdinalIgnoreCase) ||
                    assetPath.StartsWith(ignoredPath + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string CreateBundleName(string assetPath, string bundleSuffix)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                throw new ArgumentException("Asset path cannot be empty.", nameof(assetPath));
            }

            string normalizedPath = assetPath.Replace('\\', '/').TrimEnd('/');
            if (normalizedPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = normalizedPath.Substring("Assets/".Length);
            }

            if (!AssetDatabase.IsValidFolder(assetPath))
            {
                normalizedPath = Path.ChangeExtension(normalizedPath, null);
            }

            string suffix = string.IsNullOrWhiteSpace(bundleSuffix)
                ? ".bundle"
                : bundleSuffix.Trim();
            if (!suffix.StartsWith("."))
            {
                suffix = "." + suffix;
            }

            return normalizedPath
                       .Replace('/', '_')
                       .Replace(' ', '_')
                       .ToLowerInvariant() +
                   suffix.ToLowerInvariant();
        }

        /// <summary>建立资源路径到唯一 Bundle 的映射，同时阻止重复归属。</summary>
        private static Dictionary<string, string> CollectResourceBundleMap(
            List<AssetBundleBuild> builds)
        {
            Dictionary<string, string> result =
                new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> bundleNames = new(StringComparer.OrdinalIgnoreCase);

            foreach (AssetBundleBuild build in builds)
            {
                string bundleName = ManifestIndex.Normalize(build.assetBundleName);
                if (!bundleNames.Add(bundleName))
                {
                    throw new InvalidDataException($"Duplicate bundle name: {bundleName}");
                }

                if (build.assetNames == null || build.assetNames.Length == 0)
                {
                    throw new InvalidDataException($"Bundle '{bundleName}' contains no assets.");
                }

                foreach (string source in build.assetNames)
                {
                    string assetPath = ManifestIndex.Normalize(source);
                    if (result.TryGetValue(assetPath, out string owner) &&
                        !string.Equals(owner, bundleName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Resource '{assetPath}' belongs to both '{owner}' and '{bundleName}'.");
                    }

                    result[assetPath] = bundleName;
                }
            }

            return result;
        }

        /// <summary>递归收集每个初始 Bundle 使用到的全部有效依赖资源。</summary>
        private static Dictionary<string, HashSet<string>> CollectDependencyList(
            List<AssetBundleBuild> builds)
        {
            Dictionary<string, HashSet<string>> result =
                new(StringComparer.OrdinalIgnoreCase);

            foreach (AssetBundleBuild build in builds)
            {
                HashSet<string> ownAssets =
                    new(build.assetNames, StringComparer.OrdinalIgnoreCase);
                HashSet<string> dependencies =
                    new(StringComparer.OrdinalIgnoreCase);
                HashSet<string> visited =
                    new(StringComparer.OrdinalIgnoreCase);

                foreach (string assetPath in build.assetNames)
                {
                    CollectDependenciesRecursive(assetPath, ownAssets, dependencies, visited);
                }

                result.Add(build.assetBundleName, dependencies);
            }

            return result;
        }

        private static void CollectDependenciesRecursive(
            string assetPath,
            HashSet<string> ownAssets,
            HashSet<string> dependencies,
            HashSet<string> visited)
        {
            if (!visited.Add(assetPath))
            {
                return;
            }

            foreach (string source in AssetDatabase.GetDependencies(assetPath, false))
            {
                string dependency = source.Replace('\\', '/');
                if (string.Equals(dependency, assetPath, StringComparison.OrdinalIgnoreCase) ||
                    !IsValidDependency(dependency))
                {
                    continue;
                }

                if (!ownAssets.Contains(dependency))
                {
                    dependencies.Add(dependency);
                }

                CollectDependenciesRecursive(dependency, ownAssets, dependencies, visited);
            }
        }

        /// <summary>
        /// 把所有尚未显式归属的依赖资源分配到唯一 Bundle。
        /// 单一拥有者依赖加入拥有者 Bundle；多拥有者依赖加入共享 Bundle。
        /// 方法名保留旧版本名称，避免既有反射测试和工具失效。
        /// </summary>
        private static void AddSharedDependencyBundle(
            List<AssetBundleBuild> builds,
            Dictionary<string, HashSet<string>> dependencyList)
        {
            Dictionary<string, int> buildIndices =
                new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> explicitlyAssigned =
                new(StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < builds.Count; index++)
            {
                AssetBundleBuild build = builds[index];
                if (string.Equals(build.assetBundleName, SharedBundleName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Bundle name '{SharedBundleName}' is reserved.");
                }

                if (!buildIndices.TryAdd(build.assetBundleName, index))
                {
                    throw new InvalidDataException(
                        $"Duplicate bundle name: {build.assetBundleName}");
                }

                foreach (string assetPath in build.assetNames)
                {
                    explicitlyAssigned.Add(assetPath);
                }
            }

            Dictionary<string, HashSet<string>> ownersByDependency =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, HashSet<string>> pair in dependencyList)
            {
                foreach (string dependency in pair.Value)
                {
                    if (explicitlyAssigned.Contains(dependency))
                    {
                        continue;
                    }

                    if (!ownersByDependency.TryGetValue(dependency, out HashSet<string> owners))
                    {
                        owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        ownersByDependency.Add(dependency, owners);
                    }

                    owners.Add(pair.Key);
                }
            }

            Dictionary<string, List<string>> additionsByBundle =
                new(StringComparer.OrdinalIgnoreCase);
            List<string> sharedAssets = new();

            foreach (KeyValuePair<string, HashSet<string>> pair in ownersByDependency)
            {
                if (pair.Value.Count > 1)
                {
                    sharedAssets.Add(pair.Key);
                    continue;
                }

                string owner = null;
                foreach (string candidate in pair.Value)
                {
                    owner = candidate;
                }

                if (!additionsByBundle.TryGetValue(owner, out List<string> additions))
                {
                    additions = new List<string>();
                    additionsByBundle.Add(owner, additions);
                }

                additions.Add(pair.Key);
            }

            foreach (KeyValuePair<string, List<string>> pair in additionsByBundle)
            {
                int buildIndex = buildIndices[pair.Key];
                AssetBundleBuild build = builds[buildIndex];
                List<string> assets = new(build.assetNames);
                pair.Value.Sort(StringComparer.Ordinal);
                assets.AddRange(pair.Value);
                build.assetNames = assets.ToArray();
                builds[buildIndex] = build;
            }

            if (sharedAssets.Count > 0)
            {
                sharedAssets.Sort(StringComparer.Ordinal);
                builds.Add(new AssetBundleBuild
                {
                    assetBundleName = SharedBundleName,
                    assetNames = sharedAssets.ToArray()
                });
            }
        }

        private static AssetBundleManifest BuildBundles(
            BuildSetting setting,
            List<AssetBundleBuild> builds)
        {
            if (string.IsNullOrWhiteSpace(setting.buildRoot))
            {
                throw new InvalidDataException("BuildRoot cannot be empty.");
            }

            string outputPath = setting.buildRoot.Replace('\\', '/').TrimEnd('/');
            Directory.CreateDirectory(outputPath);
            return BuildPipeline.BuildAssetBundles(
                outputPath,
                builds.ToArray(),
                BuildAssetBundleOptions.ChunkBasedCompression,
                EditorUserBuildSettings.activeBuildTarget);
        }

        public static void WriteRuntimeManifest(
            string outputPath,
            Dictionary<string, string> resourceBundleMap,
            AssetBundleManifest unityManifest)
        {
            if (unityManifest == null)
            {
                throw new InvalidDataException(
                    "AssetBundle build failed; runtime manifest cannot be written.");
            }

            RuntimeManifest manifest = new();
            string[] bundleNames = unityManifest.GetAllAssetBundles();
            Array.Sort(bundleNames, StringComparer.Ordinal);

            Dictionary<string, string> canonicalBundleNames =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (string bundleName in bundleNames)
            {
                canonicalBundleNames.Add(bundleName, bundleName);
                string bundlePath = Path.Combine(outputPath, bundleName);
                if (!BuildPipeline.GetCRCForAssetBundle(bundlePath, out uint crc))
                {
                    throw new InvalidDataException($"Unable to read Bundle CRC: {bundlePath}");
                }

                string[] dependencies = unityManifest.GetDirectDependencies(bundleName);
                Array.Sort(dependencies, StringComparer.Ordinal);
                manifest.bundles.Add(new BundleInfo
                {
                    bundleName = bundleName,
                    hash = unityManifest.GetAssetBundleHash(bundleName).ToString(),
                    size = new FileInfo(bundlePath).Length,
                    crc = crc,
                    directDependencies = new List<string>(dependencies)
                });
            }

            List<string> resourcePaths = new(resourceBundleMap.Keys);
            resourcePaths.Sort(StringComparer.Ordinal);
            foreach (string resourcePath in resourcePaths)
            {
                List<string> directDependencies = new();
                foreach (string source in AssetDatabase.GetDependencies(resourcePath, false))
                {
                    string dependency = source.Replace('\\', '/');
                    if (!string.Equals(dependency, resourcePath, StringComparison.OrdinalIgnoreCase) &&
                        resourceBundleMap.ContainsKey(dependency) &&
                        !directDependencies.Contains(dependency))
                    {
                        directDependencies.Add(dependency);
                    }
                }

                directDependencies.Sort(StringComparer.Ordinal);
                string bundleName = resourceBundleMap[resourcePath];
                manifest.resources.Add(new ResourceInfo
                {
                    address = resourcePath,
                    assetPath = resourcePath,
                    bundleName = canonicalBundleNames[bundleName],
                    directDependencies = directDependencies
                });
            }

            // 写盘前再次构建索引，确保不存在缺失目标或资源/Bundle 循环依赖。
            _ = new ManifestIndex(manifest);
            string destination = Path.Combine(outputPath, RuntimeManifest.FileName);
            string temporary = destination + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(manifest, true));
            if (File.Exists(destination))
            {
                File.Replace(temporary, destination, null);
            }
            else
            {
                File.Move(temporary, destination);
            }
        }

        private static void LogManifestDependencies(AssetBundleManifest manifest)
        {
            foreach (string bundleName in manifest.GetAllAssetBundles())
            {
                string[] dependencies = manifest.GetAllDependencies(bundleName);
                Debug.Log(dependencies.Length == 0
                    ? $"Bundle '{bundleName}' has no external dependencies."
                    : $"Bundle '{bundleName}' depends on: {string.Join(", ", dependencies)}");
            }
        }

        private static bool IsValidDependency(string assetPath)
        {
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                AssetDatabase.IsValidFolder(assetPath))
            {
                return false;
            }

            string extension = Path.GetExtension(assetPath);
            return !extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) &&
                   !extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                   !extension.Equals(".asmdef", StringComparison.OrdinalIgnoreCase) &&
                   !extension.Equals(".asmref", StringComparison.OrdinalIgnoreCase);
        }
    }
}
