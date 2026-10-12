using UnityEditor;
using UnityEngine;

namespace Native.Editor.Hybrid
{
    public static class HybridMenu
    {
        [MenuItem("Tools/Hybrid", false, 1200)]
        public static void Open()
        {
            HybridWindow.OpenWindow();
        }
    }

    internal sealed class HybridWindow : EditorWindow
    {
        Vector2 _scroll;
        bool _showAdvanced;
        string _publishStatus;
        bool _publishFailed;
        double _lastConditionCheck;
        bool _canDeploy;
        bool _canValidate;
        bool _canPublish;
        string _deployBlockReason;
        string _validateBlockReason;
        string _publishBlockReason;

        internal static void OpenWindow()
        {
            var window = GetWindow<HybridWindow>();
            window.titleContent = new GUIContent("Hybrid");
            window.minSize = new Vector2(520, 440);
            window.Show();
        }

        void OnGUI()
        {
            var settings = HybridSettings.instance;
            RefreshConditions();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (DrawActionButton("一键部署热更新功能", 38, _canDeploy, _deployBlockReason))
            {
                settings.Persist();
                HybridDeployer.StartDeployment();
                _lastConditionCheck = 0;
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("业务热更新配置", EditorStyles.boldLabel);
            settings.businessRemoteEnabled = EditorGUILayout.Toggle("启用 CDN 热更新", settings.businessRemoteEnabled);
            using (new EditorGUI.DisabledScope(!settings.businessRemoteEnabled))
                settings.businessRemoteRoot = EditorGUILayout.TextField("CDN 根地址", settings.businessRemoteRoot);
            EditorGUILayout.LabelField("APK 当前版本", PlayerSettings.bundleVersion);
            using (new EditorGUILayout.HorizontalScope())
            {
                settings.businessPublishVersion = EditorGUILayout.TextField("CDN 发布版本", settings.businessPublishVersion);
                if (GUILayout.Button("当前时间", GUILayout.Width(72)))
                    settings.businessPublishVersion = System.DateTime.Now.ToString("yyyyMMddHHmmss");
            }

            EditorGUILayout.Space(8);
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "高级设置", true);
            if (_showAdvanced)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    settings.androidSdkRoot = EditorGUILayout.TextField("Android SDK 路径", settings.androidSdkRoot);
                    settings.mainScenePath = EditorGUILayout.TextField("业务主场景", settings.mainScenePath);
                    settings.businessStartupType = EditorGUILayout.TextField("启动类型", settings.businessStartupType);
                    settings.businessRemoteTimeoutSeconds = EditorGUILayout.IntSlider(
                        "请求超时（秒）",
                        settings.businessRemoteTimeoutSeconds,
                        3,
                        60);
                }
            }

            EditorGUILayout.Space(12);
            if (DrawActionButton("检查当前状态", 32, _canValidate, _validateBlockReason))
                HybridDeployer.ValidateProject(false);

            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(HybridDeployer.LastStatus, HybridDeployer.LastStatusIsError ? MessageType.Error : MessageType.Info);

            EditorGUILayout.Space(12);
            using (new EditorGUILayout.HorizontalScope())
            {
                settings.businessOutputPath = EditorGUILayout.TextField("生成文件根目录", settings.businessOutputPath);
                if (GUILayout.Button("选择...", GUILayout.Width(64)))
                {
                    var selected = EditorUtility.OpenFolderPanel(
                        "选择业务热更新包生成根目录",
                        GetExistingDirectory(settings.GetBusinessOutputRoot()),
                        string.Empty);
                    if (!string.IsNullOrEmpty(selected))
                    {
                        settings.businessOutputPath = selected.Replace('\\', '/');
                        settings.Persist();
                    }
                }
            }
            EditorGUILayout.LabelField(
                "实际路径",
                settings.GetBusinessOutputPath(settings.businessPublishVersion),
                EditorStyles.miniLabel);
            EditorGUILayout.Space(4);
            if (DrawActionButton("生成业务热更新包", 38, _canPublish, _publishBlockReason))
            {
                if (string.IsNullOrWhiteSpace(settings.businessPublishVersion))
                    settings.businessPublishVersion = System.DateTime.Now.ToString("yyyyMMddHHmmss");
                settings.Persist();
                var outputPath = settings.GetBusinessOutputPath(settings.businessPublishVersion);
                _publishFailed = !HybridBuildBridge.BuildGameHotUpdate(
                    settings.businessPublishVersion,
                    outputPath);
                _publishStatus = _publishFailed
                    ? "业务热更包生成失败，请查看 Console。"
                    : "业务热更包生成完成：" + outputPath;
            }
            if (!string.IsNullOrEmpty(_publishStatus))
                EditorGUILayout.HelpBox(_publishStatus, _publishFailed ? MessageType.Error : MessageType.Info);

            EditorGUILayout.EndScrollView();

            if (GUI.changed)
            {
                settings.Persist();
                _lastConditionCheck = 0;
            }
        }

        void OnInspectorUpdate()
        {
            Repaint();
        }

        void RefreshConditions()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastConditionCheck < 1d)
                return;
            _lastConditionCheck = now;
            _canDeploy = HybridDeployer.CanStartDeployment(out _deployBlockReason);
            _canValidate = HybridDeployer.CanValidateProject(out _validateBlockReason);
            _canPublish = HybridDeployer.CanGenerateBusinessHotUpdate(out _publishBlockReason);
        }

        static bool DrawActionButton(string label, float height, bool enabled, string blockReason)
        {
            var content = new GUIContent(label, enabled ? string.Empty : blockReason);
            var rect = GUILayoutUtility.GetRect(content, GUI.skin.button, GUILayout.Height(height), GUILayout.ExpandWidth(true));
            if (!enabled && Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                var message = string.IsNullOrWhiteSpace(blockReason) ? "当前条件不满足。" : blockReason;
                Event.current.Use();
                EditorApplication.delayCall += () => EditorUtility.DisplayDialog("当前无法执行", message, "确定");
            }

            bool clicked;
            using (new EditorGUI.DisabledScope(!enabled))
                clicked = GUI.Button(rect, content);
            return enabled && clicked;
        }

        static string GetExistingDirectory(string path)
        {
            var current = path;
            while (!string.IsNullOrEmpty(current) && !System.IO.Directory.Exists(current))
                current = System.IO.Path.GetDirectoryName(current);
            return string.IsNullOrEmpty(current) ? UnityEngine.Application.dataPath : current;
        }
    }
}
