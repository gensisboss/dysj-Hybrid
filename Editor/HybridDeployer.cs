using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Native.Editor.Hybrid
{
    [InitializeOnLoad]
    internal static class HybridDeployer
    {
        const string PendingKeyPrefix = "DYSJ.Hybrid.PendingDeployment|";
        const string PackageName = "com.dysj.hybrid";
        const string TemplateRelativeRoot = "Editor/Templates";
        const string HybridClrVersion = "8.11.0";
        const string CompatibilityHookTemplate = "HybridCLRGetIl2CppFolderHook.cs.txt";
        const string CompatibilityHookRelativePath = "Editor/3rds/UnityHook/HybridCLRHooks/GetIl2CppFolderHook.cs";
        static AddRequest _addRequest;
        static ListRequest _resolveRequest;

        static string PendingKey => PendingKeyPrefix + GetProjectRoot();

        public static string LastStatus { get; private set; } = "尚未检查。";
        public static bool LastStatusIsError { get; private set; }

        static HybridDeployer()
        {
            EditorApplication.delayCall += ResumePendingDeployment;
        }

        public static void StartDeployment()
        {
            try
            {
                if (!CanStartDeployment(out var problem))
                    throw new InvalidOperationException(problem.Replace("\n- ", "\n").TrimStart('-', ' '));
                var settings = HybridSettings.instance;
                settings.Persist();
                ValidateSettings(settings);
                EditorPrefs.SetBool(PendingKey, true);

                if (IsPackageInstalled(settings.hybridClrPackageName))
                {
                    CompleteDeployment();
                    return;
                }

                SetStatus("正在安装 HybridCLR Package，安装完成后会自动继续部署。", false);
                _addRequest = Client.Add(settings.hybridClrPackageSpec);
                EditorApplication.update -= PollPackageRequest;
                EditorApplication.update += PollPackageRequest;
            }
            catch (Exception e)
            {
                Fail("启动部署失败", e);
            }
        }

        static void PollPackageRequest()
        {
            if (_addRequest == null || !_addRequest.IsCompleted)
                return;

            EditorApplication.update -= PollPackageRequest;
            if (_addRequest.Status == StatusCode.Success)
            {
                SetStatus("HybridCLR Package 已安装，等待 Unity 重新编译后继续部署。", false);
                AssetDatabase.Refresh();
                EditorApplication.delayCall += ResumePendingDeployment;
            }
            else
            {
                EditorPrefs.DeleteKey(PendingKey);
                SetStatus("HybridCLR Package 安装失败: " + _addRequest.Error?.message, true);
            }
            _addRequest = null;
        }

        static void ResumePendingDeployment()
        {
            if (!EditorPrefs.GetBool(PendingKey, false))
                return;
            if (!IsPackageInstalled(HybridSettings.instance.hybridClrPackageName))
            {
                SetStatus("正在等待 HybridCLR Package 安装完成。", false);
                return;
            }
            CompleteDeployment();
        }

        static void CompleteDeployment()
        {
            try
            {
                var settings = HybridSettings.instance;
                ValidateSettings(settings);
                if (!EnsureHybridClrCompatibility(settings))
                    return;

                if (settings.autoSwitchBuildTarget && EditorUserBuildSettings.activeBuildTarget != settings.buildTarget)
                {
                    var group = BuildPipeline.GetBuildTargetGroup(settings.buildTarget);
                    if (!EditorUserBuildSettings.SwitchActiveBuildTarget(group, settings.buildTarget))
                        throw new InvalidOperationException("无法切换 Build Target: " + settings.buildTarget);
                }

                HybridAndroidSdk.ConfigureUnityEditor(settings);
                HybridAndroidSdk.RemoveBusinessAssemblyFromLinkXml(settings);
                HybridAndroidSdk.EnsureHybridClrRuntimeLinkXml();
                if (settings.enforceIl2Cpp)
                {
                    var group = BuildPipeline.GetBuildTargetGroup(settings.buildTarget);
                    PlayerSettings.SetScriptingBackend(group, ScriptingImplementation.IL2CPP);
                }

                GenerateFiles(settings);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                ConfigureHybridClrSettings(settings);
                WriteBusinessBootstrapConfig(settings);
                EnsureBootstrapScenes(settings);
                EnsureYooAssetCollector(settings);
                HybridAndroidSdk.SyncBootstrapResources(settings, false);

                if (settings.installHybridClrRuntime)
                    EnsureHybridClrRuntimeInstalled();
                if (HybridRuntimeCompatibility.IsRuntimeInstalled)
                    HybridRuntimeCompatibility.EnsureInstalledRuntime();

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                EditorPrefs.DeleteKey(PendingKey);
                SetStatus("Hybrid 部署完成。热更新代码、启动接入、YooAsset 收集规则和 Android 构建链均已恢复。", false);
                Debug.Log("[Hybrid] 一键部署完成");
            }
            catch (Exception e)
            {
                EditorPrefs.DeleteKey(PendingKey);
                Fail("Hybrid 部署失败", e);
            }
        }

        static void GenerateFiles(HybridSettings settings)
        {
            var replacements = new Dictionary<string, string>
            {
                ["{{BUSINESS_ASSEMBLY_NAME}}"] = settings.businessAssemblyName,
                ["{{TARGET}}"] = settings.buildTarget.ToString(),
                ["{{RESOURCE_ROOT}}"] = NormalizeAssetPath(settings.resourceRoot),
                ["{{BUSINESS_SCENE_PATH}}"] = NormalizeAssetPath(settings.mainScenePath),
            };

            WriteTemplate("HybridCLREditorTools.asmdef.txt", CombineAssetPath(settings.buildPipelineRoot, "HybridCLREditorTools.asmdef"), replacements);
            WriteTemplate("HybridCLRBuildPipeline.cs.txt", CombineAssetPath(settings.buildPipelineRoot, "HybridCLRBuildPipeline.cs"), replacements);
            WriteTemplate("BuildYooAssetsAndroid.cs.txt", settings.buildAdapterPath, replacements);
            WriteTemplate("HybridLauncher.unity.txt", settings.launcherScenePath, replacements);
            WriteTemplate("HybridBootstrap.unity.txt", settings.bootstrapScenePath, replacements);

            EnsureAssetDirectory(settings.resourceRoot);
        }

        static void WriteTemplate(string templateName, string destination, Dictionary<string, string> replacements)
        {
            var templatePath = GetPackageFilePath(CombineAssetPath(TemplateRelativeRoot, templateName));
            if (!File.Exists(templatePath))
                throw new FileNotFoundException("Hybrid 模板不存在", templatePath);

            var content = File.ReadAllText(templatePath, Encoding.UTF8);
            foreach (var replacement in replacements)
                content = content.Replace(replacement.Key, replacement.Value);

            var fullDestination = ToFullPath(destination);
            var directory = Path.GetDirectoryName(fullDestination);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("无效部署路径: " + destination);
            Directory.CreateDirectory(directory);

            WriteIfChanged(fullDestination, content);
        }

        static void ConfigureHybridClrSettings(HybridSettings settings)
        {
            var type = FindType("HybridCLR.Editor.Settings.HybridCLRSettings")
                ?? throw new InvalidOperationException("HybridCLR Settings 类型尚未加载，请等待 Package 编译完成后重试。");
            var instanceProperty = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var instance = instanceProperty?.GetValue(null) as UnityEngine.Object
                ?? throw new InvalidOperationException("无法取得 HybridCLRSettings.Instance");

            var serialized = new SerializedObject(instance);
            SetBool(serialized, "enable", true);
            SetBool(serialized, "useGlobalIl2cpp", false);
            SetString(serialized, "hotUpdateDllCompileOutputRootDir", "HybridCLRData/HotUpdateDlls");
            SetString(serialized, "strippedAOTDllOutputRootDir", "HybridCLRData/AssembliesPostIl2CppStrip");
            SetString(serialized, "outputLinkFile", "HybridCLRGenerate/link.xml");
            SetString(serialized, "outputAOTGenericReferenceFile", "HybridCLRGenerate/AOTGenericReferences.cs");

            var hotUpdateDefinitions = RequireProperty(serialized, "hotUpdateAssemblyDefinitions");
            hotUpdateDefinitions.arraySize = 0;

            var hotUpdateAssemblies = RequireProperty(serialized, "hotUpdateAssemblies");
            hotUpdateAssemblies.arraySize = 1;
            hotUpdateAssemblies.GetArrayElementAtIndex(0).stringValue = settings.businessAssemblyName;

            var patchAot = RequireProperty(serialized, "patchAOTAssemblies");
            var existingAotNames = Enumerable.Range(0, patchAot.arraySize)
                .Select(index => patchAot.GetArrayElementAtIndex(index).stringValue);
            var aotNames = ParseLines(settings.patchAotAssemblies)
                .Concat(existingAotNames)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            patchAot.arraySize = aotNames.Length;
            for (var i = 0; i < aotNames.Length; i++)
                patchAot.GetArrayElementAtIndex(i).stringValue = aotNames[i];

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(instance);
            type.GetMethod("Save", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        }

        static void WriteBusinessBootstrapConfig(HybridSettings settings)
        {
            var prefix = (settings.runtimeResourcePrefix ?? string.Empty).Trim().Trim('/', '\\');
            if (string.IsNullOrEmpty(prefix) || prefix.Contains("..", StringComparison.Ordinal))
                throw new InvalidOperationException("Runtime Prefix 无效: " + settings.runtimeResourcePrefix);
            var assetPath = $"Assets/Resources/{prefix}/business_bootstrap_config.json";
            EnsureAssetDirectory(Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? "Assets/Resources");
            settings.bootPackageRemoteRoot = ResolveBootPackageRemoteRoot(settings);
            settings.Persist();
            var config = new BusinessBootstrapConfig
            {
                remoteRoot = (settings.businessRemoteRoot ?? string.Empty).Trim().TrimEnd('/'),
                latestManifest = "business_latest.json",
                mainSceneName = Path.GetFileNameWithoutExtension(settings.mainScenePath),
                cacheFolder = "HybridCLRBusiness",
                timeoutSeconds = Math.Max(3, Math.Min(60, settings.businessRemoteTimeoutSeconds)),
                bootPackageName = settings.bootPackageName,
                bootPackageRemoteRoot = (settings.bootPackageRemoteRoot ?? string.Empty).Trim().TrimEnd('/'),
                bootSceneLocation = Path.GetFileNameWithoutExtension(settings.bootstrapScenePath),
                embeddedBootPackageVersion = string.Empty,
            };
            WriteIfChanged(ToFullPath(assetPath), JsonUtility.ToJson(config, true) + Environment.NewLine);
        }

        static string ResolveBootPackageRemoteRoot(HybridSettings settings)
        {
            var configured = (settings.bootPackageRemoteRoot ?? string.Empty).Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(configured))
                return configured;

            var gameConfig = FindType("GameConfig");
            var bundleCdn = gameConfig?
                .GetField("BundleCDN", BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null)?
                .ToString()?
                .Trim()
                .TrimEnd('/');
            if (string.IsNullOrEmpty(bundleCdn))
                throw new InvalidOperationException("无法从 GameConfig.BundleCDN 推导 BootPackage CDN 地址");
            return bundleCdn + "/YooPackage/" + settings.bootPackageName;
        }

        internal static void StampEmbeddedBootPackageVersion(string version)
        {
            if (!IsSafeVersion(version))
                throw new InvalidOperationException("BootPackage 内置版本号非法: " + version);
            var settings = HybridSettings.instance;
            var prefix = (settings.runtimeResourcePrefix ?? string.Empty).Trim().Trim('/', '\\');
            var assetPath = $"Assets/Resources/{prefix}/business_bootstrap_config.json";
            var fullPath = ToFullPath(assetPath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("缺少启动配置，请先执行一键部署", assetPath);
            var config = JsonUtility.FromJson<BusinessBootstrapConfig>(File.ReadAllText(fullPath, Encoding.UTF8))
                ?? throw new InvalidDataException("启动配置格式无效: " + assetPath);
            config.embeddedBootPackageVersion = version;
            WriteIfChanged(fullPath, JsonUtility.ToJson(config, true) + Environment.NewLine);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        static void EnsureBootstrapScenes(HybridSettings settings)
        {
            var launcher = NormalizeAssetPath(settings.launcherScenePath);
            var bootstrap = NormalizeAssetPath(settings.bootstrapScenePath);
            var main = NormalizeAssetPath(settings.mainScenePath);
            if (!File.Exists(ToFullPath(launcher)))
                throw new FileNotFoundException("内置启动入口场景不存在", launcher);
            if (!File.Exists(ToFullPath(bootstrap)))
                throw new FileNotFoundException("热更新启动界面场景不存在", bootstrap);
            if (!File.Exists(ToFullPath(main)))
                throw new FileNotFoundException("游戏主场景不存在", main);

            var scenes = new List<EditorBuildSettingsScene>
            {
                new EditorBuildSettingsScene(launcher, true),
                new EditorBuildSettingsScene(bootstrap, true),
                new EditorBuildSettingsScene(main, true),
            };
            scenes.AddRange(EditorBuildSettings.scenes.Where(scene =>
                !string.Equals(NormalizeAssetPath(scene.path), launcher, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(NormalizeAssetPath(scene.path), bootstrap, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(NormalizeAssetPath(scene.path), main, StringComparison.OrdinalIgnoreCase)));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        static void EnsureHybridClrRuntimeInstalled()
        {
            var type = FindType("HybridCLR.Editor.Installer.InstallerController")
                ?? throw new InvalidOperationException("找不到 HybridCLR InstallerController");
            var controller = Activator.CreateInstance(type);
            var hasInstalled = (bool)type.GetMethod("HasInstalledHybridCLR")!.Invoke(controller, null);
            var packageVersion = type.GetProperty("PackageVersion")?.GetValue(controller)?.ToString();
            var installedVersion = type.GetProperty("InstalledLibil2cppVersion")?.GetValue(controller)?.ToString();
            if (hasInstalled && string.Equals(packageVersion, installedVersion, StringComparison.Ordinal))
                return;

            SetStatus("正在安装 HybridCLR Runtime。", false);
            type.GetMethod("InstallDefaultHybridCLR")?.Invoke(controller, null);
        }

        static void EnsureYooAssetCollector(HybridSettings settings)
        {
            var collector = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(settings.collectorSettingsPath)
                ?? throw new InvalidOperationException("找不到 YooAsset Collector Settings: " + settings.collectorSettingsPath);
            var serialized = new SerializedObject(collector);
            var packages = RequireProperty(serialized, "Packages");
            SerializedProperty gamePackage = null;
            for (var i = 0; i < packages.arraySize; i++)
            {
                var item = packages.GetArrayElementAtIndex(i);
                if (item.FindPropertyRelative("PackageName")?.stringValue == "Game")
                {
                    gamePackage = item;
                    break;
                }
            }
            if (gamePackage == null)
                throw new InvalidOperationException("YooAsset Collector Settings 中缺少 Game Package");

            var groups = gamePackage.FindPropertyRelative("Groups")
                ?? throw new InvalidOperationException("Game Package 缺少 Groups 字段");
            SerializedProperty group = null;
            for (var i = 0; i < groups.arraySize; i++)
            {
                var item = groups.GetArrayElementAtIndex(i);
                if (item.FindPropertyRelative("GroupName")?.stringValue == "hybridclr")
                {
                    group = item;
                    break;
                }
            }
            if (group == null)
            {
                groups.InsertArrayElementAtIndex(groups.arraySize);
                group = groups.GetArrayElementAtIndex(groups.arraySize - 1);
            }

            SetRelativeString(group, "GroupName", "hybridclr");
            SetRelativeString(group, "GroupDesc", "HybridCLR runtime artifacts");
            SetRelativeString(group, "AssetTags", "hybridclr");
            SetRelativeString(group, "ActiveRuleName", "EnableGroup");

            var collectors = group.FindPropertyRelative("Collectors")
                ?? throw new InvalidOperationException("HybridCLR Group 缺少 Collectors 字段");
            collectors.arraySize = 1;
            var itemCollector = collectors.GetArrayElementAtIndex(0);
            SetRelativeString(itemCollector, "CollectPath", NormalizeAssetPath(settings.resourceRoot));
            SetRelativeString(itemCollector, "CollectorGUID", AssetDatabase.AssetPathToGUID(NormalizeAssetPath(settings.resourceRoot)));
            SetRelativeInt(itemCollector, "CollectorType", 0);
            SetRelativeString(itemCollector, "AddressRuleName", "AddressByFileName");
            SetRelativeString(itemCollector, "PackRuleName", "PackDirectory");
            SetRelativeString(itemCollector, "FilterRuleName", "CollectAll");
            SetRelativeString(itemCollector, "AssetTags", "hybridclr");
            SetRelativeString(itemCollector, "UserData", string.Empty);

            EnsureBootPackageCollector(packages, settings);

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(collector);
        }

        static void EnsureBootPackageCollector(SerializedProperty packages, HybridSettings settings)
        {
            SerializedProperty bootPackage = null;
            for (var i = 0; i < packages.arraySize; i++)
            {
                var item = packages.GetArrayElementAtIndex(i);
                if (item.FindPropertyRelative("PackageName")?.stringValue == settings.bootPackageName)
                {
                    bootPackage = item;
                    break;
                }
            }
            if (bootPackage == null)
            {
                packages.InsertArrayElementAtIndex(packages.arraySize);
                bootPackage = packages.GetArrayElementAtIndex(packages.arraySize - 1);
            }

            SetRelativeString(bootPackage, "PackageName", settings.bootPackageName);
            SetRelativeString(bootPackage, "PackageDesc", "Hot-update bootstrap scene");
            SetRelativeBool(bootPackage, "EnableAddressable", false);
            SetRelativeBool(bootPackage, "SupportExtensionless", true);
            SetRelativeBool(bootPackage, "LocationToLower", true);
            SetRelativeBool(bootPackage, "IncludeAssetGUID", false);
            SetRelativeBool(bootPackage, "AutoCollectShaders", true);
            SetRelativeString(bootPackage, "IgnoreRuleName", "NormalIgnoreRule");

            var groups = bootPackage.FindPropertyRelative("Groups")
                ?? throw new InvalidOperationException("BootPackage 缺少 Groups 字段");
            groups.arraySize = 1;
            var group = groups.GetArrayElementAtIndex(0);
            SetRelativeString(group, "GroupName", "bootstrap");
            SetRelativeString(group, "GroupDesc", "Hybrid bootstrap scene and visual dependencies");
            SetRelativeString(group, "AssetTags", "bootstrap");
            SetRelativeString(group, "ActiveRuleName", "EnableGroup");

            var collectors = group.FindPropertyRelative("Collectors")
                ?? throw new InvalidOperationException("BootPackage bootstrap Group 缺少 Collectors 字段");
            collectors.arraySize = 1;
            var sceneCollector = collectors.GetArrayElementAtIndex(0);
            var scenePath = NormalizeAssetPath(settings.bootstrapScenePath);
            SetRelativeString(sceneCollector, "CollectPath", scenePath);
            SetRelativeString(sceneCollector, "CollectorGUID", AssetDatabase.AssetPathToGUID(scenePath));
            SetRelativeInt(sceneCollector, "CollectorType", 0);
            SetRelativeString(sceneCollector, "AddressRuleName", "AddressByFileName");
            SetRelativeString(sceneCollector, "PackRuleName", "PackSeparately");
            SetRelativeString(sceneCollector, "FilterRuleName", "CollectScene");
            SetRelativeString(sceneCollector, "AssetTags", "bootstrap");
            SetRelativeString(sceneCollector, "UserData", string.Empty);
        }

        public static bool ValidateProject(bool showDialog)
        {
            var settings = HybridSettings.instance;
            var problems = CollectDeploymentProblemsSafely(settings);

            var ok = problems.Count == 0;
            var successMessage = RequiresCompatibilityPatch()
                ? "Hybrid 部署状态正常。Unity 2022.1 兼容补丁已启用。"
                : "Hybrid 部署状态正常。";
            SetStatus(ok ? successMessage : string.Join("\n", problems.Select(p => "- " + p)), !ok);
            if (showDialog)
                EditorUtility.DisplayDialog("Hybrid", LastStatus, "确定");
            return ok;
        }

        public static bool CanStartDeployment(out string problem)
        {
            var problems = new List<string>();
            var settings = HybridSettings.instance;
            try
            {
                AddEditorBusyProblems(problems);
                if (!CanUseCurrentUnityVersion(out var unityVersionProblem))
                    problems.Add(unityVersionProblem);
                AddAndroidSdkProblem(settings, problems);
                CheckProjectFile(settings.mainScenePath, "游戏主场景不存在", problems);
                if (FindPackage("com.tuyoogame.yooasset") == null)
                    problems.Add("YooAsset Package 未安装");
                if (FindType("Res.PackageConfig") == null)
                    problems.Add("当前项目缺少 Res.PackageConfig");
                ValidateCollectorPrerequisite(settings, problems);
                foreach (var template in new[]
                         {
                             "HybridCLREditorTools.asmdef.txt",
                             "HybridCLRBuildPipeline.cs.txt",
                             "HybridCLRGetIl2CppFolderHook.cs.txt",
                             "BuildYooAssetsAndroid.cs.txt",
                             "HybridLauncher.unity.txt",
                             "HybridBootstrap.unity.txt",
                         })
                    CheckPackageFile(CombineAssetPath(TemplateRelativeRoot, template), problems);
            }
            catch (Exception e)
            {
                problems.Add("检查部署条件失败: " + e.GetBaseException().Message);
            }

            problem = string.Join("\n", problems.Select(item => "- " + item));
            return problems.Count == 0;
        }

        public static bool CanGenerateBusinessHotUpdate(out string problem)
        {
            var settings = HybridSettings.instance;
            var problems = CollectDeploymentProblemsSafely(settings);
            try
            {
                AddEditorBusyProblems(problems);
                CheckFile(settings.buildAdapterPath, problems);
                CheckFile(settings.resourceRoot + "/patch_aot_list.txt", problems);
                CheckFile("Assets/StreamingAssets/HybridCLR/hybrid_main_scene.bundle", problems);
                if (!IsSafeVersion(settings.businessPublishVersion))
                    problems.Add("CDN 发布版本不能为空，且只能包含字母、数字、点、下划线或短横线（最长 64 位）");
                _ = settings.GetBusinessOutputPath(settings.businessPublishVersion);
                if (!HybridBuildBridge.HasBusinessHotUpdateBuilder())
                    problems.Add("业务热更新构建入口尚未加载，请等待 Unity 编译完成");
            }
            catch (Exception e)
            {
                problems.Add("检查热更新包生成条件失败: " + e.GetBaseException().Message);
            }
            if (problems.Any(item => item.Contains("patch_aot_list.txt")
                                     || item.Contains("hybrid_main_scene.bundle")))
                problems.Add("尚未生成 APK 的 AOT 基线，请先执行一次 Unity Build 构建基准 APK");

            problem = string.Join("\n", problems.Distinct().Select(item => "- " + item));
            return problems.Count == 0;
        }

        public static bool CanGenerateBootPackageHotUpdate(out string problem)
        {
            var settings = HybridSettings.instance;
            var problems = CollectDeploymentProblemsSafely(settings);
            try
            {
                AddEditorBusyProblems(problems);
                CheckFile(settings.buildAdapterPath, problems);
                CheckFile(settings.bootstrapScenePath, problems);
                if (!IsSafeVersion(settings.businessPublishVersion))
                    problems.Add("CDN 发布版本不能为空，且只能包含字母、数字、点、下划线或短横线（最长 64 位）");
                if (!HybridBuildBridge.HasBootPackageBuilder())
                    problems.Add("BootPackage 构建入口尚未加载，请等待 Unity 编译完成");
            }
            catch (Exception e)
            {
                problems.Add("检查启动界面资源包生成条件失败: " + e.GetBaseException().Message);
            }

            problem = string.Join("\n", problems.Distinct().Select(item => "- " + item));
            return problems.Count == 0;
        }

        public static bool CanValidateProject(out string problem)
        {
            var problems = new List<string>();
            AddEditorBusyProblems(problems);
            problem = string.Join("\n", problems.Distinct().Select(item => "- " + item));
            return problems.Count == 0;
        }

        static List<string> CollectDeploymentProblems(HybridSettings settings)
        {
            var problems = new List<string>();
            if (!CanUseCurrentUnityVersion(out var unityVersionProblem)) problems.Add(unityVersionProblem);
            if (!IsPackageInstalled(settings.hybridClrPackageName)) problems.Add("HybridCLR Package 未安装");
            if (RequiresCompatibilityPatch() && !IsCompatibilityPatchCurrent(settings))
                problems.Add("Unity 2022.1 兼容补丁未部署");
            if (!HybridRuntimeCompatibility.IsRuntimeInstalled)
                problems.Add("HybridCLR Runtime 尚未安装");
            else if (!HybridRuntimeCompatibility.IsPatchCurrent(out var runtimeCompatibilityProblem))
                problems.Add(runtimeCompatibilityProblem);
            if (!HybridAndroidSdk.ValidateHybridClrRuntimeLinkXml(out var linkXmlProblem))
                problems.Add(linkXmlProblem);
            AddAndroidSdkProblem(settings, problems);
            CheckFile(settings.buildPipelineRoot + "/HybridCLRBuildPipeline.cs", problems);
            CheckFile(settings.launcherScenePath, problems);
            CheckFile(settings.bootstrapScenePath, problems);
            CheckPackageFile("Runtime/HybridBusinessBootstrap.cs", problems);
            CheckFile($"Assets/Resources/{(settings.runtimeResourcePrefix ?? string.Empty).Trim('/')}/business_bootstrap_config.json", problems);
            var enabledScenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).ToArray();
            if (enabledScenes.Length == 0 || !string.Equals(NormalizeAssetPath(enabledScenes[0].path), NormalizeAssetPath(settings.launcherScenePath), StringComparison.OrdinalIgnoreCase))
                problems.Add("Build Settings 的第一个场景不是 HybridLauncher");
            ValidateBootPackageCollector(settings, problems);
            ValidateBootSceneDependencies(settings, problems);
            if (PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android) != ScriptingImplementation.IL2CPP)
                problems.Add("Android Scripting Backend 不是 IL2CPP");
            return problems;
        }

        internal static void ValidateBootSceneForBuild()
        {
            var problems = new List<string>();
            ValidateBootSceneDependencies(HybridSettings.instance, problems);
            if (problems.Count > 0)
                throw new BuildFailedException(string.Join("\n", problems));
        }

        static void ValidateBootSceneDependencies(HybridSettings settings, ICollection<string> problems)
        {
            var scenePath = NormalizeAssetPath(settings.bootstrapScenePath);
            if (!File.Exists(ToFullPath(scenePath)))
                return;
            foreach (var dependency in AssetDatabase.GetDependencies(scenePath, true))
            {
                if (!dependency.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    continue;
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(dependency);
                var type = script == null ? null : script.GetClass();
                if (type != null && string.Equals(type.Assembly.GetName().Name, "Assembly-CSharp", StringComparison.Ordinal))
                    problems.Add("HybridBootstrap 引用了业务脚本，不能进入 BootPackage: " + dependency);
            }
        }

        static List<string> CollectDeploymentProblemsSafely(HybridSettings settings)
        {
            try
            {
                return CollectDeploymentProblems(settings);
            }
            catch (Exception e)
            {
                return new List<string> { "检查部署状态失败: " + e.GetBaseException().Message };
            }
        }

        static void AddAndroidSdkProblem(HybridSettings settings, ICollection<string> problems)
        {
            try
            {
                if (!HybridAndroidSdk.Validate(settings, out var androidSdkProblem))
                    problems.Add(androidSdkProblem);
            }
            catch (Exception e)
            {
                problems.Add("Android SDK 路径无效: " + e.GetBaseException().Message);
            }
        }

        static void ValidateCollectorPrerequisite(HybridSettings settings, ICollection<string> problems)
        {
            var collector = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(settings.collectorSettingsPath);
            if (collector == null)
            {
                problems.Add("找不到 YooAsset Collector Settings: " + settings.collectorSettingsPath);
                return;
            }
            var packages = new SerializedObject(collector).FindProperty("Packages");
            if (packages == null)
            {
                problems.Add("YooAsset Collector Settings 缺少 Packages 配置");
                return;
            }
            var hasGame = Enumerable.Range(0, packages.arraySize)
                .Any(index => packages.GetArrayElementAtIndex(index)
                    .FindPropertyRelative("PackageName")?.stringValue == "Game");
            if (!hasGame)
                problems.Add("YooAsset Collector Settings 中缺少 Game Package");
        }

        static void ValidateBootPackageCollector(HybridSettings settings, ICollection<string> problems)
        {
            var collector = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(settings.collectorSettingsPath);
            var packages = collector == null ? null : new SerializedObject(collector).FindProperty("Packages");
            if (packages == null)
                return;
            for (var i = 0; i < packages.arraySize; i++)
            {
                var package = packages.GetArrayElementAtIndex(i);
                if (package.FindPropertyRelative("PackageName")?.stringValue != settings.bootPackageName)
                    continue;
                var groups = package.FindPropertyRelative("Groups");
                if (groups == null || groups.arraySize != 1)
                {
                    problems.Add("BootPackage 收集组配置无效");
                    return;
                }
                var collectors = groups.GetArrayElementAtIndex(0).FindPropertyRelative("Collectors");
                if (collectors == null || collectors.arraySize != 1
                    || !string.Equals(
                        NormalizeAssetPath(collectors.GetArrayElementAtIndex(0).FindPropertyRelative("CollectPath")?.stringValue),
                        NormalizeAssetPath(settings.bootstrapScenePath),
                        StringComparison.OrdinalIgnoreCase))
                    problems.Add("BootPackage 未正确收集 HybridBootstrap 场景");
                return;
            }
            problems.Add("YooAsset Collector Settings 中缺少 BootPackage");
        }

        static void ValidateSettings(HybridSettings settings)
        {
            if (!CanUseCurrentUnityVersion(out var unityVersionProblem))
                throw new InvalidOperationException(unityVersionProblem);
            if (settings.buildTarget != BuildTarget.Android)
                throw new InvalidOperationException("当前文档方案只支持 Android，Build Target 必须为 Android。");
            if (string.IsNullOrWhiteSpace(settings.hybridClrPackageSpec))
                throw new InvalidOperationException("HybridCLR Package Spec 不能为空");
            if (!string.Equals(settings.businessAssemblyName, "Assembly-CSharp", StringComparison.Ordinal))
                throw new InvalidOperationException("当前业务热更新程序集必须为 Assembly-CSharp");
            if (!Uri.TryCreate(settings.businessRemoteRoot, UriKind.Absolute, out var businessUri)
                || (businessUri.Scheme != Uri.UriSchemeHttp && businessUri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("Business CDN Root 必须是有效的 HTTP/HTTPS 地址");
            if (!HybridAndroidSdk.Validate(settings, out var androidSdkProblem))
                throw new InvalidOperationException(androidSdkProblem);
            var bootPackageRemoteRoot = ResolveBootPackageRemoteRoot(settings);
            if (!Uri.TryCreate(bootPackageRemoteRoot, UriKind.Absolute, out var bootUri)
                || (bootUri.Scheme != Uri.UriSchemeHttp && bootUri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("BootPackage CDN Root 必须是有效的 HTTP/HTTPS 地址");
            foreach (var path in new[] { settings.buildPipelineRoot, settings.resourceRoot, settings.launcherScenePath, settings.bootstrapScenePath, settings.mainScenePath })
                ValidateAssetPath(path);
        }

        static void AddEditorBusyProblems(ICollection<string> problems)
        {
            if (_addRequest != null || _resolveRequest != null)
                problems.Add("部署正在进行，请等待 Unity 完成 Package 刷新");
            if (EditorApplication.isCompiling)
                problems.Add("Unity 正在编译脚本，请等待编译完成");
            if (EditorApplication.isUpdating)
                problems.Add("Unity 正在导入或刷新资源，请等待处理完成");
            if (BuildPipeline.isBuildingPlayer)
                problems.Add("Unity 正在构建应用，请等待构建完成");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                problems.Add("请先退出运行模式");
        }

        static bool IsSafeVersion(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Length <= 64
                && value.All(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-');
        }

        [Serializable]
        sealed class BusinessBootstrapConfig
        {
            public string remoteRoot;
            public string latestManifest;
            public string mainSceneName;
            public string cacheFolder;
            public int timeoutSeconds;
            public string bootPackageName;
            public string bootPackageRemoteRoot;
            public string bootSceneLocation;
            public string embeddedBootPackageVersion;
        }

        static bool CanUseCurrentUnityVersion(out string problem)
        {
            var il2CppUtils = typeof(EditorApplication).Assembly.GetType("UnityEditorInternal.IL2CPPUtils");
            if (il2CppUtils == null)
            {
                problem = "当前 Unity 缺少 UnityEditorInternal.IL2CPPUtils";
                return false;
            }

            var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var supportedHook = il2CppUtils.GetMethod("GetIl2CppFolder", flags, null,
                new[] { typeof(bool).MakeByRefType() }, null);
            if (supportedHook != null)
            {
                problem = null;
                return true;
            }

            var parameterless = il2CppUtils.GetMethod("GetIl2CppFolder", flags, null, Type.EmptyTypes, null);
            if (parameterless == null)
            {
                problem = $"当前 Unity {Application.unityVersion} 的 IL2CPP 路径接口无法识别";
                return false;
            }

            var oldValue = Environment.GetEnvironmentVariable("UNITY_IL2CPP_PATH");
            var probePath = Path.Combine(GetProjectRoot(), "HybridCLRData", "HybridIl2CppPathProbe");
            try
            {
                Environment.SetEnvironmentVariable("UNITY_IL2CPP_PATH", probePath);
                var actual = parameterless.Invoke(null, null) as string;
                if (!string.Equals(Path.GetFullPath(actual ?? string.Empty), Path.GetFullPath(probePath), StringComparison.OrdinalIgnoreCase))
                {
                    problem = $"当前 Unity {Application.unityVersion} 不支持通过 UNITY_IL2CPP_PATH 接管 IL2CPP";
                    return false;
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("UNITY_IL2CPP_PATH", oldValue);
            }

            problem = null;
            return true;
        }

        static bool RequiresCompatibilityPatch()
        {
            var il2CppUtils = typeof(EditorApplication).Assembly.GetType("UnityEditorInternal.IL2CPPUtils");
            if (il2CppUtils == null)
                return false;
            var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            return il2CppUtils.GetMethod("GetIl2CppFolder", flags, null,
                       new[] { typeof(bool).MakeByRefType() }, null) == null
                   && il2CppUtils.GetMethod("GetIl2CppFolder", flags, null, Type.EmptyTypes, null) != null;
        }

        static bool EnsureHybridClrCompatibility(HybridSettings settings)
        {
            if (!RequiresCompatibilityPatch())
                return true;

            var package = FindPackage(settings.hybridClrPackageName)
                ?? throw new InvalidOperationException("HybridCLR Package 尚未注册");
            if (!string.Equals(package.version, HybridClrVersion, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unity 2022.1 兼容补丁仅验证过 HybridCLR {HybridClrVersion}，当前为 {package.version}");

            var embeddedRoot = GetEmbeddedPackageRoot(settings);
            var resolvedRoot = Path.GetFullPath(package.resolvedPath);
            if (!Directory.Exists(embeddedRoot))
                CopyDirectory(resolvedRoot, embeddedRoot);

            var templatePath = GetPackageFilePath(CombineAssetPath(TemplateRelativeRoot, CompatibilityHookTemplate));
            var hookPath = Path.Combine(embeddedRoot, CompatibilityHookRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(templatePath))
                throw new FileNotFoundException("缺少 HybridCLR 兼容补丁模板", templatePath);
            if (!File.Exists(hookPath))
                throw new FileNotFoundException("HybridCLR Hook 文件不存在", hookPath);

            var content = File.ReadAllText(templatePath, Encoding.UTF8);
            var changed = !File.ReadAllText(hookPath, Encoding.UTF8).Replace("\r\n", "\n")
                .Equals(content.Replace("\r\n", "\n"), StringComparison.Ordinal);
            WriteIfChanged(hookPath, content);
            if (changed || !PathsEqual(resolvedRoot, embeddedRoot))
            {
                SetStatus("已部署 HybridCLR 8.11.0 Unity 2022.1 兼容补丁，Unity 刷新后会继续部署。", false);
                if (!PathsEqual(resolvedRoot, embeddedRoot) && _resolveRequest == null)
                {
                    Client.Resolve();
                    _resolveRequest = Client.List(true, true);
                    EditorApplication.update -= PollResolveRequest;
                    EditorApplication.update += PollResolveRequest;
                }
                AssetDatabase.Refresh();
                if (_resolveRequest == null)
                    EditorApplication.delayCall += ResumePendingDeployment;
                return false;
            }

            return true;
        }

        static void PollResolveRequest()
        {
            if (_resolveRequest == null || !_resolveRequest.IsCompleted)
                return;

            EditorApplication.update -= PollResolveRequest;
            if (_resolveRequest.Status == StatusCode.Success)
            {
                var settings = HybridSettings.instance;
                var package = _resolveRequest.Result.FirstOrDefault(item => item.name == settings.hybridClrPackageName);
                var expectedRoot = GetEmbeddedPackageRoot(settings);
                if (package == null || !PathsEqual(package.resolvedPath, expectedRoot))
                {
                    EditorPrefs.DeleteKey(PendingKey);
                    SetStatus("HybridCLR 内嵌包解析完成，但实际来源未切换到: " + expectedRoot, true);
                    _resolveRequest = null;
                    return;
                }
                SetStatus("HybridCLR 已切换到内嵌兼容包，等待 Unity 重新编译后继续部署。", false);
                AssetDatabase.Refresh();
                EditorApplication.delayCall += ResumePendingDeployment;
            }
            else
            {
                EditorPrefs.DeleteKey(PendingKey);
                SetStatus("HybridCLR 内嵌包解析失败: " + _resolveRequest.Error?.message, true);
            }
            _resolveRequest = null;
        }

        static bool IsCompatibilityPatchCurrent(HybridSettings settings)
        {
            var templatePath = GetPackageFilePath(CombineAssetPath(TemplateRelativeRoot, CompatibilityHookTemplate));
            var hookPath = Path.Combine(GetEmbeddedPackageRoot(settings),
                CompatibilityHookRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(templatePath) || !File.Exists(hookPath))
                return false;
            return File.ReadAllText(templatePath, Encoding.UTF8).Replace("\r\n", "\n")
                .Equals(File.ReadAllText(hookPath, Encoding.UTF8).Replace("\r\n", "\n"), StringComparison.Ordinal);
        }

        static void CopyDirectory(string sourceRoot, string destinationRoot)
        {
            foreach (var directory in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = directory.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
            }
            Directory.CreateDirectory(destinationRoot);
            foreach (var file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var destination = Path.Combine(destinationRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destinationRoot);
                File.Copy(file, destination, true);
            }
        }

        static bool PathsEqual(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        static string GetEmbeddedPackageRoot(HybridSettings settings)
        {
            return Path.Combine(GetProjectRoot(), "Packages", settings.hybridClrPackageName + "@" + HybridClrVersion);
        }

        static bool IsPackageInstalled(string packageName)
        {
            return FindPackage(packageName) != null;
        }

        static UnityEditor.PackageManager.PackageInfo FindPackage(string packageName)
        {
            return UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .FirstOrDefault(package => package.name == packageName);
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

        static SerializedProperty RequireProperty(SerializedObject serialized, string name)
        {
            return serialized.FindProperty(name)
                ?? throw new InvalidOperationException($"{serialized.targetObject.GetType().Name} 缺少字段 {name}");
        }

        static void SetBool(SerializedObject serialized, string name, bool value) => RequireProperty(serialized, name).boolValue = value;
        static void SetString(SerializedObject serialized, string name, string value) => RequireProperty(serialized, name).stringValue = value;
        static void SetRelativeString(SerializedProperty parent, string name, string value)
        {
            var property = parent.FindPropertyRelative(name) ?? throw new InvalidOperationException("缺少序列化字段: " + name);
            property.stringValue = value;
        }
        static void SetRelativeInt(SerializedProperty parent, string name, int value)
        {
            var property = parent.FindPropertyRelative(name) ?? throw new InvalidOperationException("缺少序列化字段: " + name);
            property.intValue = value;
        }
        static void SetRelativeBool(SerializedProperty parent, string name, bool value)
        {
            var property = parent.FindPropertyRelative(name) ?? throw new InvalidOperationException("缺少序列化字段: " + name);
            property.boolValue = value;
        }

        static IEnumerable<string> ParseLines(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        static void EnsureAssetDirectory(string assetPath)
        {
            Directory.CreateDirectory(ToFullPath(assetPath));
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        static void CheckFile(string path, ICollection<string> problems)
        {
            try
            {
                if (!File.Exists(ToFullPath(path))) problems.Add("缺少文件: " + path);
            }
            catch (Exception e)
            {
                problems.Add("路径无效: " + path + "（" + e.Message + "）");
            }
        }

        static void CheckProjectFile(string path, string missingMessage, ICollection<string> problems)
        {
            try
            {
                ValidateAssetPath(path);
                if (!File.Exists(ToFullPath(path))) problems.Add(missingMessage + ": " + path);
            }
            catch (Exception e)
            {
                problems.Add("项目路径无效: " + path + "（" + e.Message + "）");
            }
        }

        static void CheckPackageFile(string relativePath, ICollection<string> problems)
        {
            var path = GetPackageFilePath(relativePath);
            if (!File.Exists(path)) problems.Add("Hybrid Package 缺少文件: " + relativePath);
        }

        static string GetPackageFilePath(string relativePath)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(HybridDeployer).Assembly);
            var packageRoot = package?.resolvedPath;
            if (string.IsNullOrWhiteSpace(packageRoot))
                packageRoot = Path.Combine(GetProjectRoot(), "Packages", PackageName);
            return Path.GetFullPath(Path.Combine(
                packageRoot,
                NormalizeAssetPath(relativePath).Replace('/', Path.DirectorySeparatorChar)));
        }

        static void WriteIfChanged(string fullPath, string content)
        {
            content = content.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            if (File.Exists(fullPath) && File.ReadAllText(fullPath, Encoding.UTF8) == content)
                return;
            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
        }

        static string CombineAssetPath(string left, string right) => NormalizeAssetPath(left).TrimEnd('/') + "/" + right.TrimStart('/');
        static string NormalizeAssetPath(string path) => (path ?? string.Empty).Replace('\\', '/').Trim();

        static void ValidateAssetPath(string path)
        {
            path = NormalizeAssetPath(path);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || path.Contains(".."))
                throw new InvalidOperationException("Hybrid 路径必须位于 Assets 下且不能包含 .. : " + path);
            _ = ToFullPath(path);
        }

        static string ToFullPath(string assetPath)
        {
            assetPath = NormalizeAssetPath(assetPath);
            var projectRoot = GetProjectRoot();
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
            var prefix = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Hybrid 路径越出项目目录: " + assetPath);
            return fullPath;
        }

        static string GetProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        static void SetStatus(string message, bool error)
        {
            LastStatus = message;
            LastStatusIsError = error;
            if (error) Debug.LogError("[Hybrid] " + message);
            else Debug.Log("[Hybrid] " + message);
        }

        static void Fail(string message, Exception exception)
        {
            SetStatus(message + ": " + (exception.GetBaseException().Message), true);
            Debug.LogException(exception);
        }
    }
}
