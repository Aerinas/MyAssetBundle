using System.Collections.Generic;
using System.IO;
using UnityEngine;
using System;
using AssetBundleFrameWork.Editor;
using UnityEditor;

namespace MyAssetBundleFramework.PackageBuilder.Editor
{

    public class Builder
    {
        [MenuItem("Build/Build Asset Bundles")]
        public static void BuildBundles()
        {
            Build();
        }

        private static void Build()
        {
            // 构建资源包的逻辑
            Console.WriteLine("Building asset bundles...");
            // 这里可以添加具体的构建逻辑，比如调用Unity的BuildPipeline.BuildAssetBundles方法

            //收集配置文件
            try
            {

                BuildSetting buildSetting = CollectConfigFiles("Assets/BuildSetting.xml");

                //收集需要打包的资源
                List<AssetBundleBuild> assetBundleBuilds = CollectAssets(buildSetting);

                if (assetBundleBuilds.Count == 0)
                {
                    Console.WriteLine("No assets found to build.");
                    return;
                }
                //构建manifest
                AssetBundleManifest manifest = BuildBundles(buildSetting, assetBundleBuilds);
                
                if (manifest == null)
                {
                    Console.WriteLine("Failed to build asset bundles.");
                    return;
                }
                Console.WriteLine("Asset bundles built successfully.");
                
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error occurred: {ex.Message}");
            }
        }
        
        private static BuildSetting CollectConfigFiles(string xmlFilePath)
        {
            // 收集配置文件的逻辑
            Console.WriteLine("Collecting config files...");
            return BuildSetting.LoadFromXml(xmlFilePath);
        }
        
        private static List<AssetBundleBuild> CollectAssets(BuildSetting buildSetting)
        {
            // 收集需要打包的资源的逻辑
            Console.WriteLine("Collecting assets...");
            
            List<AssetBundleBuild> assetBundleBuilds = new List<AssetBundleBuild>();
            foreach (var item in buildSetting.buildItems)
            {
                Console.WriteLine($"Collecting asset: {item.assetPath}");
                // 这里可以添加具体的资源收集逻辑
                
                List<string> assetPaths = FindAssetPath(item); //收集所有需要打包的资源路径             
                if (assetPaths.Count == 0)
                {
                    Debug.LogWarning($"Can't Found asset path: {item.assetPath}");
                    continue;
                }
                //当打包类型为文件时，直接按每个资源分别打包
                if (item.bundleType == EBundleType.File)
                {
                    foreach (var assetPath in assetPaths)
                    {
                        AssetBundleBuild build = new AssetBundleBuild
                        {
                            assetBundleName = CreateBundleName(assetPath, item.suffix),
                            assetNames = new[] { assetPath }
                        };
                        assetBundleBuilds.Add(build);
                    }
                }
                // 当打包类型为目录时，按目录打包内全部资源
                else if (item.bundleType == EBundleType.Directory)
                {
                    AssetBundleBuild build = new AssetBundleBuild
                    {
                        assetBundleName = CreateBundleName(item.assetPath, item.suffix),
                        assetNames = assetPaths.ToArray()
                    };
                    assetBundleBuilds.Add(build);
                }
            }
            return assetBundleBuilds;
        }

        private static List<string> FindAssetPath(BuildItem item)
        {
            // 这里可以添加具体的逻辑来查找资源路径
            // 例如，使用AssetDatabase.FindAssets来查找资源
            // TODO: path可能是一个文件？
            string[] guids = AssetDatabase.FindAssets("", new[] { item.assetPath });
            List<string> assetPaths = new List<string>();
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                // 排除文件夹
                if (AssetDatabase.IsValidFolder(assetPath))
                {
                    continue;
                }

                // 排除不允许的文件后缀
                if (!IsAllowedAsset(assetPath, item))
                {
                    continue;
                }

                // 排除忽略路径
                if (IsIgnored(assetPath, item))
                {
                    continue;
                }

                // 避免重复
                if (!assetPaths.Contains(assetPath))
                {
                    assetPaths.Add(assetPath);
                }
                
            }
            return assetPaths;
        }
        
        private static bool IsAllowedAsset(string path, BuildItem item)
        {
            // 检查资源路径是否符合允许的后缀规则
            foreach (var suffix in item.suffixes)
            {
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        
        private static bool IsIgnored(string path, BuildItem item)
        {
            // 检查资源路径是否在忽略路径列表中
            foreach (var ignorePath in item.ignorePaths)
            {
                if (path.StartsWith(ignorePath, StringComparison.OrdinalIgnoreCase))
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
                throw new ArgumentException("Asset path cannot be null or empty.", nameof(assetPath));
            }

            string normalizedPath = assetPath.Replace('\\', '/').TrimEnd('/');

            if (normalizedPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = normalizedPath.Substring("Assets/".Length);
            }

            // Remove the source extension so Login.txt becomes login.bundle.
            if (!AssetDatabase.IsValidFolder(assetPath))
            {
                normalizedPath = Path.ChangeExtension(normalizedPath, null);
            }

            string bundleName = normalizedPath
                .Replace('/', '_')
                .Replace(' ', '_')
                .ToLowerInvariant();

            string suffix = string.IsNullOrWhiteSpace(bundleSuffix)
                ? ".bundle"
                : bundleSuffix.Trim();

            if (!suffix.StartsWith("."))
            {
                suffix = "." + suffix;
            }

            return bundleName + suffix.ToLowerInvariant();
        }

        private static AssetBundleManifest BuildBundles(
            BuildSetting buildSetting,
            List<AssetBundleBuild> assetBundleBuilds)
        {
            // 构建manifest的逻辑
            Console.WriteLine("Building manifest...");

            if (string.IsNullOrWhiteSpace(buildSetting.buildRoot))
            {
                throw new InvalidDataException("BuildRoot cannot be null or empty.");
            }

            string outputPath = buildSetting.buildRoot.Replace('\\', '/').TrimEnd('/');
            Directory.CreateDirectory(outputPath);

            AssetBundleManifest manifest = 
               BuildPipeline.BuildAssetBundles(
                     outputPath,
                     assetBundleBuilds.ToArray(), 
                     BuildAssetBundleOptions.ChunkBasedCompression,
                     EditorUserBuildSettings.activeBuildTarget);

            AssetDatabase.Refresh();
            
            return manifest;
        }


    }
}
