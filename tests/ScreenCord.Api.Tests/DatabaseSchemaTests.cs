using ScreenCord.Api.Data;
using ScreenCord.Api.Observability;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ScreenCord.Api.Tests;

public class DatabaseSchemaTests
{
    [Fact]
    public void BancoDeVersaoAnterior_GanhaAsTabelasNovasSemPerderDados()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

        // Simula um banco criado antes dos grupos existirem, já com um usuário.
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("DROP TABLE GroupMembers; DROP TABLE Groups;");
        db.Users.Add(new User { Id = Guid.NewGuid(), DiscordId = "1", Username = "ana" });
        db.SaveChanges();

        DatabaseSchema.Ensure(db);
        DatabaseSchema.Ensure(db);

        Assert.Equal(1, db.Users.Count());
        Assert.Equal(0, db.Groups.Count());
        Assert.Equal(0, db.GroupMembers.Count());
    }

    [Fact]
    public void BancoDeVersaoAnterior_GanhaAsColunasNovasSemPerderDados()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

        // Simula um banco de antes do estado do app e do log irem junto com o erro.
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("ALTER TABLE ErrorEvents DROP COLUMN Device; ALTER TABLE ErrorEvents DROP COLUMN Context; ALTER TABLE ErrorEvents DROP COLUMN Breadcrumbs;");
        db.Database.ExecuteSqlRaw(
            "INSERT INTO ErrorEvents (Id, Source, OccurredAtUtc, ReceivedAtUtc, Level, Category, Message, Fingerprint) VALUES ({0}, 'app', 0, 0, 'Error', 'X', 'antigo', 'abc')",
            Guid.NewGuid());

        DatabaseSchema.Ensure(db);
        DatabaseSchema.Ensure(db);

        db.ErrorEvents.Add(new ErrorEvent
        {
            Id = Guid.NewGuid(), Source = ErrorEvent.FromApp, Level = "Error", Category = "X", Message = "novo", Fingerprint = "def",
            Device = "{}", Context = "{}", Breadcrumbs = "linha",
        });
        db.SaveChanges();

        Assert.Equal(["antigo", "novo"], db.ErrorEvents.OrderBy(e => e.Message).Select(e => e.Message).ToArray());
        Assert.Equal("linha", db.ErrorEvents.Single(e => e.Message == "novo").Breadcrumbs);
    }
}
