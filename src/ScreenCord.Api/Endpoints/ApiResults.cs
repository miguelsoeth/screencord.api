using ScreenCord.Api.Data;
using ScreenCord.Contracts;

namespace ScreenCord.Api.Endpoints;

public static class ApiResults
{
    public static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new ErrorResponse(code, message), statusCode: statusCode);

    public static UserDto ToDto(this User user, RelationshipStatus relationship) =>
        new(user.Id, user.DiscordId, user.Username, user.GlobalName, user.Discriminator, user.AvatarHash, relationship);
}
