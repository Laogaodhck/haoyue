using Haoyue.Runtime.Data;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// MEMORY.md 是读-改-写：memory_save 与后台固化并发追加时，串行化必须保证
/// 每个成功返回 Written 的条目都真的落盘（旧实现会静默覆盖先写者）。
/// </summary>
public class MemoryFileWriterTests
{
    [Fact]
    public async Task ConcurrentAppends_KeepEveryEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"haoyue-memory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "MEMORY.md");
        try
        {
            var appends = Enumerable.Range(0, 25)
                .Select(i => MemoryFileWriter.AppendAsync(file, $"事实-{i:D2}", "并发", CancellationToken.None))
                .ToArray();
            var results = await Task.WhenAll(appends);

            Assert.All(results, result => Assert.Equal(MemoryFileWriter.Status.Written, result.Result));
            var text = await File.ReadAllTextAsync(file);
            for (var i = 0; i < 25; i++)
            {
                Assert.Contains($"事实-{i:D2}", text, StringComparison.Ordinal);
            }

            Assert.Equal(25, text.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DuplicateContent_IsNotAppendedTwice()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"haoyue-memory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "MEMORY.md");
        try
        {
            var first = await MemoryFileWriter.AppendAsync(file, "唯一事实", null, CancellationToken.None);
            var second = await MemoryFileWriter.AppendAsync(file, "唯一事实", null, CancellationToken.None);

            Assert.Equal(MemoryFileWriter.Status.Written, first.Result);
            Assert.Equal(MemoryFileWriter.Status.Duplicate, second.Result);
            Assert.Single((await File.ReadAllTextAsync(file)).Split('\n'), line => line.StartsWith("- ", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
