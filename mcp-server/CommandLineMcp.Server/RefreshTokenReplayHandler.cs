using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace CommandLineMcp.Server;

/// <summary>Revokes the authorization if a single-use reference refresh token is presented again.</summary>
public sealed class RefreshTokenReplayHandler(
    IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager authorizations) : IOpenIddictServerHandler<OpenIddictServerEvents.ApplyTokenResponseContext>
{
    public async ValueTask HandleAsync(OpenIddictServerEvents.ApplyTokenResponseContext context)
    {
        if (context.Error is not (OpenIddictConstants.Errors.InvalidGrant or OpenIddictConstants.Errors.InvalidToken) ||
            context.Request?.IsRefreshTokenGrantType() != true ||
            string.IsNullOrEmpty(context.Request.RefreshToken)) return;

        var token = await tokens.FindByReferenceIdAsync(context.Request.RefreshToken);
        if (token is null) return;
        var status = await tokens.GetStatusAsync(token);
        if (status is not (OpenIddictConstants.Statuses.Redeemed or OpenIddictConstants.Statuses.Revoked)) return;

        var authorizationId = await tokens.GetAuthorizationIdAsync(token);
        if (string.IsNullOrEmpty(authorizationId)) return;

        var authorization = await authorizations.FindByIdAsync(authorizationId);
        if (authorization is null) return;
        if (await authorizations.TryRevokeAsync(authorization)) return;

        var descriptor = new OpenIddictAuthorizationDescriptor();
        await authorizations.PopulateAsync(descriptor, authorization);
        descriptor.Status = OpenIddictConstants.Statuses.Revoked;
        await authorizations.UpdateAsync(authorization, descriptor);
    }
}
