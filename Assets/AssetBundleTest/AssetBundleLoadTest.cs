using MyAssetBundleFramework.ResourceManager;
using UnityEngine;

public sealed class AssetBundleLoadTest : MonoBehaviour
{
    private const string LoginBundleName = "assetbundletest_ui_login.bundle";
    private const string LoginAssetName = "Assets/AssetBundleTest/UI/Login.txt";

    private ResourceManager _resourceManager;

    private void Start()
    {
        _resourceManager = new ResourceManager();
        _resourceManager.Initialize();

        TextAsset loginText = _resourceManager.LoadResource<TextAsset>(
            LoginBundleName,
            LoginAssetName);

        if (loginText != null)
        {
            Debug.Log($"AssetBundle test succeeded: {loginText.text}");
        }
    }

    private void OnDestroy()
    {
        _resourceManager?.UnloadAll();
    }
}
