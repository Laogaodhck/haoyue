using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using haoyue_webserver.Data;
using haoyue_webserver.Models;
using Xunit;

namespace haoyue_tests;

/// <summary>
/// Webserver 的迁移与关键 API 集成测试：真实 Program 启动（cookie 认证/限流/路由全链路），
/// 只把 DbContext 换成内存 SQLite，避免触碰仓库里的 App_Data/haoyue.db。
/// </summary>
public sealed class WebServerApiTests
{
    private sealed class WebAppFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppDbContext>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }
    }

    private static AppDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

    [Fact]
    public void Database_Migrate_CreatesSchemaAndMigrationHistory()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = CreateDb(connection);

        DatabaseMigration.Apply(db);

        Assert.Contains(db.Database.GetAppliedMigrations(), id => id.EndsWith("_InitialCreate", StringComparison.Ordinal));
        db.Users.Add(new User { Username = "admin@haoyue.org", PasswordHash = "hash", CreatedAt = 0 });
        db.SaveChanges();
        Assert.Equal(1, db.Users.Count());
    }

    [Fact]
    public void Database_LegacyEnsureCreated_BaselinesWithoutLosingRows()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using (var legacy = CreateDb(connection))
        {
            legacy.Database.EnsureCreated();
            legacy.Users.Add(new User { Username = "legacy@haoyue.org", PasswordHash = "hash", CreatedAt = 0 });
            legacy.SaveChanges();
        }

        using (var db = CreateDb(connection))
        {
            DatabaseMigration.Apply(db);

            Assert.Contains(db.Database.GetAppliedMigrations(), id => id.EndsWith("_InitialCreate", StringComparison.Ordinal));
            Assert.Equal(1, db.Users.Count());
            Assert.Equal("legacy@haoyue.org", db.Users.Single().Username);
        }

        // 再跑一次必须幂等（启动重试/多实例场景）。
        using (var db = CreateDb(connection))
        {
            DatabaseMigration.Apply(db);
            Assert.Equal(1, db.Users.Count());
        }
    }

    [Fact]
    public async Task Api_SetupStatus_Initialize_Login_AndRegistrationGate()
    {
        using var factory = new WebAppFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var status = await client.GetFromJsonAsync<JsonElement>("/api/setup/status");
        Assert.False(status.GetProperty("isInitialized").GetBoolean());

        var init = await client.PostAsJsonAsync("/api/setup/initialize", new
        {
            adminEmail = "admin@haoyue.org",
            adminPassword = "SecurePassword123",
            siteName = "Test Hub",
            allowRegistration = false,
            seedSampleSkills = false
        });
        var initJson = await init.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(initJson.GetProperty("success").GetBoolean());

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.True(me.GetProperty("authenticated").GetBoolean());
        Assert.True(me.GetProperty("isSuperAdmin").GetBoolean());
        Assert.Equal("admin@haoyue.org", me.GetProperty("username").GetString());

        var register = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "user@example.com",
            password = "UserPassword123"
        });
        var registerJson = await register.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(registerJson.GetProperty("success").GetBoolean());
        Assert.Contains("未开放注册", registerJson.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Api_SuperAdminEndpoint_WithoutCookie_Returns401()
    {
        using var factory = new WebAppFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/api/setup/test-db");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
