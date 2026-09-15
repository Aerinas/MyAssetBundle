using System;
using UnityEngine;

namespace MyAssetBundleFramework.ResourceManager
{
    /// <summary>
    /// 资源系统返回的稳定句柄。
    /// 每次成功取得句柄都会增加一次引用，使用结束后必须交还给 ResourceManager。
    /// </summary>
    public interface IResource
    {
        string Address { get; }
        UnityEngine.Object Asset { get; }
        Type AssetType { get; }
        int ReferenceCount { get; }
        bool IsDone { get; }
        Exception Error { get; }
    }
}
