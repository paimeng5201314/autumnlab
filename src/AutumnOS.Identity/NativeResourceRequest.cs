namespace AutumnOS.Identity;

/// <summary>Internal independent-client sample option; never a host SDK token-export capability.</summary>
internal sealed record NativeResourceRequest(Uri Resource, string RequiredScope)
{
    internal static NativeResourceRequest? Validate(NativeResourceRequest? request, LogtoPublicOptions options)
    {
        if (request is null) return null;
        if (!request.Resource.IsAbsoluteUri || request.Resource.Scheme != "https" || request.Resource.UserInfo.Length != 0 ||
            request.Resource.Query.Length != 0 || request.Resource.Fragment.Length != 0 ||
            string.IsNullOrWhiteSpace(request.RequiredScope) || request.RequiredScope.Length > 128 ||
            request.RequiredScope.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            request.RequiredScope is "openid" or "profile" or "offline_access" ||
            options.RedirectUri.AbsoluteUri != "http://127.0.0.1:17854/callback/" ||
            !options.Scopes.SequenceEqual(["openid", "profile"], StringComparer.Ordinal))
            throw new IdentityFlowException("AUTH_CONFIGURATION_ERROR");
        return request;
    }
}
