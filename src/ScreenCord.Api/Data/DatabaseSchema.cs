using Microsoft.EntityFrameworkCore;

namespace ScreenCord.Api.Data;

public static class DatabaseSchema
{
    /// <summary>
    /// Cria o banco na primeira execução. Num banco criado por uma versão anterior, cria só as tabelas e
    /// índices que faltam (o projeto não usa migrações; isso cobre apenas mudanças aditivas no esquema).
    /// </summary>
    public static void Ensure(AppDbContext db)
    {
        if (db.Database.EnsureCreated())
        {
            return;
        }

        var script = db.Database.GenerateCreateScript()
            .Replace("CREATE TABLE \"", "CREATE TABLE IF NOT EXISTS \"", StringComparison.Ordinal)
            .Replace("CREATE UNIQUE INDEX \"", "CREATE UNIQUE INDEX IF NOT EXISTS \"", StringComparison.Ordinal)
            .Replace("CREATE INDEX \"", "CREATE INDEX IF NOT EXISTS \"", StringComparison.Ordinal);

        var connection = db.Database.GetDbConnection();
        db.Database.OpenConnection();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = script;
            command.ExecuteNonQuery();
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }
}
