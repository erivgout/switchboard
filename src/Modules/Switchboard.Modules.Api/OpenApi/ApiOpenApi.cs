using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Switchboard.Core;
using Switchboard.Core.Api;
using Switchboard.Modules.Api.Http;

namespace Switchboard.Modules.Api.OpenApi;

/// <summary>OpenAPI 3.1 document at <c>/openapi/v1.json</c> covering every <c>/api/v1</c> route of every module (FR-INT-1).</summary>
internal static class ApiOpenApi
{
    public const string DocumentName = "v1";
    public const string Path = "/openapi/v1.json";
    public const string BearerScheme = "apiKey";
    public const string BasicScheme = "apiKeyBasic";

    public static IServiceCollection AddSwitchboardOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, o =>
        {
            o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            o.ShouldInclude = d => d.RelativePath?.StartsWith("api/v1/", StringComparison.OrdinalIgnoreCase) == true
                || string.Equals(d.RelativePath, "api/v1", StringComparison.OrdinalIgnoreCase);
            o.AddDocumentTransformer((document, context, ct) =>
            {
                var baseUrl = context.ApplicationServices.GetRequiredService<IOptions<SwitchboardOptions>>().Value.BaseUrl.TrimEnd('/');
                document.Info = new OpenApiInfo
                {
                    Title = "Switchboard API",
                    Version = "v1",
                    Description = """
                        The Switchboard REST API. Every call acts as the user who owns the API key, with that user's permissions, narrowed further by the key's scopes.

                        Authenticate with `Authorization: Bearer sb_…`, or with Basic auth using the key as the user name and any password.
                        Lists return `{ data, meta }`; follow `meta.nextCursor` with `?cursor=`. Send `If-Match` with a ticket's ETag on PATCH, and
                        `Idempotency-Key` on POST to retry safely for 24 hours. Errors are `application/problem+json`; validation errors list fields under `errors`.
                        """,
                    License = new OpenApiLicense { Name = "MIT" },
                };
                document.Servers = [new OpenApiServer { Url = baseUrl }];
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "sb_ followed by 43 letters and digits",
                    Description = "A personal API key from your profile page.",
                };
                document.Components.SecuritySchemes[BasicScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "basic",
                    Description = "The API key as the user name; the password is ignored.",
                };
                document.Security =
                [
                    new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BearerScheme, document)] = [] },
                    new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BasicScheme, document)] = [] },
                ];
                return Task.CompletedTask;
            });
            o.AddOperationTransformer((operation, context, ct) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                var notes = new List<string>();
                if (metadata.OfType<ApiScopeMetadata>().FirstOrDefault() is { } scope)
                {
                    notes.Add($"Scoped keys need `{scope.Scope}`.");
                }
                if (metadata.OfType<ApiAgentOnlyMetadata>().Any())
                {
                    notes.Add("Agents only.");
                }
                if (notes.Count > 0)
                {
                    operation.Description = string.Join(" ", new[] { operation.Description }.Where(d => !string.IsNullOrWhiteSpace(d)).Concat(notes));
                }
                operation.Responses ??= new OpenApiResponses();
                foreach (var (code, text) in new[] { ("401", "Missing, invalid, expired or revoked API key."), ("403", "Not allowed for this user or key."), ("429", "Rate limit reached; see Retry-After.") })
                {
                    operation.Responses.TryAdd(code, new OpenApiResponse { Description = text });
                }
                return Task.CompletedTask;
            });
        });
        return services;
    }

    public static void MapSwitchboardOpenApi(this IEndpointRouteBuilder endpoints) =>
        // Public like any API description: it lists routes, never data.
        endpoints.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous().ExcludeFromDescription();
}
