// Shared by the inbox .NET Framework entry and the .NET 10 bootstrap.
// Keep this file compatible with the inbox C# 5 compiler.
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace AutumnOS.Startup
{
    [DataContract]
    internal sealed class StartupFailure
    {
        [DataMember] public int SchemaVersion = 1;
        [DataMember] public string Code = "STARTUP_FAILED";
        [DataMember] public string Role = "";
        [DataMember] public string Stage = "";
        [DataMember] public string Target = "";
        [DataMember] public string BuildId = "";
        [DataMember] public string SourceSnapshotId = "";
        [DataMember] public string CreatedUtc = "";
        [DataMember] public string ExceptionType = "";
        [DataMember] public int HResult;
        [DataMember] public int? NativeErrorCode;

        internal static StartupFailure Create(Exception error, string role, string stage,
            string target, string buildId, string sourceSnapshotId)
        {
            var native = error as Win32Exception;
            var failure = new StartupFailure {
                Role = role, Stage = stage, Target = target, BuildId = buildId,
                SourceSnapshotId = sourceSnapshotId, CreatedUtc = DateTimeOffset.UtcNow.ToString("o"),
                ExceptionType = error.GetType().FullName ?? "Exception", HResult = error.HResult,
                NativeErrorCode = native == null ? (int?)null : native.NativeErrorCode
            };
            if (error is PathTooLongException) failure.Code = "STARTUP_PATH_TOO_LONG";
            switch (failure.NativeErrorCode)
            {
                // ERROR_INVALID_IMAGE_HASH / ACCESS_DISABLED_BY_POLICY /
                // SYSTEM_INTEGRITY_POLICY_VIOLATION. Do not guess which policy issued it.
                case 577: case 1260: case 4551: failure.Code = "STARTUP_WINDOWS_POLICY_BLOCKED"; break;
                case 5: failure.Code = "STARTUP_ACCESS_DENIED"; break;
                case 2: case 3: failure.Code = "STARTUP_FILE_MISSING"; break;
                case 126: failure.Code = "STARTUP_DEPENDENCY_MISSING"; break;
                case 193: failure.Code = "STARTUP_BAD_IMAGE"; break;
                case 206: failure.Code = "STARTUP_PATH_TOO_LONG"; break;
            }
            // Only fixed product codes may be copied. Never log arbitrary exception
            // messages, arguments, environment variables, stack traces or account data.
            if (failure.Code == "STARTUP_FAILED" && error is IOException)
            {
                switch (error.Message)
                {
                    case "UPDATE_BOOTSTRAP_ARGUMENT_REJECTED": case "UPDATE_TARGET_ROOT_INVALID":
                    case "UPDATE_RECOVERY_START_FAILED": case "UPDATE_RECOVERY_REQUIRED":
                    case "UPDATE_CLIENT_START_FAILED": case "PORTABLE_ARGUMENT_REJECTED":
                    case "PORTABLE_PREPARATION_BUSY": case "PORTABLE_UPDATED_RUNTIME_MISSING":
                    case "PORTABLE_CLIENT_START_FAILED": case "PORTABLE_PAYLOAD_INVALID":
                    case "PORTABLE_LAYOUT_INVALID": case "PORTABLE_REDIRECTED_PATH":
                    case "PORTABLE_ARCHIVE_INVALID": case "PORTABLE_CACHE_FILE_CHANGED":
                    case "PORTABLE_CACHE_IDENTITY_INVALID": case "PORTABLE_CHANGED_BUILD_WITHOUT_TRANSACTION":
                    case "PORTABLE_CURRENT_BUILD_FILE_CHANGED": case "PORTABLE_ENTRY_BINDING_INVALID":
                    case "PORTABLE_FILE_HASH_FAILED": case "PORTABLE_INVENTORY_INVALID":
                    case "PORTABLE_PATH_REJECTED": case "PORTABLE_PAYLOAD_HASH_FAILED":
                    case "PORTABLE_PREPARATION_STATE_INVALID": case "PORTABLE_RECEIPT_INVALID":
                    case "PORTABLE_RECOVERY_RECORD_INVALID": case "PORTABLE_STABLE_ENTRY_INVALID":
                    case "PORTABLE_UNUSED_ARCHIVE_CONTENT": case "UPDATE_INSTALL_RECEIPT_INVALID":
                    case "UPDATE_RECORD_INVALID": case "UPDATE_TRANSACTION_INVALID":
                        failure.Code = error.Message; break;
                }
                failure.AcceptUpdateSecurityCode(error.Message);
            }
            return failure;
        }

        // The .NET 10 bootstrap may pass the Code of a typed UpdateException.
        // This shared C# 5 file cannot reference that assembly. Keep the original
        // exception identity/HRESULT and accept only this fixed diagnostic set.
        internal void AcceptUpdateSecurityCode(string code)
        {
            if (Code != "STARTUP_FAILED") return;
            switch (code)
            {
                case "UPDATE_INSTALL_RECEIPT_REQUIRED": case "UPDATE_MINIMUM_SECURITY_VERSION":
                case "UPDATE_FAILED_BUILD_BLOCKED": case "UPDATE_SECURITY_DATA_ROOT_MISMATCH":
                case "UPDATE_SECURITY_LEDGER_BUSY": case "UPDATE_SECURITY_LEDGER_INVALID":
                case "UPDATE_SECURITY_LEDGER_MISSING": case "UPDATE_SECURITY_SEQUENCE_CONFLICT":
                case "UPDATE_SECURITY_FAILED_BUILDS_LIMIT": case "UPDATE_SECURITY_LEGACY_BINDING_INVALID":
                case "UPDATE_SECURITY_LEGACY_RECOVERY_REQUIRED": case "UPDATE_SECURITY_LEGACY_SOURCES_LIMIT":
                case "UPDATE_STATE_INVALID": case "UPDATE_METADATA_INVALID":
                case "UPDATE_METADATA_ENCODING_OR_SIZE":
                    Code = code; break;
            }
        }

        internal string TryWrite(string dataDirectory)
        {
            try
            {
                if (string.IsNullOrEmpty(dataDirectory)) return "";
                string directory = Path.Combine(Path.GetFullPath(dataDirectory), "Logs", "Startup");
                Plain(directory);
                Directory.CreateDirectory(directory);
                Plain(directory);
                string path = Path.Combine(directory, "startup-" + Guid.NewGuid().ToString("N") + ".json");
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(StartupFailure)).WriteObject(stream, this);
                    stream.Flush(true);
                }
                return path;
            }
            catch (Exception error)
            {
                if (error is OutOfMemoryException) throw;
                return "";
            }
        }

        internal string Message(string reportPath)
        {
            string detail = Code == "STARTUP_WINDOWS_POLICY_BLOCKED"
                ? "Windows 安全策略阻止了程序启动。需要符合本机策略、具有可信发布者签名的构建。"
                : Code == "STARTUP_PATH_TOO_LONG"
                ? "程序所在目录过深。请正常退出后，将 AutumnOS.exe 与同级 AutumnOS_Data 一起移到更短的目录，再重试；不要删除 AutumnOS_Data。"
                : Code == "UPDATE_INSTALL_RECEIPT_REQUIRED" || Code == "UPDATE_INSTALL_RECEIPT_INVALID"
                ? "安装版本信息缺失或损坏。请用完整发行版修复安装，并保留 AutumnOS_Data。"
                : Code == "UPDATE_MINIMUM_SECURITY_VERSION"
                ? "当前安装版本低于已记录的安全下限。请使用符合安全下限的完整新版本，并保留 AutumnOS_Data。"
                : Code == "UPDATE_FAILED_BUILD_BLOCKED"
                ? "此版本此前未通过更新验证。请使用其他完整发行版本，并保留 AutumnOS_Data。"
                : Code == "UPDATE_SECURITY_LEDGER_BUSY"
                ? "安全更新记录正由另一操作使用。请稍后重试。"
                : Code == "UPDATE_SECURITY_LEGACY_RECOVERY_REQUIRED"
                ? "旧运行目录仍有未完成的更新事务。请先通过对应旧版本入口完成恢复；不要删除旧运行目录或 AutumnOS_Data。"
                : Code.StartsWith("UPDATE_SECURITY_", StringComparison.Ordinal) || Code == "UPDATE_STATE_INVALID" ||
                    Code == "UPDATE_METADATA_INVALID" || Code == "UPDATE_METADATA_ENCODING_OR_SIZE"
                ? "安装或安全更新记录无法确认，原文件已保留。请恢复完整记录；不要删除安全更新记录或 AutumnOS_Data。"
                : "AutumnOS 启动失败。";
            return Code + "\n" + detail + "\n阶段：" + Stage + "；目标：" + Target +
                (NativeErrorCode.HasValue ? "；Windows 错误码：" + NativeErrorCode.Value : "") +
                "\n未清除用户数据。\n" + (reportPath.Length == 0
                    ? "未能写入诊断文件。请保留此错误码和阶段。" : "诊断文件：" + reportPath);
        }

        private static void Plain(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current) ?? "")
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("STARTUP_REDIRECTED_PATH");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
    }
}
