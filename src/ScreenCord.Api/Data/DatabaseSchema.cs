using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ScreenCord.Api.Data;

public static class DatabaseSchema
{
    /// <summary>
    /// Cria o banco na primeira execução. Num banco criado por uma versão anterior, cria só as tabelas,
    /// colunas e índices que faltam (o projeto não usa migrações; isso cobre apenas mudanças aditivas no
    /// esquema, e coluna nova em tabela existente precisa aceitar nulo).
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
            using (var command = connection.CreateCommand())
            {
                command.CommandText = script;
                command.ExecuteNonQuery();
            }

            AddMissingColumns(db, connection);
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    private static void AddMissingColumns(AppDbContext db, DbConnection connection)
    {
        foreach (var entity in db.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is not { } table)
            {
                continue;
            }

            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT name FROM pragma_table_info('{table.Replace("'", "''")}')";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    existing.Add(reader.GetString(0));
                }
            }

            foreach (var property in entity.GetProperties())
            {
                if (property.GetColumnName(store) is not { } column || existing.Contains(column))
                {
                    continue;
                }

                if (!property.IsColumnNullable(store))
                {
                    throw new InvalidOperationException($"A coluna {table}.{column} é nova e não aceita nulo: não dá para criá-la num banco existente.");
                }

                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {property.GetColumnType(store)} NULL";
                alter.ExecuteNonQuery();
                existing.Add(column);
            }
        }
    }
}
