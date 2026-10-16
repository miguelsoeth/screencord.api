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

/// <summary>Pro pago da conta (uma linha por conta que já comprou). Sem linha, ou vencido, é o plano gratuito.</summary>
public sealed class Subscription
{
    public Guid UserId { get; set; }

    public DateTimeOffset ProUntilUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public enum PaymentStatus
{
    /// <summary>Checkout criado, aguardando o pagamento.</summary>
    Pending,
    Paid,
    /// <summary>Estornado ou contestado: os dias comprados foram retirados.</summary>
    Refunded,
}

/// <summary>Compra de um período de Pro. O id vai como <c>externalId</c> no checkout do AbacatePay.</summary>
public sealed class Payment
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public required string OfferId { get; set; }

    public int Days { get; set; }

    public int AmountCents { get; set; }

    /// <summary>Id do checkout no AbacatePay (<c>bill_...</c>), preenchido quando ele é criado.</summary>
    public string? CheckoutId { get; set; }

    public PaymentStatus Status { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? PaidAtUtc { get; set; }

    public DateTimeOffset? RefundedAtUtc { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Friendship> Friendships => Set<Friendship>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<Group> Groups => Set<Group>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<Payment> Payments => Set<Payment>();

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

        modelBuilder.Entity<Subscription>(subscription =>
        {
            subscription.HasKey(s => s.UserId);
            subscription.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Payment>(payment =>
        {
            payment.HasIndex(p => p.UserId);
            payment.HasIndex(p => p.CheckoutId).IsUnique();
            payment.Property(p => p.OfferId).HasMaxLength(40);
            payment.Property(p => p.CheckoutId).HasMaxLength(64);
            payment.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    // O SQLite não ordena nem compara DateTimeOffset nativamente; gravar como ticks UTC resolve.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<long>();
    }
}
