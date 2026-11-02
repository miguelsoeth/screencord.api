using ScreenCord.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace ScreenCord.Api.Realtime;

/// <summary>Avisa os dois lados de uma amizade (todas as conexões de cada um) de que a relação mudou.</summary>
public sealed class FriendNotifier(IHubContext<GroupsHub> hub)
{
    public Task FriendsChangedAsync(Guid a, Guid b, CancellationToken cancellationToken = default) =>
        hub.Clients.Users([a.ToString(), b.ToString()]).SendAsync(GroupsHubContract.FriendsChanged, cancellationToken);

    /// <summary>O nome ou a foto de alguém mudou: quem tem essa pessoa nas listas de amigos e solicitações recarrega.</summary>
    public Task ProfileChangedAsync(IReadOnlyList<Guid> users, CancellationToken cancellationToken = default) =>
        users.Count == 0
            ? Task.CompletedTask
            : hub.Clients.Users(users.Select(id => id.ToString()).ToList()).SendAsync(GroupsHubContract.FriendsChanged, cancellationToken);
}
