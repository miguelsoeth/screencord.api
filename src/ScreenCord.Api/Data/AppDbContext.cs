using ScreenCord.Api.Observability;
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

/// <summary>Sala de presença criada por um usuário; cresce conforme os integrantes adicionam amigos.</summary>
public sealed class Group
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public Guid CreatedById { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class GroupMember
{
    public Guid GroupId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Integrante que adicionou esta pessoa (o próprio criador, no caso dele).</summary>
    public Guid AddedById { get; set; }

    public DateTimeOffset AddedAtUtc { get; set; }
}

/// <summary>Configuração que o dono muda pelo painel /admin, sem novo deploy (ex.: os limites do compartilhamento).</summary>
public sealed class AppSetting
{
    public const int MaxValueLength = 2000;

    public required string Key { get; set; }

    /// <summary>JSON do valor.</summary>
    public required string Value { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Friendship> Friendships => Set<Friendship>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<Group> Groups => Set<Group>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    public DbSet<ErrorEvent> ErrorEvents => Set<ErrorEvent>();

    public DbSet<UserActivity> UserActivities => Set<UserActivity>();

    public DbSet<AppSetting> Settings => Set<AppSetting>();

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

        modelBuilder.Entity<Group>(group =>
        {
            group.Property(g => g.Name).HasMaxLength(ScreenCord.Contracts.GroupsHubContract.MaxNameLength);
        });

        modelBuilder.Entity<GroupMember>(member =>
        {
            member.HasKey(m => new { m.GroupId, m.UserId });
            member.HasIndex(m => m.UserId);
            member.HasOne<Group>().WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            member.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // Sem chave estrangeira para User: o erro continua no painel mesmo se a conta for apagada.
        modelBuilder.Entity<ErrorEvent>(error =>
        {
            error.HasIndex(e => e.OccurredAtUtc);
            error.HasIndex(e => e.Fingerprint);
            error.HasIndex(e => e.TraceId);
            error.Property(e => e.Source).HasMaxLength(8);
            error.Property(e => e.Level).HasMaxLength(16);
            error.Property(e => e.Category).HasMaxLength(200);
            error.Property(e => e.Message).HasMaxLength(ErrorEvent.MaxMessageLength + 1);
            error.Property(e => e.Exception).HasMaxLength(ErrorEvent.MaxExceptionLength + 1);
            error.Property(e => e.TraceId).HasMaxLength(32);
            error.Property(e => e.Fingerprint).HasMaxLength(16);
            error.Property(e => e.AppVersion).HasMaxLength(32);
            error.Property(e => e.Os).HasMaxLength(100);
            error.Property(e => e.Environment).HasMaxLength(32);
            error.Property(e => e.Request).HasMaxLength(300);
            error.Property(e => e.Device).HasMaxLength(ErrorEvent.MaxDetailsLength + 1);
            error.Property(e => e.Context).HasMaxLength(ErrorEvent.MaxDetailsLength + 1);
            error.Property(e => e.Breadcrumbs).HasMaxLength(ErrorEvent.MaxBreadcrumbsLength + 1);
        });

        modelBuilder.Entity<AppSetting>(setting =>
        {
            setting.HasKey(s => s.Key);
            setting.Property(s => s.Key).HasMaxLength(64);
            setting.Property(s => s.Value).HasMaxLength(AppSetting.MaxValueLength);
        });

        // Também sem chave estrangeira: apagar a conta não muda quantos estavam ativos naquele dia.
        modelBuilder.Entity<UserActivity>(activity =>
        {
            activity.HasKey(a => new { a.UserId, a.Day });
            activity.HasIndex(a => a.Day);
        });
    }

    // O SQLite não ordena nem compara DateTimeOffset nativamente; gravar como ticks UTC resolve.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<long>();
    }
}
