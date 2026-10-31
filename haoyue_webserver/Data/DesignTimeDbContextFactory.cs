using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace haoyue_webserver.Data;

/// <summary>
/// 设计时工厂：<c>dotnet ef migrations</c> 不需要启动站点（否则会执行 Program 的迁移与
/// 真实 App_Data 数据库）。迁移只依赖模型，用临时 SQLite 即可。
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new AppDbContext(options);
    }
}
