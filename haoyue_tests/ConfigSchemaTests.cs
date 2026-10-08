using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Tests;

/// <summary>
/// 统一配置源 Schema 管理器的契约测试：
/// 1) 默认值单源漂移防护（登记表 vs DefaultHaoyueConfig 实际序列化输出）；
/// 2) 规范化（类型回退/越界钳制/枚举回退）；
/// 3) schema 版本迁移与落盘。
/// </summary>
public sealed class ConfigSchemaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-tests", Guid.NewGuid().ToString("N"));

    public ConfigSchemaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void RegisteredDefaults_MatchSerializedDefaults_NoDrift()
    {
        // 默认值只允许存在一处语义：登记表（ConfigSchema）= 种子基线（DefaultHaoyueConfig）。
        var seeded = JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(
                DefaultHaoyueConfig.Build(), HaoyueJsonContext.Default.HaoyueConfig)) as JsonObject;
        Assert.NotNull(seeded);

        var drifts = new List<string>();
        foreach (var field in ConfigSchema.Fields)
        {
            if (field.Default is null) continue; // 用户数据型字段：种子基线不提供默认
            JsonNode? actual = seeded;
            foreach (var seg in field.Path.Split('.'))
            {
                actual = actual is JsonObject obj && obj.TryGetPropertyValue(seg, out var child) ? child : null;
                if (actual is null) break;
            }
            if (actual is null || !JsonNode.DeepEquals(actual, field.Default))
                drifts.Add($"{field.Path}: 登记默认={field.Default?.ToJsonString()} 实际={actual?.ToJsonString() ?? "<缺失>"}");
        }
        Assert.True(drifts.Count == 0, "默认值漂移：\n" + string.Join("\n", drifts));
    }

    [Fact]
    public void Normalize_ClampsOutOfRange_AndResetsInvalidEnum()
    {
        var root = JsonNode.Parse("""
        {
            "agent": { "maxSteps": 99999, "mode": "yolo", "maxToolOutputChars": 10, "bashTimeoutSeconds": "long" },
            "temperature": 3.5,
            "routing": { "loadBalance": "random", "retry": { "maxAttempts": 0 } }
        }
        """)!.AsObject();

        var (warnings, normalized) = ConfigSchema.Normalize(root);

        Assert.True(normalized);
        Assert.Equal(500, (int?)root["agent"]?["maxSteps"]);
        Assert.Equal("edit", (string?)root["agent"]?["mode"]);
        Assert.Equal(1_000, (int?)root["agent"]?["maxToolOutputChars"]);
        Assert.Equal(180, (int?)root["agent"]?["bashTimeoutSeconds"]); // 类型错误回退默认
        Assert.Equal(2.0, (double?)root["temperature"]);
        Assert.Equal("priority", (string?)root["routing"]?["loadBalance"]);
        Assert.Equal(1, (int?)root["routing"]?["retry"]?["maxAttempts"]);
        Assert.Equal(7, warnings.Count);
    }

    [Fact]
    public void Normalize_KeepsUnknownAndUserDataFields()
    {
        var root = JsonNode.Parse("""
        { "provider": "p1", "model": null, "customFutureField": { "x": 1 } }
        """)!.AsObject();

        var (warnings, normalized) = ConfigSchema.Normalize(root);

        Assert.False(normalized);
        Assert.Empty(warnings);
        Assert.Equal("p1", (string?)root["provider"]);
        Assert.Null((string?)root["model"]);
        Assert.NotNull(root["customFutureField"]); // 未知字段容忍，只忽略不删除
    }

    [Fact]
    public void Store_MigratesLegacyFileWithoutSchemaVersion_AndStampsCurrent()
    {
        var file = Path.Combine(_dir, "config.json");
        File.WriteAllText(file, """{ "agent": { "maxSteps": 25 } }""");

        var store = new ConfigStore(file, Path.Combine(_dir, "state.json"));

        Assert.Equal(ConfigSchema.CurrentVersion, store.Config.SchemaVersion);
        Assert.Equal(25, store.Config.Agent.MaxSteps); // 业务值保留
        Assert.Contains(store.ValidationWarnings, w => w.Contains("schemaVersion"));
        // 迁移已落盘
        var onDisk = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.Equal(ConfigSchema.CurrentVersion, (int?)onDisk["schemaVersion"]);
    }

    [Fact]
    public void Store_ClampsCorruptedValue_AndReportsWarning()
    {
        var file = Path.Combine(_dir, "config.json");
        File.WriteAllText(file, """{ "schemaVersion": 1, "agent": { "maxSteps": -5 } }""");

        var store = new ConfigStore(file, Path.Combine(_dir, "state.json"));

        Assert.Equal(1, store.Config.Agent.MaxSteps);
        Assert.Contains(store.ValidationWarnings, w => w.Contains("agent.maxSteps"));
    }

    [Fact]
    public void ExportSchemaJson_ContainsEveryRegisteredField()
    {
        var exported = JsonNode.Parse(ConfigSchema.ExportSchemaJson())!.AsObject();
        var fields = exported["fields"]!.AsArray();
        Assert.Equal(ConfigSchema.Fields.Count, fields.Count);
        Assert.Equal(ConfigSchema.CurrentVersion, (int?)exported["schemaVersion"]);
    }
}
