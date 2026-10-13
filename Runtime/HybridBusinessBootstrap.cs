using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YooAsset;

namespace DYSJ.HotUpdate.Bootstrap
{
    /// <summary>
    /// AOT-only first scene. It loads the versioned Assembly-CSharp business assembly before
    /// Unity deserializes the real game scene, so existing MonoBehaviour GUIDs remain valid.
    /// </summary>
    public sealed class HybridBusinessBootstrap : MonoBehaviour
    {
        const string BusinessAssemblyName = "Assembly-CSharp";
        const string ConfigResourcePath = "HybridCLR/business_bootstrap_config";
        const string EmbeddedManifestResourcePath = "HybridCLR/business_manifest";
        const string EmbeddedDllResourcePath = "HybridCLR/Assembly-CSharp.dll";
        const string PatchAotListResourcePath = "HybridCLR/patch_aot_list";
        const string PatchAotResourcePathFormat = "HybridCLR/AOT/{0}";
        const string BusinessSceneBundleRelativePath = "HybridCLR/hybrid_main_scene.bundle";
        const string CachedManifestFile = "business_cached_manifest.json";
        const string DefaultBootPackageName = "BootPackage";
        const string DefaultBootCanvasLocation = "HybridBootCanvas";
        const string BootPackageVersionKey = "DYSJ.Hybrid.BootPackage.Version";

        [Header("Loading UI")]
        [SerializeField] Slider slider;
        [SerializeField] TMP_Text labPrompt;
        [SerializeField] TMP_Text labProgress;
        [SerializeField] GameObject fallbackBootCanvas;

        float _progress;
        string _status = string.Empty;
        ResourcePackage _bootPackage;
        AssetHandle _bootCanvasHandle;
        GameObject _bootCanvasInstance;
        bool _bootPackageUsesHostMode;

        IEnumerator Start()
        {
            DontDestroyOnLoad(gameObject);
            BindLoadingView(fallbackBootCanvas);
            SetLoading("正在准备启动资源...", 0.02f);

            BootstrapConfig config;
            try
            {
                config = LoadConfig();
            }
            catch (Exception e)
            {
                Fail("APK 内置启动配置无效", e);
                yield break;
            }

#if !UNITY_EDITOR
            yield return InitializeAndLoadBootPackage(config);
#endif

            SetLoading("正在准备游戏代码...", 0.05f);

#if UNITY_EDITOR
            yield return LoadMainScene(config.mainSceneName);
#else
            Coroutine bootPackageUpdate = null;
            if (_bootPackageUsesHostMode)
                bootPackageUpdate = StartCoroutine(UpdateBootPackageInBackground(config));

            try
            {
                LoadAotMetadata();
                SetLoading("正在校验内置资源...", 0.2f);
            }
            catch (Exception e)
            {
                Fail("APK 内置热更新环境无效", e);
                yield break;
            }

            var embeddedBytes = TryLoadEmbedded(out var embeddedVersion);
            if (embeddedBytes == null)
            {
                Fail("APK 缺少可用的默认业务代码", null);
                yield break;
            }
            SetLoading("正在检查业务更新...", 0.3f);

            byte[] assemblyBytes = null;
            var remoteMatchesEmbedded = false;
            if (!string.IsNullOrWhiteSpace(config.remoteRoot))
            {
                yield return TryLoadRemote(
                    config,
                    embeddedVersion,
                    bytes => assemblyBytes = bytes,
                    () => remoteMatchesEmbedded = true);
            }

            if (assemblyBytes == null && remoteMatchesEmbedded)
            {
                assemblyBytes = embeddedBytes;
                Debug.Log("[Hybrid] 使用 APK 内置默认业务代码: " + embeddedVersion);
            }
            if (assemblyBytes == null && !remoteMatchesEmbedded)
                assemblyBytes = TryLoadCached(config);
            if (assemblyBytes == null)
            {
                assemblyBytes = embeddedBytes;
                Debug.Log("[Hybrid] 使用 APK 内置默认业务代码: " + embeddedVersion);
            }

            try
            {
                SetLoading("正在加载游戏代码...", 0.92f);
                var preloaded = FindLoadedBusinessAssembly();
                if (preloaded != null)
                {
                    throw new InvalidOperationException(
                        "检测到业务程序集已被编译进 IL2CPP，动态业务 DLL 无法接管；" +
                        "默认业务程序集必须以内置 .dll.bytes 资源提供: " + preloaded.FullName);
                }

                Assembly.Load(assemblyBytes);
                Debug.Log("[Hybrid] 业务程序集已加载并接管: " + BusinessAssemblyName);
            }
            catch (Exception e)
            {
                Fail("业务代码加载失败", e);
                yield break;
            }

            // Main resets the global YooAsset runtime during its own initialization. Finish the
            // parallel boot-package cache operation before switching scenes so it cannot be cut off.
            if (bootPackageUpdate != null)
            {
                SetLoading("正在完成启动资源同步...", 0.94f);
                yield return bootPackageUpdate;
            }

            yield return LoadMainScene(config.mainSceneName);
#endif
        }

#if !UNITY_EDITOR
        IEnumerator InitializeAndLoadBootPackage(BootstrapConfig config)
        {
            if (!YooAssets.Initialized)
                YooAssets.Initialize();

            var packageName = string.IsNullOrWhiteSpace(config.bootPackageName)
                ? DefaultBootPackageName
                : config.bootPackageName.Trim();
            var preferredVersion = PlayerPrefs.GetString(BootPackageVersionKey, string.Empty);
            if (string.IsNullOrWhiteSpace(preferredVersion))
                preferredVersion = config.embeddedBootPackageVersion;

            if (!string.IsNullOrWhiteSpace(config.bootPackageRemoteRoot)
                && !string.IsNullOrWhiteSpace(preferredVersion))
            {
                var hostPackage = YooAssets.CreatePackage(packageName);
                var hostParameters = new HostPlayModeParameters
                {
                    BuildinFileSystemParameters = FileSystemParameters.CreateDefaultBuildinFileSystemParameters(),
                    CacheFileSystemParameters = FileSystemParameters.CreateDefaultCacheFileSystemParameters(
                        new BootRemoteServices(config.bootPackageRemoteRoot)),
                };
                var initialize = hostPackage.InitializeAsync(hostParameters);
                yield return initialize;
                if (initialize.Status == EOperationStatus.Succeed)
                {
                    var manifest = hostPackage.UpdatePackageManifestAsync(
                        preferredVersion.Trim(),
                        Mathf.Clamp(config.timeoutSeconds, 3, 60));
                    yield return manifest;
                    if (manifest.Status == EOperationStatus.Succeed)
                    {
                        var missing = hostPackage.CreateResourceDownloader(1, 1);
                        if (missing.TotalDownloadCount == 0)
                        {
                            _bootPackage = hostPackage;
                            _bootPackageUsesHostMode = true;
                            yield return LoadBootCanvasFromPackage(config);
                            if (_bootCanvasInstance != null)
                                yield break;
                        }
                    }
                }

                Debug.LogWarning("[Hybrid] 缓存启动界面不可用，回退 APK 内置 BootPackage。");
                yield return DestroyBootPackage(hostPackage);
                if (_bootPackage == hostPackage)
                    _bootPackage = null;
                _bootPackageUsesHostMode = false;
            }

            var offlinePackage = YooAssets.CreatePackage(packageName);
            var offlineParameters = new OfflinePlayModeParameters
            {
                BuildinFileSystemParameters = FileSystemParameters.CreateDefaultBuildinFileSystemParameters(),
            };
            var offlineInitialize = offlinePackage.InitializeAsync(offlineParameters);
            yield return offlineInitialize;
            if (offlineInitialize.Status != EOperationStatus.Succeed)
            {
                Debug.LogWarning("[Hybrid] APK 内置 BootPackage 初始化失败，继续使用内置启动界面: " + offlineInitialize.Error);
                yield return DestroyBootPackage(offlinePackage);
                yield break;
            }

            var version = offlinePackage.RequestPackageVersionAsync(false, Mathf.Clamp(config.timeoutSeconds, 3, 60));
            yield return version;
            if (version.Status != EOperationStatus.Succeed)
            {
                Debug.LogWarning("[Hybrid] APK 内置 BootPackage 缺少版本信息，继续使用内置启动界面: " + version.Error);
                yield return DestroyBootPackage(offlinePackage);
                yield break;
            }

            var offlineManifest = offlinePackage.UpdatePackageManifestAsync(
                version.PackageVersion,
                Mathf.Clamp(config.timeoutSeconds, 3, 60));
            yield return offlineManifest;
            if (offlineManifest.Status != EOperationStatus.Succeed)
            {
                Debug.LogWarning("[Hybrid] APK 内置 BootPackage 清单无效，继续使用内置启动界面: " + offlineManifest.Error);
                yield return DestroyBootPackage(offlinePackage);
                yield break;
            }

            _bootPackage = offlinePackage;
            _bootPackageUsesHostMode = false;
            yield return LoadBootCanvasFromPackage(config);
        }

        IEnumerator LoadBootCanvasFromPackage(BootstrapConfig config)
        {
            var location = string.IsNullOrWhiteSpace(config.bootCanvasLocation)
                ? DefaultBootCanvasLocation
                : config.bootCanvasLocation.Trim();
            _bootCanvasHandle = _bootPackage.LoadAssetAsync<GameObject>(location);
            yield return _bootCanvasHandle;
            if (_bootCanvasHandle.Status != EOperationStatus.Succeed)
            {
                Debug.LogWarning("[Hybrid] BootPackage 无法加载 HybridBootCanvas Prefab，继续使用内置界面: " + _bootCanvasHandle.LastError);
                ReleaseBootCanvasHandle();
                yield break;
            }

            var instantiate = _bootCanvasHandle.InstantiateAsync(false);
            yield return instantiate;
            if (instantiate.Status != EOperationStatus.Succeed || instantiate.Result == null)
            {
                Debug.LogWarning("[Hybrid] HybridBootCanvas Prefab 实例化失败，继续使用内置界面: " + instantiate.Error);
                ReleaseBootCanvasHandle();
                yield break;
            }

            var replacement = instantiate.Result;
            replacement.name = DefaultBootCanvasLocation;
            if (!TryBindLoadingView(replacement))
            {
                Destroy(replacement);
                Debug.LogWarning("[Hybrid] HybridBootCanvas Prefab 缺少 Slider、lab_prompt 或 lab_progress，继续使用内置界面。");
                ReleaseBootCanvasHandle();
                yield break;
            }

            _bootCanvasInstance = replacement;
            SetLoading(_status, _progress);
            replacement.SetActive(true);
            if (fallbackBootCanvas != null && fallbackBootCanvas != replacement)
            {
                fallbackBootCanvas.SetActive(false);
                Destroy(fallbackBootCanvas);
                fallbackBootCanvas = null;
            }
        }

        IEnumerator UpdateBootPackageInBackground(BootstrapConfig config)
        {
            var package = _bootPackage;
            if (package == null || string.IsNullOrWhiteSpace(config.bootPackageRemoteRoot))
                yield break;

            var requestVersion = package.RequestPackageVersionAsync(true, Mathf.Clamp(config.timeoutSeconds, 3, 60));
            yield return requestVersion;
            if (requestVersion.Status != EOperationStatus.Succeed)
            {
                Debug.LogWarning("[Hybrid] 后台检查启动界面更新失败: " + requestVersion.Error);
                yield break;
            }

            var cachedVersion = PlayerPrefs.GetString(BootPackageVersionKey, string.Empty);
            if (string.Equals(requestVersion.PackageVersion, cachedVersion, StringComparison.Ordinal))
                yield break;

            var updateManifest = package.UpdatePackageManifestAsync(
                requestVersion.PackageVersion,
                Mathf.Clamp(config.timeoutSeconds, 3, 60));
            yield return updateManifest;
            if (updateManifest.Status != EOperationStatus.Succeed)
            {
                Debug.LogWarning("[Hybrid] 后台更新启动界面清单失败: " + updateManifest.Error);
                yield break;
            }

            var downloader = package.CreateResourceDownloader(2, 2);
            downloader.BeginDownload();
            yield return downloader;
            if (downloader.Status != EOperationStatus.Succeed)
            {
                Debug.LogWarning("[Hybrid] 后台下载启动界面更新失败: " + downloader.Error);
                yield break;
            }

            PlayerPrefs.SetString(BootPackageVersionKey, requestVersion.PackageVersion);
            PlayerPrefs.Save();
            Debug.Log("[Hybrid] 启动界面更新已缓存，将在下次启动生效: " + requestVersion.PackageVersion);
        }

        static IEnumerator DestroyBootPackage(ResourcePackage package)
        {
            if (package == null)
                yield break;
            var destroy = package.DestroyAsync();
            yield return destroy;
            if (YooAssets.Initialized)
                YooAssets.RemovePackage(package);
        }
#endif

        void OnDestroy()
        {
            ReleaseBootCanvasHandle();
        }

        void ReleaseBootCanvasHandle()
        {
            if (_bootCanvasHandle != null && _bootCanvasHandle.IsValid)
                _bootCanvasHandle.Release();
            _bootCanvasHandle = null;
        }

        void BindLoadingView(GameObject root)
        {
            if (!TryBindLoadingView(root))
                Debug.LogWarning("[Hybrid] 内置 HybridBootCanvas 缺少 Slider、lab_prompt 或 lab_progress。");
        }

        bool TryBindLoadingView(GameObject root)
        {
            if (root == null)
                return false;
            var nextSlider = FindNamedComponent<Slider>(root, "Slider");
            var nextPrompt = FindNamedComponent<TMP_Text>(root, "lab_prompt");
            var nextProgress = FindNamedComponent<TMP_Text>(root, "lab_progress");
            if (nextSlider == null || nextPrompt == null || nextProgress == null)
                return false;
            slider = nextSlider;
            labPrompt = nextPrompt;
            labProgress = nextProgress;
            return true;
        }

        static T FindNamedComponent<T>(GameObject root, string objectName) where T : Component
        {
            return root.GetComponentsInChildren<T>(true)
                .FirstOrDefault(item => item != null
                    && string.Equals(item.gameObject.name, objectName, StringComparison.Ordinal));
        }

        IEnumerator TryLoadRemote(
            BootstrapConfig config,
            string embeddedVersion,
            Action<byte[]> onLoaded,
            Action onSameAsEmbedded)
        {
            SetLoading("正在检查业务更新...", 0.32f);
            var root = config.remoteRoot.Trim().TrimEnd('/');
            var manifestName = string.IsNullOrWhiteSpace(config.latestManifest)
                ? "business_latest.json"
                : config.latestManifest.Trim();
            if (!IsSafeFileName(manifestName))
            {
                Debug.LogError("[Hybrid] 远端业务清单名称非法: " + manifestName);
                yield break;
            }

            string manifestJson = null;
            var manifestUrl = root + "/" + manifestName + "?t=" + DateTime.UtcNow.Ticks;
            Debug.Log("[Hybrid] 检查业务更新清单: " + manifestUrl);
            using (var request = UnityWebRequest.Get(manifestUrl))
            {
                request.SetRequestHeader("Cache-Control", "no-cache, no-store");
                request.SetRequestHeader("Pragma", "no-cache");
                request.timeout = Mathf.Clamp(config.timeoutSeconds, 3, 60);
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    SetLoading("正在检查业务更新...", Mathf.Lerp(0.32f, 0.4f, request.downloadProgress));
                    yield return null;
                }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("[Hybrid] 获取业务更新清单失败，尝试本地缓存: " + request.error);
                    yield break;
                }
                manifestJson = request.downloadHandler.text;
            }

            BusinessManifest manifest;
            try
            {
                manifest = ParseAndValidateManifest(manifestJson);
            }
            catch (Exception e)
            {
                Debug.LogError("[Hybrid] 远端业务清单无效: " + e.Message);
                yield break;
            }

            if (string.Equals(manifest.version, embeddedVersion, StringComparison.Ordinal))
            {
                Debug.Log("[Hybrid] 远端业务版本与 APK 内置版本一致，无需下载: " + embeddedVersion);
                onSameAsEmbedded();
                yield break;
            }

            Debug.Log($"[Hybrid] 发现不同版本业务代码: embedded={embeddedVersion}, remote={manifest.version}");

            var downloadStatus = "正在下载业务更新 " + manifest.version + "...";
            SetLoading(downloadStatus, 0.42f);
            byte[] bytes;
            using (var request = UnityWebRequest.Get(root + "/" + manifest.fileName))
            {
                request.timeout = Mathf.Clamp(config.timeoutSeconds, 3, 60);
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    SetLoading(downloadStatus, Mathf.Lerp(0.42f, 0.82f, request.downloadProgress));
                    yield return null;
                }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("[Hybrid] 下载业务代码失败，尝试本地缓存: " + request.error);
                    yield break;
                }
                bytes = request.downloadHandler.data;
            }

            try
            {
                SetLoading("正在校验业务更新...", 0.86f);
                ValidateAssemblyBytes(manifest, bytes);
                SaveCache(config, manifestJson, manifest, bytes);
                Debug.Log("[Hybrid] 使用远端业务代码: " + manifest.version);
                onLoaded(bytes);
            }
            catch (Exception e)
            {
                Debug.LogError("[Hybrid] 远端业务代码校验失败: " + e.Message);
            }
        }

        static byte[] TryLoadCached(BootstrapConfig config)
        {
            try
            {
                var cacheRoot = GetCacheRoot(config);
                var manifestPath = Path.Combine(cacheRoot, CachedManifestFile);
                if (!File.Exists(manifestPath))
                    return null;
                var manifest = ParseAndValidateManifest(File.ReadAllText(manifestPath, Encoding.UTF8));
                var dllPath = Path.Combine(cacheRoot, manifest.fileName);
                if (!File.Exists(dllPath))
                    return null;
                var bytes = File.ReadAllBytes(dllPath);
                ValidateAssemblyBytes(manifest, bytes);
                Debug.Log("[Hybrid] 使用已缓存业务代码: " + manifest.version);
                return bytes;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Hybrid] 业务代码缓存无效: " + e.Message);
                return null;
            }
        }

        static byte[] TryLoadEmbedded(out string version)
        {
            version = null;
            try
            {
                var manifestAsset = Resources.Load<TextAsset>(EmbeddedManifestResourcePath);
                var dllAsset = Resources.Load<TextAsset>(EmbeddedDllResourcePath);
                if (manifestAsset == null || dllAsset == null)
                    throw new FileNotFoundException("APK 缺少 Assembly-CSharp 基线资源");
                var manifest = ParseAndValidateManifest(manifestAsset.text);
                ValidateAssemblyBytes(manifest, dllAsset.bytes);
                version = manifest.version;
                Debug.Log("[Hybrid] APK 内置默认业务代码校验通过: " + manifest.version);
                return dllAsset.bytes;
            }
            catch (Exception e)
            {
                Debug.LogError("[Hybrid] APK 内置业务代码无效: " + e);
                return null;
            }
        }

        static Assembly FindLoadedBusinessAssembly()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(
                    assembly.GetName().Name,
                    BusinessAssemblyName,
                    StringComparison.Ordinal));
        }

        static void LoadAotMetadata()
        {
            var list = Resources.Load<TextAsset>(PatchAotListResourcePath)
                ?? throw new FileNotFoundException("APK 缺少 patch_aot_list.txt");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in list.text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                var parts = line.Split('|');
                if (parts.Length != 2 || !IsSafeAssemblyFileName(parts[0].Trim()) || !IsSha256(parts[1].Trim()))
                    throw new InvalidDataException("AOT 清单条目无效: " + line);
                var assemblyName = parts[0].Trim();
                if (!names.Add(assemblyName))
                    throw new InvalidDataException("AOT 清单存在重复项: " + assemblyName);
                var resourcePath = string.Format(PatchAotResourcePathFormat, RemoveLastExtension(assemblyName + ".bytes"));
                var asset = Resources.Load<TextAsset>(resourcePath)
                    ?? throw new FileNotFoundException("APK 缺少 AOT 元数据: " + assemblyName);
                VerifySha256(asset.bytes, parts[1].Trim());
                LoadMetadataForAotAssembly(asset.bytes, assemblyName);
            }
        }

        static void LoadMetadataForAotAssembly(byte[] bytes, string assemblyName)
        {
            var runtimeApi = Type.GetType("HybridCLR.RuntimeApi, HybridCLR.Runtime", false)
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("HybridCLR.RuntimeApi", false))
                    .FirstOrDefault(type => type != null)
                ?? throw new InvalidOperationException("HybridCLR RuntimeApi 未加载");
            var method = runtimeApi.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(candidate =>
                    candidate.Name == "LoadMetadataForAOTAssembly"
                    && candidate.GetParameters().Length == 2)
                ?? throw new MissingMethodException(runtimeApi.FullName, "LoadMetadataForAOTAssembly");
            var modeType = method.GetParameters()[1].ParameterType;
            var superSet = Enum.Parse(modeType, "SuperSet");
            var code = method.Invoke(null, new[] { (object)bytes, superSet });
            var codeName = code?.ToString();
            if (codeName != "OK" && codeName != "HOMOLOGOUS_ASSEMBLY_HAS_LOADED")
                throw new InvalidOperationException("LoadMetadataForAOTAssembly(" + assemblyName + ") => " + codeName);
        }

        static BootstrapConfig LoadConfig()
        {
            var asset = Resources.Load<TextAsset>(ConfigResourcePath);
            if (asset == null)
            {
                return new BootstrapConfig
                {
                    mainSceneName = "Main",
                    timeoutSeconds = 12,
                    bootPackageName = DefaultBootPackageName,
                    bootCanvasLocation = DefaultBootCanvasLocation,
                };
            }
            var config = JsonUtility.FromJson<BootstrapConfig>(asset.text)
                ?? throw new InvalidDataException("business_bootstrap_config.json 格式无效");
            if (string.IsNullOrWhiteSpace(config.mainSceneName))
                config.mainSceneName = "Main";
            if (string.IsNullOrWhiteSpace(config.bootPackageName))
                config.bootPackageName = DefaultBootPackageName;
            if (string.IsNullOrWhiteSpace(config.bootCanvasLocation))
                config.bootCanvasLocation = DefaultBootCanvasLocation;
            if (config.timeoutSeconds <= 0)
                config.timeoutSeconds = 12;
            return config;
        }

        static void SaveCache(BootstrapConfig config, string manifestJson, BusinessManifest manifest, byte[] bytes)
        {
            var root = GetCacheRoot(config);
            Directory.CreateDirectory(root);
            WriteAtomic(Path.Combine(root, manifest.fileName), bytes);
            WriteAtomic(Path.Combine(root, CachedManifestFile), Encoding.UTF8.GetBytes(manifestJson));
        }

        static void WriteAtomic(string destination, byte[] bytes)
        {
            var temporary = destination + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(destination))
                File.Delete(destination);
            File.Move(temporary, destination);
        }

        static string GetCacheRoot(BootstrapConfig config)
        {
            var folder = string.IsNullOrWhiteSpace(config.cacheFolder) ? "HybridCLRBusiness" : config.cacheFolder.Trim();
            if (!IsSafeFileName(folder))
                folder = "HybridCLRBusiness";
            return Path.Combine(Application.persistentDataPath, folder);
        }

        static BusinessManifest ParseAndValidateManifest(string json)
        {
            var manifest = JsonUtility.FromJson<BusinessManifest>(json)
                ?? throw new InvalidDataException("清单 JSON 为空");
            if (!string.Equals(manifest.assemblyName, BusinessAssemblyName + ".dll", StringComparison.Ordinal))
                throw new InvalidDataException("程序集名称错误: " + manifest.assemblyName);
            if (!string.Equals(manifest.buildTarget, "Android", StringComparison.Ordinal))
                throw new InvalidDataException("构建平台错误: " + manifest.buildTarget);
            if (string.IsNullOrWhiteSpace(manifest.version))
                throw new InvalidDataException("版本号为空");
            if (!IsSafeFileName(manifest.fileName) || !manifest.fileName.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DLL 文件名非法: " + manifest.fileName);
            if (!IsSha256(manifest.sha256) || manifest.length <= 0)
                throw new InvalidDataException("DLL 校验信息无效");
            return manifest;
        }

        static void ValidateAssemblyBytes(BusinessManifest manifest, byte[] bytes)
        {
            if (bytes == null || bytes.LongLength != manifest.length)
                throw new InvalidDataException("DLL 长度不匹配");
            VerifySha256(bytes, manifest.sha256);
        }

        static void VerifySha256(byte[] bytes, string expected)
        {
            using var sha = SHA256.Create();
            var actual = string.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2")));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 不匹配");
        }

        static bool IsSafeFileName(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.IndexOf('/') < 0
                && value.IndexOf('\\') < 0
                && value.IndexOf("..", StringComparison.Ordinal) < 0
                && value.All(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-');
        }

        static bool IsSafeAssemblyFileName(string value)
        {
            return IsSafeFileName(value) && value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsSha256(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length == 64 && value.All(Uri.IsHexDigit);
        }

        static string RemoveLastExtension(string value)
        {
            var index = value.LastIndexOf('.');
            return index > 0 ? value.Substring(0, index) : value;
        }

        IEnumerator LoadMainScene(string sceneName)
        {
            SetLoading("正在进入游戏...", 0.95f);
            var expectedSceneName = string.IsNullOrWhiteSpace(sceneName) ? "Main" : sceneName;
#if UNITY_EDITOR
            var operation = SceneManager.LoadSceneAsync(expectedSceneName);
#else
            var bundleUrl = Path.Combine(Application.streamingAssetsPath, BusinessSceneBundleRelativePath)
                .Replace('\\', '/');
            AssetBundle sceneBundle;
            using (var request = UnityWebRequestAssetBundle.GetAssetBundle(bundleUrl))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Fail("无法读取内置业务场景资源包", new IOException(request.error));
                    yield break;
                }
                sceneBundle = DownloadHandlerAssetBundle.GetContent(request);
            }

            if (sceneBundle == null)
            {
                Fail("内置业务场景资源包无效", null);
                yield break;
            }

            var scenePath = sceneBundle.GetAllScenePaths()
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    expectedSceneName,
                    StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(scenePath))
            {
                sceneBundle.Unload(false);
                Fail("内置业务场景资源包缺少场景: " + expectedSceneName, null);
                yield break;
            }

            Debug.Log("[Hybrid] 从内置资源包加载业务场景: " + scenePath);
            var operation = SceneManager.LoadSceneAsync(scenePath, LoadSceneMode.Single);
#endif
            if (operation == null)
            {
#if !UNITY_EDITOR
                sceneBundle.Unload(false);
#endif
                Fail("无法加载主场景", null);
                yield break;
            }
            while (!operation.isDone)
            {
                SetLoading("正在进入游戏...", Mathf.Lerp(0.95f, 1f, Mathf.Clamp01(operation.progress / 0.9f)));
                yield return null;
            }
            SetLoading("正在进入游戏...", 1f);
#if !UNITY_EDITOR
            sceneBundle.Unload(false);
#endif
            Destroy(gameObject);
        }

        void Fail(string message, Exception exception)
        {
            var error = exception == null ? message : message + "\n" + exception.Message;
            SetLoading(error, _progress);
            Debug.LogError("[Hybrid] " + message + (exception == null ? string.Empty : ": " + exception));
        }

        void SetLoading(string status, float progress)
        {
            _status = status ?? string.Empty;
            _progress = Mathf.Clamp01(progress);
            if (slider != null)
                slider.SetValueWithoutNotify(_progress);
            if (labProgress != null)
                labProgress.text = Mathf.RoundToInt(_progress * 100f) + "%";
            if (labPrompt != null)
                labPrompt.text = status;
        }

        [Serializable]
        sealed class BootstrapConfig
        {
            public string remoteRoot;
            public string latestManifest;
            public string mainSceneName;
            public string cacheFolder;
            public int timeoutSeconds;
            public string bootPackageName;
            public string bootPackageRemoteRoot;
            public string bootCanvasLocation;
            public string embeddedBootPackageVersion;
        }

        sealed class BootRemoteServices : IRemoteServices
        {
            readonly string _root;

            public BootRemoteServices(string root)
            {
                _root = (root ?? string.Empty).Trim().TrimEnd('/');
            }

            public string GetRemoteMainURL(string fileName)
            {
                return _root + "/" + fileName;
            }

            public string GetRemoteFallbackURL(string fileName)
            {
                return GetRemoteMainURL(fileName);
            }
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
    }
}
