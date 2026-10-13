using LLama;

namespace Haoyue.Runtime.Providers;

/// <summary>
/// 模型版本兼容处理（P-版本）。三层：
/// 1) GGUF 格式版本门：version 2/3 已支持；更高版本（未来格式）在加载前早期拒绝，
///    给出可操作消息，而不是让 llama.cpp 抛出难懂的原生错误或直接崩溃；
/// 2) 后端构建识别：以 LLamaSharp 程序集版本作为后端 ABI 身份，与指纹里上次成功
///    的构建号比对——升级 runtime 后旧模型加载失败时，错误消息直接点明 ABI 变化；
/// 3) 元数据健康提示：架构缺失等不阻止加载的问题以 note 形式进入加载报告。
/// </summary>
public static class ModelCompatibility
{
    /// <summary>当前 llama.cpp/LLamaSharp 已支持的 GGUF 格式版本上限。</summary>
    public const int MaxSupportedGgufVersion = 3;

    /// <summary>
    /// 当前后端构建号（LLamaSharp 程序集版本）。仅用于指纹比对与日志，不做能力判断。
    /// </summary>
    public static string BackendBuild { get; } =
        typeof(LLamaWeights).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// 加载前的兼容性检查。返回 (Fatal, Warning)：Fatal 非 null 表示必须拒绝加载
    /// （消息面向用户、含处理建议）；Warning 非 null 表示可加载但值得注意。
    /// 绝不抛出——调用方决定如何处置。
    /// </summary>
    public static (string? Fatal, string? Warning) Check(GgufProbe.Header header)
    {
        if (header.Version is { } version && version > MaxSupportedGgufVersion)
        {
            return ($"GGUF 格式版本 {version} 不受支持（当前 runtime 最高支持 v{MaxSupportedGgufVersion}）。"
                    + "该模型由更新版的转换工具生成，请升级 Haoyue runtime，或用旧版 llama.cpp 工具重新转换为 GGUF v3。", null);
        }

        if (header.Version is null && header.Architecture is null && header.Name is null)
        {
            // header 完全不可读：文件可能损坏或根本不是 GGUF。不在此处拦截——
            // 权重加载会给出原生层错误，指纹会记录失败；这里只提示。
            return (null, "无法读取 GGUF header（文件损坏或格式不符），加载很可能失败。");
        }

        if (header.Architecture is null)
            return (null, "GGUF header 缺少 general.architecture，自适应装载档位将退化为提供商默认。");

        return (null, null);
    }

    /// <summary>
    /// 加载失败时的版本/ABI 诊断补充：指纹显示该模型曾用不同后端构建成功加载时，
    /// 生成一段点明原因的附加消息（升级 runtime / 换后端包后的 ABI 漂移）。
    /// </summary>
    public static string? DiagnoseFailure(LocalModelFingerprints fingerprints, string path)
    {
        var previous = fingerprints.BackendBuildAtLastSuccess(path);
        if (previous is null || string.Equals(previous, BackendBuild, StringComparison.Ordinal))
            return null;
        return $"该模型此前在后端构建 {previous} 上加载成功，当前为 {BackendBuild}——"
            + "runtime/后端包升级可能导致 ABI 不兼容，请换回原版本或重新转换模型文件。";
    }
}
