namespace Haoyue.Tests;

/// <summary>
/// Process-wide environment variables are shared by every test running in parallel:
/// classes that mutate them (connector root, doc toolkit path, local model directory,
/// secret resolution) join this collection so their setup/teardown cannot race each
/// other or leak a temporary value into an unrelated test.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "process-env";
}
