# DYSJ Hybrid Hot Update

用于把转换后不含热更新接入代码的 DYSJ Unity 项目恢复为 HybridCLR 业务热更新项目。

## 导入

在 Unity Package Manager 中选择 `Add package from disk...`，然后选择本目录下的
`package.json`。导入完成后，通过 `Tools > Hybrid` 打开工具。

当前方案面向 Unity 2022.1 与 Android，并会为 HybridCLR 8.11.0 部署项目所需的兼容层。
目标项目还需要包含 YooAsset、`Res.PackageConfig`、名为 `Game` 的资源包配置，以及有效的
Android SDK。

## 使用顺序

1. 填写 CDN、主场景、启动类型和生成目录等参数。
2. 点击“一键部署热更新功能”，恢复 HybridCLR、启动场景、构建入口和 YooAsset 收集规则。
3. 先通过 Unity Build 构建一次基准 APK，生成与包体匹配的 AOT 基线。
4. 修改业务代码后填写新的发布版本，点击“生成业务热更新包”。

三个主操作按钮会根据当前项目状态自动禁用。点击禁用按钮会显示缺失文件、错误配置、
正在编译或尚未构建基准 APK 等具体原因。
