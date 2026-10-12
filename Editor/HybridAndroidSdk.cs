using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;
#if UNITY_ANDROID
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
#endif

namespace Native.Editor.Hybrid
{
    internal static class HybridAndroidSdk
    {
        const int MinimumCompileSdkVersion = 31;

        public static void StampEmbeddedBusinessVersion(HybridSettings settings)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var resourceRoot = settings.resourceRoot.Replace('/', Path.DirectorySeparatorChar);
            var manifestPath = Path.Combine(projectRoot, resourceRoot, "business_manifest.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("缺少 APK 内置业务清单", manifestPath);

            var manifest = JsonUtility.FromJson<BusinessManifest>(File.ReadAllText(manifestPath, Encoding.UTF8))
                ?? throw new InvalidDataException("APK 内置业务清单格式无效: " + manifestPath);
            var version = PlayerSettings.bundleVersion;
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException("APK 内置业务版本号不能为空");
            if (string.Equals(manifest.version, version, StringComparison.Ordinal))
                return;

            manifest.version = version;
            File.WriteAllText(
                manifestPath,
                JsonUtility.ToJson(manifest, true) + Environment.NewLine,
                new UTF8Encoding(false));
            Debug.Log($"[Hybrid] APK 内置默认业务版本已设置为: {version}");
        }

        public static void RemoveBusinessAssemblyFromLinkXml(HybridSettings settings)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var linkXmlPath = Path.Combine(projectRoot, "Assets", "link.xml");
            if (!File.Exists(linkXmlPath))
                return;

            var document = XDocument.Load(linkXmlPath, LoadOptions.PreserveWhitespace);
            var conflictingNodes = document
                .Descendants("assembly")
                .Where(node => string.Equals(
                    (string)node.Attribute("fullname"),
                    settings.businessAssemblyName,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (conflictingNodes.Length == 0)
                return;

            foreach (var node in conflictingNodes)
                node.Remove();
            using (var writer = new StreamWriter(linkXmlPath, false, new UTF8Encoding(false)))
                document.Save(writer, SaveOptions.DisableFormatting);

            Debug.LogWarning($"[Hybrid] 已从 Assets/link.xml 移除 {settings.businessAssemblyName}；业务热更新程序集不能作为 AOT 根程序集保留。");
        }

        public static void RemoveStaleBusinessAotCache(HybridSettings settings)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var libraryRoot = Path.GetFullPath(Path.Combine(projectRoot, "Library"));
            var cppRoot = Path.Combine(libraryRoot, "Bee", "artifacts", "Android", "il2cppOutput", "cpp");
            if (!ContainsBusinessAotCode(cppRoot, settings.businessAssemblyName))
                return;

            var staleRoots = new[]
            {
                Path.Combine(libraryRoot, "Bee", "artifacts", "Android"),
                Path.Combine(libraryRoot, "Bee", "Android"),
                Path.Combine(libraryRoot, "Il2cppBuildCache"),
            };

            foreach (var staleRoot in staleRoots)
            {
                var fullPath = Path.GetFullPath(staleRoot);
                if (!IsPathInside(fullPath, libraryRoot))
                    throw new InvalidOperationException("拒绝清理 Library 以外的目录: " + fullPath);
                if (Directory.Exists(fullPath))
                    Directory.Delete(fullPath, true);
            }

            Debug.LogWarning($"[Hybrid] 已清理旧 IL2CPP 中间产物；{settings.businessAssemblyName} 将仅由远端 HybridCLR DLL 提供。");
        }

        public static void ValidateBusinessAssemblyIsInterpreterOnly(HybridSettings settings)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var managedAssembly = Path.Combine(
                projectRoot,
                "Library",
                "Bee",
                "artifacts",
                "Android",
                "ManagedStripped",
                settings.businessAssemblyName + ".dll");
            var cppRoot = Path.Combine(projectRoot, "Library", "Bee", "artifacts", "Android", "il2cppOutput", "cpp");
            if (File.Exists(managedAssembly) || ContainsBusinessAotCode(cppRoot, settings.businessAssemblyName))
                throw new BuildFailedException($"[Hybrid] 主包仍包含 {settings.businessAssemblyName} 的 IL2CPP 代码，业务热更新未真正生效。请执行完整清理后重新打包。");

            Debug.Log($"[Hybrid] 主包校验通过: {settings.businessAssemblyName} 未编入 IL2CPP，将由远端 DLL 提供。");
        }

        static bool ContainsBusinessAotCode(string cppRoot, string assemblyName)
        {
            if (!Directory.Exists(cppRoot) || string.IsNullOrWhiteSpace(assemblyName))
                return false;

            var splitFilePattern = new Regex(
                "^" + Regex.Escape(assemblyName) + @"(?:\d+)?\.cpp$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return Directory.EnumerateFiles(cppRoot, "*.cpp", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Any(fileName => splitFilePattern.IsMatch(fileName));
        }

        static bool IsPathInside(string path, string parent)
        {
            var parentWithSeparator = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                      + Path.DirectorySeparatorChar;
            return path.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        [Serializable]
        sealed class BusinessManifest
        {
            public string assemblyName;
            public string version;
            public string fileName;
            public string sha256;
            public long length;
            public string buildTarget;
            public string generatedUtc;
        }

        public static bool Validate(HybridSettings settings, out string problem)
        {
            var sdkRoot = settings.GetAndroidSdkRoot();
            if (string.IsNullOrWhiteSpace(sdkRoot) || !Directory.Exists(sdkRoot))
            {
                problem = "Android SDK 目录不存在: " + sdkRoot;
                return false;
            }

            if (settings.androidCompileSdkVersion < MinimumCompileSdkVersion)
            {
                problem = $"当前 Android 依赖要求 Compile SDK 不低于 {MinimumCompileSdkVersion}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(settings.androidBuildToolsVersion))
            {
                problem = "Android Build Tools 不能为空";
                return false;
            }

            var platform = "android-" + settings.androidCompileSdkVersion;
            var platformJar = Path.Combine(sdkRoot, "platforms", platform, "android.jar");
            if (!File.Exists(platformJar))
            {
                problem = $"Android SDK 缺少 platforms;{platform}: {sdkRoot}";
                return false;
            }

            var aapt = Path.Combine(sdkRoot, "build-tools", settings.androidBuildToolsVersion.Trim(), "aapt.exe");
            if (!File.Exists(aapt))
            {
                problem = $"Android SDK 缺少 build-tools;{settings.androidBuildToolsVersion.Trim()}: {sdkRoot}";
                return false;
            }

            var license = Path.Combine(sdkRoot, "licenses", "android-sdk-license");
            if (!File.Exists(license) || new FileInfo(license).Length == 0)
            {
                problem = "Android SDK License 未安装: " + license;
                return false;
            }

            problem = null;
            return true;
        }

        public static void PatchGeneratedProject(string generatedPath)
        {
            var settings = HybridSettings.instance;
            var sdkRoot = settings.GetAndroidSdkRoot();
            if (!Validate(settings, out var problem))
                throw new InvalidOperationException(problem);

            var gradleRoot = FindGradleRoot(generatedPath);
            var localPropertiesPath = Path.Combine(gradleRoot, "local.properties");
            var lines = File.Exists(localPropertiesPath)
                ? File.ReadAllLines(localPropertiesPath).ToList()
                : new List<string>();
            lines.RemoveAll(line => line.TrimStart().StartsWith("sdk.dir=", StringComparison.Ordinal));
            lines.Insert(0, "sdk.dir=" + EscapeLocalPropertiesPath(sdkRoot));
            File.WriteAllLines(localPropertiesPath, lines, new UTF8Encoding(false));

            var patchedFiles = 0;
            foreach (var gradleFile in Directory.GetFiles(gradleRoot, "build.gradle", SearchOption.AllDirectories))
            {
                if (PatchGradleFile(gradleFile, settings))
                    patchedFiles++;
            }

            Debug.Log($"[Hybrid] Android Gradle 已配置: SDK={sdkRoot}, compileSdk={settings.androidCompileSdkVersion}, buildTools={settings.androidBuildToolsVersion}，修改 {patchedFiles} 个模块。");
        }

        public static void ConfigureUnityEditor(HybridSettings settings)
        {
            var sdkRoot = settings.GetAndroidSdkRoot();
            if (!Validate(settings, out var problem))
                throw new InvalidOperationException(problem);
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var templatePaths = new[]
            {
                Path.Combine(projectRoot, "Assets", "Plugins", "Android", "mainTemplate.gradle"),
                Path.Combine(projectRoot, "Assets", "Plugins", "Android", "launcherTemplate.gradle"),
            };
            var patchedTemplates = templatePaths.Count(PatchUnityGradleTemplate);
            Debug.Log($"[Hybrid] Unity Android 模板已恢复占位符，修改 {patchedTemplates} 个模板；生成 Gradle 工程后将使用 compileSdk={settings.androidCompileSdkVersion}, buildTools={settings.androidBuildToolsVersion} 和外接 SDK。");
        }

        public static void SyncBootstrapResources(HybridSettings settings, bool required)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var sourceRoot = Path.Combine(projectRoot, settings.resourceRoot.Replace('/', Path.DirectorySeparatorChar));
            var prefix = (settings.runtimeResourcePrefix ?? string.Empty).Trim().Trim('/', '\\');
            if (string.IsNullOrEmpty(prefix) || prefix.Contains(".."))
                throw new InvalidOperationException("Runtime Prefix 无效: " + settings.runtimeResourcePrefix);

            var sourceFiles = Directory.Exists(sourceRoot)
                ? Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
                    .Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                                   && !string.Equals(Path.GetFileName(path), ".gitignore", StringComparison.OrdinalIgnoreCase))
                    .ToArray()
                : Array.Empty<string>();
            if (sourceFiles.Length == 0)
            {
                if (required)
                    throw new InvalidOperationException("缺少可内置的 HybridCLR 基线资源: " + sourceRoot);
                Debug.LogWarning("[Hybrid] 尚未生成 HybridCLR 基线资源，完成 Generate All 后会自动内置。");
                return;
            }

            var destinationRoot = Path.Combine(projectRoot, "Assets", "Resources", prefix.Replace('/', Path.DirectorySeparatorChar));
            var changed = false;
            foreach (var source in sourceFiles)
            {
                var relative = source.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var destination = Path.Combine(destinationRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destinationRoot);
                if (File.Exists(destination) && File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(destination)))
                    continue;
                File.Copy(source, destination, true);
                changed = true;
            }

            if (changed)
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"[Hybrid] APK 基线热更新资源已同步: {sourceFiles.Length} 个文件 -> Assets/Resources/{prefix}");
        }

        static bool PatchUnityGradleTemplate(string path)
        {
            if (!File.Exists(path))
                return false;

            var content = File.ReadAllText(path, Encoding.UTF8);
            var patched = Regex.Replace(content,
                @"(?m)^(\s*)compileSdkVersion\s+[^\r\n]+$",
                "$1compileSdkVersion **APIVERSION**");
            patched = Regex.Replace(patched,
                "(?m)^(\\s*)buildToolsVersion\\s+['\"][^'\"]+['\"]\\s*$",
                "$1buildToolsVersion '**BUILDTOOLS**'");
            if (patched == content)
                return false;

            File.WriteAllText(path, patched, new UTF8Encoding(false));
            return true;
        }

        static bool PatchGradleFile(string path, HybridSettings settings)
        {
            if (!File.Exists(path))
                return false;

            var content = File.ReadAllText(path, Encoding.UTF8);
            var patched = Regex.Replace(content,
                @"(?m)^(\s*)compileSdkVersion\s+[^\r\n]+$",
                "$1compileSdkVersion " + settings.androidCompileSdkVersion);
            patched = Regex.Replace(patched,
                "(?m)^(\\s*)buildToolsVersion\\s+['\"][^'\"]+['\"]\\s*$",
                "$1buildToolsVersion '" + settings.androidBuildToolsVersion.Trim() + "'");
            if (patched == content)
                return false;

            File.WriteAllText(path, patched, new UTF8Encoding(false));
            return true;
        }

        static string FindGradleRoot(string generatedPath)
        {
            if (string.IsNullOrWhiteSpace(generatedPath))
                throw new ArgumentException("Android Gradle 工程路径为空", nameof(generatedPath));

            var fullPath = Path.GetFullPath(generatedPath);
            var looksLikeOutputFile = string.Equals(Path.GetExtension(fullPath), ".apk", StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(Path.GetExtension(fullPath), ".aab", StringComparison.OrdinalIgnoreCase);
            var current = File.Exists(fullPath) || looksLikeOutputFile
                ? new DirectoryInfo(Path.GetDirectoryName(fullPath) ?? fullPath)
                : new DirectoryInfo(fullPath);
            for (var i = 0; current != null && i < 5; i++, current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "settings.gradle")))
                    return current.FullName;
            }

            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var knownRoots = new[]
            {
                Path.Combine(projectRoot, "Library", "Bee", "Android", "Prj", "IL2CPP", "Gradle"),
                Path.Combine(projectRoot, "Temp", "gradleOut"),
            };
            foreach (var root in knownRoots)
            {
                if (File.Exists(Path.Combine(root, "settings.gradle")))
                    return root;
            }

            throw new DirectoryNotFoundException("无法定位 Android Gradle 工程根目录: " + generatedPath);
        }

        static string EscapeLocalPropertiesPath(string path)
        {
            return path.Replace("\\", "\\\\").Replace(":", "\\:");
        }
    }

#if UNITY_ANDROID
    internal sealed class HybridAndroidBuildPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -10000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android)
                return;

            var settings = HybridSettings.instance;
            HybridAndroidSdk.RemoveBusinessAssemblyFromLinkXml(settings);
            HybridAndroidSdk.RemoveStaleBusinessAotCache(settings);
            if (!HybridAndroidSdk.Validate(settings, out var problem))
                throw new BuildFailedException("[Hybrid] " + problem);
            HybridAndroidSdk.ConfigureUnityEditor(settings);
            HybridAndroidSdk.StampEmbeddedBusinessVersion(settings);
            HybridAndroidSdk.SyncBootstrapResources(settings, true);
        }
    }

    internal sealed class HybridAndroidSdkPostprocessor : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 10000;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            HybridAndroidSdk.ValidateBusinessAssemblyIsInterpreterOnly(HybridSettings.instance);
            HybridAndroidSdk.PatchGeneratedProject(path);
        }
    }

    internal sealed class HybridAndroidBusinessAotVerifier : IPostprocessBuildWithReport
    {
        public int callbackOrder => 20000;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android || report.summary.result != BuildResult.Succeeded)
                return;
            HybridAndroidSdk.ValidateBusinessAssemblyIsInterpreterOnly(HybridSettings.instance);
        }
    }
#endif
}
