using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Native.Editor.Hybrid
{
    /// <summary>Restores the Unity 2022.1 native contracts after HybridCLR installs its 2022.x runtime.</summary>
    public static class HybridRuntimeCompatibility
    {
        const string SupportedHybridClrVersion = "8.11.0";
        const string Libil2CppVersionRelativePath = "hybridclr/generated/libil2cpp-version.txt";
        const string BoehmGcRelativePath = "gc/BoehmGC.cpp";
        const string CodegenHeaderRelativePath = "codegen/il2cpp-codegen-il2cpp.h";
        const string BrotliSourceRelativePath = "os/ClassLibraryPAL/brotli/include/brotli";
        const string BrotliDestinationRelativePath = "brotli";

        static readonly string[] BrotliHeaders = { "decode.h", "encode.h", "port.h", "types.h" };

        const string NewEphemeronDeclaration =
            "static GC_ms_entry*\npush_ephemerons(GC_ms_entry* mark_stack_ptr, GC_ms_entry* mark_stack_limit);";
        const string OldEphemeronDeclaration = "static void\npush_ephemerons(void);";
        const string NewEphemeronDefinition =
            "static GC_ms_entry*\npush_ephemerons(GC_ms_entry* mark_stack_ptr, GC_ms_entry* mark_stack_limit)";
        const string OldEphemeronDefinition = "static void\npush_ephemerons(void)";
        const string NewEphemeronMark =
            "            if (current_ephemeron->value)\n" +
            "            {\n" +
            "                mark_stack_ptr = GC_mark_and_push((void*)current_ephemeron->value, mark_stack_ptr, mark_stack_limit, (void**)&current_ephemeron->value);\n" +
            "            }";
        const string OldEphemeronMark =
            "            if (current_ephemeron->value && !GC_is_marked(current_ephemeron->value))\n" +
            "            {\n" +
            "                /* the key is marked, so mark the value if needed */\n" +
            "                GC_push_all(&current_ephemeron->value, &current_ephemeron->value + 1);\n" +
            "            }";
        const string NewFieldWriteParameter = "no_infer<T> data";
        const string OldFieldWriteParameter = "T data";
        const int FieldWriteTemplateCount = 5;

        public static bool IsRequired => Application.unityVersion.StartsWith("2022.1.", StringComparison.Ordinal);

        public static bool IsRuntimeInstalled => Directory.Exists(GetLibil2CppRoot());

        public static void EnsureInstalledRuntime()
        {
            if (!IsRequired)
                return;

            var libil2CppRoot = GetLibil2CppRoot();
            if (!Directory.Exists(libil2CppRoot))
                throw new InvalidOperationException("HybridCLR Runtime 尚未安装，缺少目录: " + libil2CppRoot);

            ValidateInstalledVersion(libil2CppRoot);
            var changed = PatchBoehmGc(libil2CppRoot);
            changed |= PatchCodegenFieldWriteTemplates(libil2CppRoot);
            changed |= InstallBrotliCompatibilityHeaders(libil2CppRoot);

            if (!IsPatchCurrent(out var problem))
                throw new InvalidOperationException(problem);

            if (changed)
                Debug.Log("[Hybrid] 已部署 HybridCLR 8.11.0 Unity 2022.1 原生 Runtime 兼容补丁。");
        }

        public static bool IsPatchCurrent(out string problem)
        {
            if (!IsRequired)
            {
                problem = null;
                return true;
            }

            var libil2CppRoot = GetLibil2CppRoot();
            if (!Directory.Exists(libil2CppRoot))
            {
                problem = "HybridCLR Runtime 尚未安装";
                return false;
            }

            var boehmGcPath = Path.Combine(libil2CppRoot, BoehmGcRelativePath);
            if (!File.Exists(boehmGcPath))
            {
                problem = "HybridCLR Runtime 缺少 BoehmGC.cpp";
                return false;
            }

            var boehmGc = NormalizeLineEndings(File.ReadAllText(boehmGcPath, Encoding.UTF8));
            if (!boehmGc.Contains(OldEphemeronDeclaration, StringComparison.Ordinal)
                || !boehmGc.Contains(OldEphemeronDefinition, StringComparison.Ordinal)
                || !boehmGc.Contains(OldEphemeronMark, StringComparison.Ordinal)
                || boehmGc.Contains(NewEphemeronMark, StringComparison.Ordinal))
            {
                problem = "HybridCLR Runtime 的 Unity 2022.1 Boehm GC 兼容补丁缺失";
                return false;
            }

            var codegenHeaderPath = Path.Combine(libil2CppRoot, CodegenHeaderRelativePath);
            if (!File.Exists(codegenHeaderPath))
            {
                problem = "HybridCLR Runtime 缺少 il2cpp-codegen-il2cpp.h";
                return false;
            }

            var codegenHeader = File.ReadAllText(codegenHeaderPath, Encoding.UTF8);
            if (CountOccurrences(codegenHeader, OldFieldWriteParameter) != FieldWriteTemplateCount
                || codegenHeader.Contains(NewFieldWriteParameter, StringComparison.Ordinal))
            {
                problem = "HybridCLR Runtime 的 Unity 2022.1 字段写入模板兼容补丁缺失";
                return false;
            }

            foreach (var header in BrotliHeaders)
            {
                var source = Path.Combine(libil2CppRoot, BrotliSourceRelativePath, header);
                var destination = Path.Combine(libil2CppRoot, BrotliDestinationRelativePath, header);
                if (!FilesEqual(source, destination))
                {
                    problem = "HybridCLR Runtime 的 Brotli 兼容头文件缺失或已过期: " + header;
                    return false;
                }
            }

            problem = null;
            return true;
        }

        static bool PatchBoehmGc(string libil2CppRoot)
        {
            var path = Path.Combine(libil2CppRoot, BoehmGcRelativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException("HybridCLR Runtime 缺少 BoehmGC.cpp", path);

            var text = NormalizeLineEndings(File.ReadAllText(path, Encoding.UTF8));
            if (text.Contains(OldEphemeronDeclaration, StringComparison.Ordinal)
                && text.Contains(OldEphemeronDefinition, StringComparison.Ordinal)
                && text.Contains(OldEphemeronMark, StringComparison.Ordinal)
                && !text.Contains(NewEphemeronMark, StringComparison.Ordinal))
                return false;

            text = ReplaceRequired(text, NewEphemeronDeclaration, OldEphemeronDeclaration, "ephemeron declaration");
            text = ReplaceRequired(text, NewEphemeronDefinition, OldEphemeronDefinition, "ephemeron definition");
            text = ReplaceRequired(text, NewEphemeronMark, OldEphemeronMark, "ephemeron mark callback");
            text = ReplaceRequired(text,
                OldEphemeronMark + "\n        }\n    }\n\n    return mark_stack_ptr;\n}",
                OldEphemeronMark + "\n        }\n    }\n}",
                "ephemeron return value");
            WriteUtf8(path, text);
            return true;
        }

        static bool PatchCodegenFieldWriteTemplates(string libil2CppRoot)
        {
            var path = Path.Combine(libil2CppRoot, CodegenHeaderRelativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException("HybridCLR Runtime 缺少 il2cpp-codegen-il2cpp.h", path);

            var text = File.ReadAllText(path, Encoding.UTF8);
            var oldCount = CountOccurrences(text, OldFieldWriteParameter);
            var newCount = CountOccurrences(text, NewFieldWriteParameter);
            if (oldCount == FieldWriteTemplateCount && newCount == 0)
                return false;
            if (oldCount != 0 || newCount != FieldWriteTemplateCount)
                throw new InvalidOperationException(
                    $"无法识别 HybridCLR Runtime 字段写入模板: patched={oldCount}, original={newCount}");

            WriteUtf8(path, text.Replace(NewFieldWriteParameter, OldFieldWriteParameter));
            return true;
        }

        static bool InstallBrotliCompatibilityHeaders(string libil2CppRoot)
        {
            var changed = false;
            var destinationRoot = Path.Combine(libil2CppRoot, BrotliDestinationRelativePath);
            Directory.CreateDirectory(destinationRoot);
            foreach (var header in BrotliHeaders)
            {
                var source = Path.Combine(libil2CppRoot, BrotliSourceRelativePath, header);
                var destination = Path.Combine(destinationRoot, header);
                if (!File.Exists(source))
                    throw new FileNotFoundException("HybridCLR Runtime 缺少 Brotli 头文件", source);
                if (FilesEqual(source, destination))
                    continue;
                File.Copy(source, destination, true);
                changed = true;
            }
            return changed;
        }

        static void ValidateInstalledVersion(string libil2CppRoot)
        {
            var versionPath = Path.Combine(libil2CppRoot, Libil2CppVersionRelativePath);
            var version = File.Exists(versionPath) ? File.ReadAllText(versionPath, Encoding.UTF8).Trim() : null;
            if (!string.Equals(version, SupportedHybridClrVersion, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Unity 2022.1 原生 Runtime 兼容补丁仅验证过 HybridCLR {SupportedHybridClrVersion}，当前为 {version ?? "未知"}");
        }

        static string GetLibil2CppRoot()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(projectRoot, "HybridCLRData", "LocalIl2CppData-" + Application.platform, "il2cpp", "libil2cpp");
        }

        static string ReplaceRequired(string text, string oldValue, string newValue, string label)
        {
            var index = text.IndexOf(oldValue, StringComparison.Ordinal);
            if (index < 0)
                throw new InvalidOperationException("无法识别 HybridCLR Runtime 源码片段: " + label);
            if (text.IndexOf(oldValue, index + oldValue.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("HybridCLR Runtime 源码片段重复，拒绝自动修改: " + label);
            return text.Substring(0, index) + newValue + text.Substring(index + oldValue.Length);
        }

        static bool FilesEqual(string left, string right)
        {
            if (!File.Exists(left) || !File.Exists(right))
                return false;
            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length)
                return false;
            return File.ReadAllBytes(left).SequenceEqual(File.ReadAllBytes(right));
        }

        static int CountOccurrences(string value, string fragment)
        {
            var count = 0;
            var index = 0;
            while ((index = value.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += fragment.Length;
            }
            return count;
        }

        static string NormalizeLineEndings(string value) => value.Replace("\r\n", "\n");

        static void WriteUtf8(string path, string content)
        {
            content = content.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
    }
}
