using System;
using System.IO;
using UnityEditor;

namespace Native.Editor.Hybrid
{
    [FilePath("ProjectSettings/HybridSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class HybridSettings : ScriptableSingleton<HybridSettings>
    {
        public string businessRemoteRoot = "https://resource.dongyoushixian.com/kele/gh/android";
        public int businessRemoteTimeoutSeconds = 12;
        public string businessPublishVersion = "";
        public string businessOutputPath = GetDefaultBusinessOutputRoot();
        public string mainScenePath = "Assets/Scenes/Main.unity";
        public string bootPackageRemoteRoot = "";

        public string hybridClrPackageName => "com.code-philosophy.hybridclr";
        public string hybridClrPackageSpec => "https://gitee.com/focus-creative-games/hybridclr_unity.git#v8.11.0";
        public BuildTarget buildTarget => BuildTarget.Android;
        public bool autoSwitchBuildTarget => true;
        public bool enforceIl2Cpp => true;
        public bool installHybridClrRuntime => true;
        public int androidCompileSdkVersion => 31;
        public string androidBuildToolsVersion => "30.0.2";
        public string businessAssemblyName => "Assembly-CSharp";
        public string bootstrapScenePath => "Assets/_Native/Runtime/Hybrid/HybridBootstrap.unity";
        public string bootCanvasPrefabPath => "Assets/_Native/Runtime/Hybrid/HybridBootCanvas.prefab";
        public string obsoleteLauncherScenePath => "Assets/_Native/Runtime/Hybrid/HybridLauncher.unity";
        public string bootPackageName => "BootPackage";
        public string buildAdapterPath => "Assets/_Native/Editor/BuildYooAssetsAndroid.cs";
        public string buildPipelineRoot => "Assets/Editor/HybridCLR";
        public string resourceRoot => "Assets/Res/HybridCLR";
        public string runtimeResourcePrefix => "HybridCLR";
        public string collectorSettingsPath => "Assets/AssetBundleCollectorSetting.asset";
        public string patchAotAssemblies => "mscorlib.dll\nSystem.dll\nSystem.Core.dll\nDOTween.dll\nNewtonsoft.Json.dll\nUnity.TextMeshPro.dll\nUnityEngine.CoreModule.dll\nUnityEngine.JSONSerializeModule.dll\nUnityEngine.UI.dll\nYooAsset.dll\nprotobuf-net.dll\nspine-csharp.dll";

        public string GetBusinessOutputRoot()
        {
            var configured = (businessOutputPath ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(configured))
                configured = GetDefaultBusinessOutputRoot();

            configured = Environment.ExpandEnvironmentVariables(configured);
            if (!Path.IsPathRooted(configured))
            {
                var projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath);
                configured = Path.Combine(projectRoot, configured);
            }
            return Path.GetFullPath(configured);
        }

        public string GetBusinessOutputPath(string version)
        {
            return Path.Combine(GetBusinessOutputRoot(), (version ?? string.Empty).Trim(), "BusinessCode");
        }

        public string GetBootPackageOutputPath(string version)
        {
            var projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath);
            return Path.GetFullPath(Path.Combine(
                projectRoot,
                "Bundles",
                buildTarget.ToString(),
                bootPackageName,
                (version ?? string.Empty).Trim()));
        }

        static string GetDefaultBusinessOutputRoot()
        {
            var projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath);
            return Path.GetFullPath(Path.Combine(projectRoot, "Bundles", "Android", "Game"))
                .Replace('\\', '/');
        }

        public void Persist()
        {
            Save(true);
        }
    }
}
