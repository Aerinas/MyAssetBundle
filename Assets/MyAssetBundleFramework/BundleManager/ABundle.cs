using System;
using UnityEngine;

namespace MyAssetBundleFramework.BundleManager
{
    /// <summary>
    /// Bundle 同步与异步状态的公共抽象。
    /// BundleManager 负责修改状态，业务层只能通过 IBundle 观察。
    /// </summary>
    public abstract class ABundle : CustomYieldInstruction, IBundle
    {
        internal AssetBundleCreateRequest Request;
        internal bool WaitingForUnload;

        public string Name { get; internal set; }
        public AssetBundle AssetBundle { get; internal set; }
        public int ReferenceCount { get; internal set; }
        public Exception Error { get; internal set; }
        public bool IsDone => Request == null;
        public override bool keepWaiting => !IsDone;
    }
}
