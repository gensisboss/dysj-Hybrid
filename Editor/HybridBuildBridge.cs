using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Native.Editor.Hybrid
{
    /// <summary>Protected bridge used by native build scripts after converted files are reset.</summary>
    public static class HybridBuildBridge
    {
        const string PipelineTypeName = "DYSJ.Editor.HybridCLR.HybridCLRBuildPipeline";
        const string BusinessBuilderTypeName = "Native.Editor.BuildYooAssetsAndroid";

        public static void PrepareHostBuild(BuildTarget target)
        {
            if (target != BuildTarget.Android)
                return;
            HybridRuntimeCompatibility.EnsureInstalledRuntime();
            Invoke("GenerateAllAndPrepareHostBuild");
            HybridAndroidSdk.StampEmbeddedBusinessVersion(HybridSettings.instance);
            HybridAndroidSdk.SyncBootstrapResources(HybridSettings.instance, true);
        }

        public static void PrepareAssetBuild(BuildTarget target)
        {
            if (target != BuildTarget.Android)
                return;
            HybridRuntimeCompatibility.EnsureInstalledRuntime();
            Invoke("PrepareBusinessHotUpdateForAssetBuild");
        }

        public static void ExportBusinessHotUpdate(BuildTarget target, string version, string outputDirectory)
        {
            if (target != BuildTarget.Android)
                return;
            Invoke("ExportBusinessHotUpdate", version, outputDirectory);
        }

        public static bool HasBusinessHotUpdateBuilder()
        {
            var type = FindType(BusinessBuilderTypeName);
            return type?.GetMethod(
                "BuildGameHotUpdate",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) != null;
        }

        public static bool BuildGameHotUpdate(string version, string outputDirectory)
        {
            var type = FindType(BusinessBuilderTypeName)
                ?? throw new BuildFailedException("业务热更新构建适配器尚未部署，请先执行一键部署热更新功能。");
            var method = type.GetMethod(
                "BuildGameHotUpdate",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new BuildFailedException("业务热更新构建入口不存在: " + BusinessBuilderTypeName);
            try
            {
                return method.Invoke(null, new object[] { version, outputDirectory }) is true;
            }
            catch (TargetInvocationException e)
            {
                throw e.InnerException ?? e;
            }
        }

        static void Invoke(string methodName, params object[] arguments)
        {
            var type = FindType(PipelineTypeName);
            if (type == null)
                throw new BuildFailedException("Hybrid 尚未部署。请先执行 Tools/Hybrid -> 一键部署热更新功能。");

            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            if (method == null)
                throw new BuildFailedException($"Hybrid 构建入口不存在: {PipelineTypeName}.{methodName}");

            try
            {
                method.Invoke(null, arguments);
            }
            catch (TargetInvocationException e)
            {
                throw e.InnerException ?? e;
            }
        }

        static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                    return type;
            }
            return null;
        }
    }

    [InitializeOnLoad]
    internal static class HybridBuildPlayerHandler
    {
        const string BootstrapScenePath = "Assets/_Native/Runtime/Hybrid/HybridBootstrap.unity";

        static HybridBuildPlayerHandler()
        {
            BuildPlayerWindow.RegisterBuildPlayerHandler(BuildPlayer);
        }

        static void BuildPlayer(BuildPlayerOptions options)
        {
            if (options.target == BuildTarget.Android)
            {
                Debug.Log("[Hybrid] Unity Build 已接管：正在准备默认业务程序集与 HybridCLR 主包资源。");
                HybridBuildBridge.PrepareHostBuild(options.target);
                options.scenes = new[] { BootstrapScenePath };
                options.options |= BuildOptions.CleanBuildCache;
                Debug.Log("[Hybrid] APK 仅包含启动场景；Main 将在业务 DLL 加载后从内置场景资源包打开。");
            }

            BuildPlayerWindow.DefaultBuildMethods.BuildPlayer(options);
        }
    }
}
