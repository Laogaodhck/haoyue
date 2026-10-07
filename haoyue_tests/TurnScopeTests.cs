using Haoyue.Runtime.Agents;

namespace Haoyue.Tests;

public sealed class TurnScopeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-tests", Guid.NewGuid().ToString("N"));

    public TurnScopeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void RecordStep_OrdersSequentially_AndTracksCompensability()
    {
        var scope = new TurnExecutionScope("s1", _dir);
        scope.RecordStep("tool", "read_file", "a.txt", compensable: false, success: true);
        scope.RecordStep("tool", "write_file", "a.txt", compensable: true, success: true);

        Assert.Equal(2, scope.Steps.Count);
        Assert.Equal(1, scope.Steps[0].Ordinal);
        Assert.Equal(2, scope.Steps[1].Ordinal);
        Assert.True(scope.Steps[1].Compensable);
    }

    [Fact]
    public void TryRegisterFileChange_BudgetExhaustion_DegradesToUncompensable()
    {
        var scope = new TurnExecutionScope("s1", _dir);

        // Fill the byte budget: 20M + 1M chars exceeds the 20MiB cap on the second entry.
        Assert.True(scope.TryRegisterFileChange(Path.Combine(_dir, "big.txt"), new string('a', 20_000_000), "big"));
        Assert.False(scope.TryRegisterFileChange(Path.Combine(_dir, "small.txt"), new string('b', 1_000_000), "small"));
        Assert.True(scope.CompensationTruncated);
        Assert.Single(scope.Changes);

        // No prior content at all → the 50-entry cap path stays reachable.
        var capped = new TurnExecutionScope("s2", _dir);
        for (var i = 0; i < TurnExecutionScope.MaxCompensations; i++)
            Assert.True(capped.TryRegisterFileChange(Path.Combine(_dir, $"f{i}.txt"), null, $"f{i}"));
        Assert.False(capped.TryRegisterFileChange(Path.Combine(_dir, "overflow.txt"), null, "overflow"));
        Assert.Equal(TurnExecutionScope.MaxCompensations, capped.Changes.Count);
    }

    [Fact]
    public async Task ApplyAsync_RestoresEditsAndDeletesCreations_InLifoOrder()
    {
        var original = "original content";
        var edited = Path.Combine(_dir, "edited.txt");
        var created = Path.Combine(_dir, "created.txt");
        await File.WriteAllTextAsync(edited, original);
        await File.WriteAllTextAsync(created, "fresh");

        var scope = new TurnExecutionScope("s1", _dir);
        Assert.True(scope.TryRegisterFileChange(edited, original, "编辑 edited.txt"));
        Assert.True(scope.TryRegisterFileChange(created, null, "创建 created.txt"));

        var ledger = scope.BuildLedger(turnFailed: false);
        Assert.NotNull(ledger);
        Assert.Equal(2, ledger!.Changes.Count);

        var (restored, failed) = await ledger.ApplyAsync();
        Assert.Empty(failed);
        Assert.Equal(2, restored.Count);
        Assert.Equal("original content", await File.ReadAllTextAsync(edited));
        Assert.False(File.Exists(created)); // creation undone by deletion
    }

    [Fact]
    public async Task ApplyAsync_ContinuesAfterSingleFailure()
    {
        var readOnlyPath = Path.Combine(_dir, "readonly.txt");
        await File.WriteAllTextAsync(readOnlyPath, "current");
        File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly); // writes fail → compensation fails
        try
        {
            var normalPath = Path.Combine(_dir, "normal.txt");
            await File.WriteAllTextAsync(normalPath, "new");

            var ledger = new TurnUndoLedger("s1", _dir, DateTimeOffset.UtcNow, true,
            [
                // LIFO: normal.txt reverts first, the read-only write fails second —
                // proving a single failure never aborts the remaining compensations.
                new UndoableFileChange(readOnlyPath, "stale", "编辑 readonly.txt"),
                new UndoableFileChange(normalPath, "old", "编辑 normal.txt"),
            ]);

            var (restored, failed) = await ledger.ApplyAsync();
            Assert.Single(failed);
            Assert.Single(restored);
            Assert.Equal("old", await File.ReadAllTextAsync(normalPath));
        }
        finally
        {
            File.SetAttributes(readOnlyPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void UndoRegistry_TakeIsOneShot_PeekDoesNotConsume()
    {
        var registry = new TurnUndoRegistry();
        var ledger = new TurnUndoLedger("s1", _dir, DateTimeOffset.UtcNow, false,
            [new UndoableFileChange(Path.Combine(_dir, "a.txt"), "x", "a")]);
        registry.Deposit(ledger);

        Assert.Same(ledger, registry.Peek("s1"));
        Assert.Same(ledger, registry.Take("s1"));
        Assert.Null(registry.Take("s1"));
        Assert.Null(registry.Peek("s1"));

        // Latest deposit per session wins.
        var first = new TurnUndoLedger("s2", _dir, DateTimeOffset.UtcNow, false, []);
        var second = new TurnUndoLedger("s2", _dir, DateTimeOffset.UtcNow, false, []);
        registry.Deposit(first);
        registry.Deposit(second);
        Assert.Same(second, registry.Take("s2"));
    }
}
