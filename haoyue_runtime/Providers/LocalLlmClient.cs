using System.Runtime.CompilerServices;
using System.Text;
using Haoyue.Runtime.Configuration;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Haoyue.Runtime.Providers;

/// <summary>
/// Runs GGUF models in-process with llama.cpp (LLamaSharp). There is no server and no
/// network: the model file is loaded from the provider's local models directory on demand
/// and generations are serialized through the shared weight cache. Capability honesty:
/// local models currently expose streaming only (tool calling stays off).
/// </summary>
public sealed class LocalLlmClient(LocalModelCache cache) : ILlmClient
{
    public string Kind => "local";

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var path = LocalModels.ResolveModelPath(request.Provider, request.Model);
        if (!File.Exists(path))
            throw new LlmException(
                $"Local model file not found: {path}. Put the GGUF file into the provider's models directory or fix the path.",
                retryable: false);

        using var lease = await cache.AcquireAsync(path, ct).ConfigureAwait(false);
        var weights = lease.Weights;

        var (prompt, startsInThinking) = BuildPrompt(weights, request);
        var executor = new StatelessExecutor(
            weights, BuildContextParams(path, request.Model, weights), NullLogger.Instance);
        try
        {
            var parser = new ThinkTagParser(startsInThinking);

            await using var stream = executor
                .InferAsync(prompt, BuildInferenceParams(request), ct)
                .GetAsyncEnumerator(ct);
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await stream.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new LlmException(
                        $"Local inference with {request.Model.Id} failed: {ex.Message}",
                        retryable: false,
                        inner: ex);
                }
                if (!hasNext) break;

                foreach (var piece in parser.Process(stream.Current))
                {
                    if (piece.IsThinking) yield return new LlmThinkingDelta(piece.Text);
                    else yield return new LlmTextDelta(piece.Text);
                }
            }

            foreach (var piece in parser.Flush())
            {
                if (piece.IsThinking) yield return new LlmThinkingDelta(piece.Text);
                else yield return new LlmTextDelta(piece.Text);
            }

            yield return new LlmCompleted(new LlmCompletion
            {
                Text = parser.Answer,
                Thinking = parser.Thinking,
                Usage = new TokenUsage(
                    CountTokens(weights, prompt),
                    CountTokens(weights, parser.Answer) + CountTokens(weights, parser.Thinking)),
                FinishReason = "stop",
            });
        }
        finally
        {
            executor.Context.Dispose();
        }
    }

    /// <summary>Renders the conversation with the chat template embedded in the GGUF file.</summary>
    private static (string Prompt, bool StartsInThinking) BuildPrompt(LLamaWeights weights, LlmRequest request)
    {
        LLamaTemplate template;
        try
        {
            template = new LLamaTemplate(weights, strict: false);
        }
        catch (Exception ex)
        {
            throw new LlmException(
                $"The GGUF file of {request.Model.Id} has no usable chat template: {ex.Message}",
                retryable: false,
                inner: ex);
        }

        if (!string.IsNullOrWhiteSpace(request.System))
            template.Add("system", request.System!);

        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case ChatRole.User:
                    template.Add("user", message.Text);
                    break;
                case ChatRole.System:
                    template.Add("system", message.Text);
                    break;
                case ChatRole.Assistant:
                    if (message.Text.Length > 0) template.Add("assistant", message.Text);
                    break;
                case ChatRole.Tool:
                    // Tool calling stays off for local models, but a session started on
                    // another provider can still carry tool results; replay them as user
                    // turns so the information is not silently dropped.
                    template.Add("user", $"[{message.ToolName ?? "tool"} result]\n{message.Text}");
                    break;
            }
        }

        template.AddAssistant = true;
        var prompt = Encoding.UTF8.GetString(template.Apply());

        // DeepSeek-R1 family models reason before answering: either the template injects
        // the opening tag into the generation prompt (original R1), or the template itself
        // references the ASCII "</think>" delimiter the model emits on its own (R1-0528).
        var trimmed = prompt.TrimEnd('\n', '\r', ' ');
        var startsInThinking = trimmed.EndsWith(" thinking", StringComparison.Ordinal)
            || (TryGetChatTemplate(weights)?.Contains("</think>", StringComparison.Ordinal) ?? false);
        return (prompt, startsInThinking);
    }

    private static string? TryGetChatTemplate(LLamaWeights weights)
    {
        try
        {
            foreach (var (key, value) in weights.Metadata)
                if (key == "tokenizer.chat_template")
                    return value?.ToString();
        }
        catch (Exception)
        {
            // Metadata layout is not critical here; fall back to prompt inspection.
        }
        return null;
    }

    private static ModelParams BuildContextParams(string path, ModelConfig model, LLamaWeights weights)
    {
        // Registering a model with a context window larger than it was trained for would
        // silently extrapolate positions, so the effective context is clamped to the model.
        // It is also clamped to a practical CPU ceiling: the KV cache of a 128k window alone
        // costs multiple gigabytes of RAM, and an entry that was written by hand inherits
        // the remote default (128k) unless the registration set a real value.
        var trained = weights.ContextSize > 0 ? weights.ContextSize : 4096;
        var configured = model.ContextWindow > 0 ? model.ContextWindow : trained;
        return new ModelParams(path)
        {
            ContextSize = (uint)Math.Max(512, Math.Min(configured, Math.Min(trained, LocalModelProbe.DefaultContextWindow))),
            GpuLayerCount = 0,
            UseMemorymap = true,
        };
    }

    private static InferenceParams BuildInferenceParams(LlmRequest request)
    {
        var maxTokens = request.MaxTokens is { } requested && requested > 0
            ? requested
            : Math.Clamp(request.Model.MaxOutput, 256, 32_768);
        return new InferenceParams
        {
            MaxTokens = maxTokens,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = (float)(request.Temperature ?? 0.6),
            },
        };
    }

    private static int CountTokens(LLamaWeights weights, string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        try { return weights.Tokenize(text, false, false, Encoding.UTF8).Count(); }
        catch { return 0; }
    }
}

/// <summary>
/// Incrementally splits model output into reasoning / answer pieces. Handles both the
/// ASCII "&lt;/think&gt;" delimiter of the DeepSeek-R1-0528 family and the special-token
/// delimiters of the original R1, with tags possibly split across stream chunks.
/// </summary>
internal sealed class ThinkTagParser(bool startsInThinking)
{
    private const string OpenTag = " thinking";
    private static readonly string[] CloseTags = ["</think>", "<｜end▁of▁thinking｜>"];

    private readonly StringBuilder _buffer = new();
    private readonly StringBuilder _thinking = new();
    private readonly StringBuilder _answer = new();
    private bool _inThinking = startsInThinking;
    private bool _emittedText;

    /// <summary>Everything classified as reasoning so far, which the answer may still grow into.</summary>
    public string Thinking => _thinking.ToString();

    /// <summary>Everything classified as the answer so far.</summary>
    public string Answer => _answer.ToString();

    public List<(bool IsThinking, string Text)> Process(string chunk)
    {
        if (!string.IsNullOrEmpty(chunk)) _buffer.Append(chunk);
        var pieces = new List<(bool IsThinking, string Text)>();
        while (true)
        {
            var buffer = _buffer.ToString();
            if (_inThinking)
            {
                if (FindCloseTag(buffer) is var (close, closeTag) && close >= 0)
                {
                    Add(pieces, true, buffer[..close]);
                    _buffer.Remove(0, close + closeTag.Length);
                    _inThinking = false;
                    continue;
                }
                var hold = LongestTagPrefixAtEnd(buffer, CloseTags);
                if (buffer.Length > hold) Add(pieces, true, buffer[..^hold]);
                KeepTail(hold);
                break;
            }

            var open = buffer.IndexOf(OpenTag, StringComparison.Ordinal);
            if (open >= 0)
            {
                Add(pieces, false, buffer[..open]);
                _buffer.Remove(0, open + OpenTag.Length);
                _inThinking = true;
                continue;
            }
            var keep = LongestTagPrefixAtEnd(buffer, [OpenTag]);
            if (buffer.Length > keep) Add(pieces, false, buffer[..(buffer.Length - keep)]);
            KeepTail(keep);
            break;
        }
        return pieces;
    }

    /// <summary>Releases whatever the stream ended while holding back.</summary>
    public List<(bool IsThinking, string Text)> Flush()
    {
        var pieces = new List<(bool IsThinking, string Text)>();
        if (_buffer.Length > 0)
        {
            Add(pieces, _inThinking, _buffer.ToString());
            _buffer.Clear();
        }
        return pieces;
    }

    private static (int Index, string Tag) FindCloseTag(string text)
    {
        var best = -1;
        var bestTag = "";
        foreach (var tag in CloseTags)
        {
            var index = text.IndexOf(tag, StringComparison.Ordinal);
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
                bestTag = tag;
            }
        }
        return (best, bestTag);
    }

    private void Add(List<(bool IsThinking, string Text)> pieces, bool thinking, string text)
    {
        if (text.Length == 0) return;
        if (!thinking && !_emittedText)
        {
            // The answer that follows the thinking delimiter typically starts with blank lines.
            text = text.TrimStart('\r', '\n');
            if (text.Length == 0) return;
        }
        if (!thinking) _emittedText = true;
        (thinking ? _thinking : _answer).Append(text);
        pieces.Add((thinking, text));
    }

    private void KeepTail(int length)
    {
        if (length == 0) _buffer.Clear();
        else if (_buffer.Length > length) _buffer.Remove(0, _buffer.Length - length);
    }

    private static int LongestTagPrefixAtEnd(string text, string[] tags)
    {
        var longest = 0;
        foreach (var tag in tags)
        {
            var max = Math.Min(tag.Length - 1, text.Length);
            for (var length = max; length > longest; length--)
            {
                if (string.CompareOrdinal(text, text.Length - length, tag, 0, length) == 0)
                {
                    longest = length;
                    break;
                }
            }
        }
        return longest;
    }
}
