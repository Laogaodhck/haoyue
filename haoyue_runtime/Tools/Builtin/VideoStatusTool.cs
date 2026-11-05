using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// 视频任务查询（与 <see cref="VideoGenTool"/> 配套）：按任务 ID 查询
/// contents/generations/tasks 状态；succeeded 时可选择下载 MP4 到工作区。
/// 用于 video_generate 单次轮询超时后取回仍在服务端运行的视频。
/// </summary>
public sealed class VideoStatusTool(
    IPromptProvider prompts,
    IConfigStore configStore,
    ILlmHttpFactory http) : BuiltinTool(prompts)
{
    public override string Name => "video_status";
    public override string StatusLabel => "Querying video task";
    public override bool Mutating => false;
    public ToolRiskTier RiskTier => ToolRiskTier.Network;
    public override bool RequiresNetwork => true;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("task_id", ToolSchema.String("The video task id returned by video_generate."), true),
        ("download", ToolSchema.Boolean("When true and the task succeeded, download the MP4 into the workspace. Default true."), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var taskId = GetString(arguments, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
            return ToolResult.Fail("task_id is required.");

        var config = configStore.Config;
        var endpoint = VideoGenTool.ResolveVideoEndpoint(config);
        if (endpoint is not (ProviderConfig provider, _))
            return ToolResult.Fail("video_status is not available: no video model is configured (agent.videoGen.model).");

        try
        {
            var client = http.GetClient(provider);
            var (status, videoUrl, error) = await VideoGenTool.QueryTaskAsync(client, provider, taskId, ct).ConfigureAwait(false);

            if (!status.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
            {
                var detail = status.Equals("failed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(error)
                    ? $": {error}"
                    : string.Empty;
                return ToolResult.Ok(
                    $"Video task {taskId} status: {status}{detail}. It is still running server-side — query again later.",
                    $"Video task {taskId}: {status}");
            }

            if (string.IsNullOrEmpty(videoUrl))
                return ToolResult.Ok($"Video task {taskId} succeeded but carried no video_url.", "Video task succeeded");

            var shouldDownload = arguments["download"] is null || GetBool(arguments, "download");
            if (!shouldDownload)
                return ToolResult.Ok($"Video task {taskId} succeeded. Video URL (expires ~24h): {videoUrl}", "Video task succeeded");

            if (context.Workspace is null)
                return ToolResult.Fail("video_status requires a workspace to write the artifact into.");

            var outputsDir = PathGuard.ArtifactDirectory(context.Workspace, "videos", out var artifactError);
            if (outputsDir is null) return ToolResult.Fail(artifactError!);
            Directory.CreateDirectory(outputsDir);
            var file = Path.Combine(outputsDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.mp4");
            var bytes = await client.GetByteArrayAsync(videoUrl, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
            return ToolResult.Ok(
                $"Video task {taskId} succeeded and the MP4 was saved: {file} ({bytes.Length:N0} bytes).",
                $"Video downloaded: {Path.GetFileName(file)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"video_status failed: {ex.Message}");
        }
    }
}
