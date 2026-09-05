using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace BasketService;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var subject = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? throw new InvalidOperationException("Token is missing a 'sub' claim.");

        return Guid.Parse(subject);
    }
}
