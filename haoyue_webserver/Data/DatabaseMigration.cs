using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace haoyue_webserver.Data;

public static class DatabaseMigration
{
    /// <summary>
    /// 生产库走迁移；早期版本用 EnsureCreated 建的库没有 __EFMigrationsHistory，
    /// 直接 Migrate() 会重放 InitialCreate 并撞「table already exists」。
    /// 这里把「表已存在但无历史」的库基线到首个迁移，后续迁移照常叠加。
    /// </summary>
    public static void Apply(AppDbContext db)
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (creator.Exists() && creator.HasTables() && !db.Database.GetAppliedMigrations().Any())
        {
            var history = db.GetService<IHistoryRepository>();
            var initialMigration = db.Database.GetMigrations().First();
            db.Database.ExecuteSqlRaw(history.GetCreateIfNotExistsScript());
            db.Database.ExecuteSqlRaw(
                history.GetInsertScript(new HistoryRow(initialMigration, ProductInfo.GetVersion())));
        }

        db.Database.Migrate();
    }
}
