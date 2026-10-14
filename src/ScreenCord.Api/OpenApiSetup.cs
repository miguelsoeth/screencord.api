using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace ScreenCord.Api;

/// <summary>Documento OpenAPI (<c>/openapi/v1.json</c>) e Swagger UI (<c>/swagger</c>), expostos só em Development.</summary>
public static class OpenApiSetup
{
    private const string BearerScheme = "Bearer";

    public static IServiceCollection AddScreenCordOpenApi(this IServiceCollection services)
    {
        return services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "ScreenCord API";
                document.Info.Version = "v1";
                document.Info.Description =
                    "API central do ScreenCord: usuários que entraram com o Discord e as amizades entre eles.\n\n" +
                    "Para testar as rotas protegidas: chame `POST /auth/discord` com um access token do Discord " +
                    "emitido para este aplicativo, copie o `token` da resposta e use o botão **Authorize**.";

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    Description = "Token de sessão retornado por POST /auth/discord (não é o token do Discord).",
                };
                return Task.CompletedTask;
            });

            // Só as rotas que exigem login recebem o cadeado no Swagger UI.
            options.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Any())
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = [],
                    });
                }

                return Task.CompletedTask;
            });
        });
    }

    public static void MapScreenCordOpenApi(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        app.MapOpenApi();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/openapi/v1.json", "ScreenCord API v1");
            options.RoutePrefix = "swagger";
            options.DocumentTitle = "ScreenCord API";
        });
    }
}
