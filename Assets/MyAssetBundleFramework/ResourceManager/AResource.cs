using System;
using System.Threading.Tasks;
using UnityEngine;
using BundleLoadOperation = MyAssetBundleFramework.BundleManager.BundleLoadOperation;

namespace MyAssetBundleFramework.ResourceManager
{
    /// <summary>
    /// ResourceManager 内部资源状态的公共基类。
    /// 该类型同时是 CustomYieldInstruction，因此 IResource 的实际对象可以直接用于协程等待。
    /// </summary>
    public abstract class AResource : CustomYieldInstruction, IResource
    {
        internal string AssetPath;
        internal string BundleName;
        internal Type RequestedType;
        internal AssetBundleRequest AssetRequest;
        internal BundleLoadOperation BundleOperation;
        internal readonly TaskCompletionSource<UnityEngine.Object> Completion = new();
        internal bool BundleAcquired;
        internal bool CancelRequested;
        internal bool WaitingForUnload;
        internal bool Done;
        internal ResourceEntry[] Dependencies;

        public string Address { get; internal set; }
        public UnityEngine.Object Asset { get; internal set; }
        public Type AssetType => RequestedType;
        public int ReferenceCount { get; internal set; }
        public bool IsDone => Done;
        public Exception Error { get; internal set; }
        public override bool keepWaiting => !Done;
    }

    /// <summary>当前实现使用的资源状态记录；具体加载策略由 ResourceManager 推进。</summary>
    internal sealed class ResourceEntry : AResource
    {
    }
}
