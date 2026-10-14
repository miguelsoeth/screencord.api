using ScreenCord.Api.Data;
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
}
