using System;
using System.Collections.Generic;
using System.IO;

namespace MyAssetBundleFramework.Manifest
{
    /// <summary>
    /// 运行时清单的只读查询索引。
    /// 初始化阶段完成路径规范化、引用校验、循环检测和加载顺序缓存，
    /// 运行时加载阶段不再重复遍历和验证整张依赖图。
    /// </summary>
    public sealed class ManifestIndex
    {
        private readonly Dictionary<string, ResourceInfo> _resources =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BundleInfo> _bundles =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> _resourceLoadOrders =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> _bundleLoadOrders =
            new(StringComparer.OrdinalIgnoreCase);

        public ManifestIndex(RuntimeManifest manifest)
        {
            if (manifest == null || manifest.version != 2 ||
                manifest.resources == null || manifest.bundles == null)
            {
                throw new InvalidDataException("Invalid or unsupported runtime manifest.");
            }

            // 先建立 Bundle 索引，资源项才能验证自己的 Bundle 归属。
            foreach (BundleInfo source in manifest.bundles)
            {
                BundleInfo bundle = NormalizeBundle(source);
                if (!_bundles.TryAdd(bundle.bundleName, bundle))
                {
                    throw new InvalidDataException($"Duplicate bundle: {bundle.bundleName}");
                }
            }

            foreach (BundleInfo bundle in _bundles.Values)
            {
                foreach (string dependency in bundle.directDependencies)
                {
                    GetBundle(dependency);
                }
            }

            foreach (ResourceInfo source in manifest.resources)
            {
                ResourceInfo resource = NormalizeResource(source);
                GetBundle(resource.bundleName);
                if (!_resources.TryAdd(resource.address, resource))
                {
                    throw new InvalidDataException(
                        $"Duplicate resource address: {resource.address}");
                }
            }

            foreach (ResourceInfo resource in _resources.Values)
            {
                foreach (string dependency in resource.directDependencies)
                {
                    GetResource(dependency);
                }
            }

            // visiting 用于识别环，visited 只用于同一次 DFS 内去重。
            foreach (string bundleName in _bundles.Keys)
            {
                List<string> order = new();
                VisitBundle(
                    bundleName,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    order);
                _bundleLoadOrders.Add(bundleName, order.ToArray());
            }

            foreach (string address in _resources.Keys)
            {
                List<string> order = new();
                VisitResource(
                    address,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    order);
                _resourceLoadOrders.Add(address, order.ToArray());
            }
        }

        public ResourceInfo GetResource(string address)
        {
            string normalized = Normalize(address);
            if (!_resources.TryGetValue(normalized, out ResourceInfo resource))
            {
                throw new KeyNotFoundException($"Resource not found: {address}");
            }

            return resource;
        }

        public BundleInfo GetBundle(string name)
        {
            string normalized = Normalize(name);
            if (!_bundles.TryGetValue(normalized, out BundleInfo bundle))
            {
                throw new InvalidDataException($"Bundle not found in manifest: {name}");
            }

            return bundle;
        }

        /// <summary>返回依赖优先、目标 Bundle 最后的稳定加载顺序。</summary>
        public List<string> GetLoadOrder(string bundleName)
        {
            string canonicalName = GetBundle(bundleName).bundleName;
            return new List<string>(_bundleLoadOrders[canonicalName]);
        }

        /// <summary>返回依赖优先、目标资源最后的稳定加载顺序。</summary>
        public List<string> GetResourceLoadOrder(string address)
        {
            string canonicalAddress = GetResource(address).address;
            return new List<string>(_resourceLoadOrders[canonicalAddress]);
        }

        private void VisitBundle(
            string name,
            HashSet<string> visiting,
            HashSet<string> visited,
            List<string> order)
        {
            BundleInfo bundle = GetBundle(name);
            string canonicalName = bundle.bundleName;
            if (visited.Contains(canonicalName))
            {
                return;
            }

            if (!visiting.Add(canonicalName))
            {
                throw new InvalidDataException(
                    $"Circular Bundle dependency detected at '{canonicalName}'.");
            }

            foreach (string dependency in bundle.directDependencies)
            {
                VisitBundle(dependency, visiting, visited, order);
            }

            visiting.Remove(canonicalName);
            visited.Add(canonicalName);
            order.Add(canonicalName);
        }

        private void VisitResource(
            string address,
            HashSet<string> visiting,
            HashSet<string> visited,
            List<string> order)
        {
            ResourceInfo resource = GetResource(address);
            string canonicalAddress = resource.address;
            if (visited.Contains(canonicalAddress))
            {
                return;
            }

            if (!visiting.Add(canonicalAddress))
            {
                throw new InvalidDataException(
                    $"Circular resource dependency detected at '{canonicalAddress}'.");
            }

            foreach (string dependency in resource.directDependencies)
            {
                VisitResource(dependency, visiting, visited, order);
            }

            visiting.Remove(canonicalAddress);
            visited.Add(canonicalAddress);
            order.Add(canonicalAddress);
        }

        private static BundleInfo NormalizeBundle(BundleInfo source)
        {
            if (source == null || source.size <= 0 ||
                string.IsNullOrWhiteSpace(source.hash) || source.directDependencies == null)
            {
                throw new InvalidDataException("Invalid bundle entry.");
            }

            BundleInfo result = new()
            {
                bundleName = Normalize(source.bundleName),
                hash = source.hash,
                size = source.size,
                crc = source.crc
            };

            HashSet<string> dependencies = new(StringComparer.OrdinalIgnoreCase);
            foreach (string dependency in source.directDependencies)
            {
                string normalized = Normalize(dependency);
                if (dependencies.Add(normalized))
                {
                    result.directDependencies.Add(normalized);
                }
            }

            return result;
        }

        private static ResourceInfo NormalizeResource(ResourceInfo source)
        {
            if (source == null || source.directDependencies == null)
            {
                throw new InvalidDataException("Invalid resource entry.");
            }

            ResourceInfo result = new()
            {
                address = Normalize(source.address),
                assetPath = Normalize(source.assetPath),
                bundleName = Normalize(source.bundleName)
            };

            HashSet<string> dependencies = new(StringComparer.OrdinalIgnoreCase);
            foreach (string dependency in source.directDependencies)
            {
                string normalized = Normalize(dependency);
                if (!string.Equals(normalized, result.address, StringComparison.OrdinalIgnoreCase) &&
                    dependencies.Add(normalized))
                {
                    result.directDependencies.Add(normalized);
                }
            }

            return result;
        }

        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path or address cannot be empty.");
            }

            // 常规 Manifest 路径已经使用正斜杠，此时直接复用原字符串，避免无效复制。
            string normalized = path.IndexOf('\\') >= 0
                ? path.Replace('\\', '/')
                : path;

            // 不使用 Split：逐字符验证路径段，避免为每次查询分配数组和子字符串。
            int segmentStart = 0;
            for (int index = 0; index <= normalized.Length; index++)
            {
                if (index < normalized.Length && normalized[index] != '/')
                {
                    if (normalized[index] == ':')
                    {
                        throw new InvalidDataException($"Invalid relative path: {path}");
                    }

                    continue;
                }

                int segmentLength = index - segmentStart;
                bool currentDirectory = segmentLength == 1 &&
                                        normalized[segmentStart] == '.';
                bool parentDirectory = segmentLength == 2 &&
                                       normalized[segmentStart] == '.' &&
                                       normalized[segmentStart + 1] == '.';
                if (segmentLength == 0 || currentDirectory || parentDirectory)
                {
                    throw new InvalidDataException($"Invalid relative path: {path}");
                }

                segmentStart = index + 1;
            }

            return normalized;
        }
    }
}
