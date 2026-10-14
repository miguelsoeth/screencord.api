using Microsoft.EntityFrameworkCore;

namespace ScreenCord.Api.Data;

public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>Snowflake do Discord; identifica a conta entre logins.</summary>
    public required string DiscordId { get; set; }

    public required string Username { get; set; }

    public string? GlobalName { get; set; }

    public string? Discriminator { get; set; }

    public string? AvatarHash { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset LastLoginAtUtc { get; set; }
}

public enum FriendshipStatus
{
    Pending,
    Accepted,
}

/// <summary>Uma linha por par de usuários, pendente ou aceita.</summary>
public sealed class Friendship
{
    public Guid Id { get; set; }

    public Guid RequesterId { get; set; }

    public Guid AddresseeId { get; set; }

    // Par ordenado (menor, maior): o índice único impede duas linhas para o mesmo par,
    // mesmo quando os dois usuários enviam solicitações ao mesmo tempo.
    public Guid UserLowId { get; set; }

    public Guid UserHighId { get; set; }

    public FriendshipStatus Status { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? AcceptedAtUtc { get; set; }

    public static (Guid Low, Guid High) OrderPair(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);
}

public sealed class Session
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>SHA-256 do token; o token em si nunca é armazenado.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Friendship> Friendships => Set<Friendship>();

    public DbSet<Session> Sessions => Set<Session>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.HasIndex(u => u.DiscordId).IsUnique();
            user.HasIndex(u => u.Username);
            user.Property(u => u.DiscordId).HasMaxLength(20);
            user.Property(u => u.Username).HasMaxLength(64);
            user.Property(u => u.GlobalName).HasMaxLength(64);
            user.Property(u => u.Discriminator).HasMaxLength(4);
            user.Property(u => u.AvatarHash).HasMaxLength(66);
        });

        modelBuilder.Entity<Friendship>(friendship =>
        {
            friendship.HasIndex(f => new { f.UserLowId, f.UserHighId }).IsUnique();
            friendship.HasIndex(f => f.RequesterId);
            friendship.HasIndex(f => f.AddresseeId);
            friendship.HasOne<User>().WithMany().HasForeignKey(f => f.RequesterId).OnDelete(DeleteBehavior.Cascade);
            friendship.HasOne<User>().WithMany().HasForeignKey(f => f.AddresseeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Session>(session =>
        {
            session.HasIndex(s => s.TokenHash).IsUnique();
            session.Property(s => s.TokenHash).HasMaxLength(64);
            session.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    // O SQLite não ordena nem compara DateTimeOffset nativamente; gravar como ticks UTC resolve.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<long>();
    }
}
