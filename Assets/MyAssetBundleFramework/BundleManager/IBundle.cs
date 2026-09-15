using System;
using UnityEngine;

namespace MyAssetBundleFramework.BundleManager
{
    /// <summary>Bundle 缓存项的只读运行时视图。</summary>
    public interface IBundle
    {
        string Name { get; }
        AssetBundle AssetBundle { get; }
        int ReferenceCount { get; }
        bool IsDone { get; }
        Exception Error { get; }
    }
}
