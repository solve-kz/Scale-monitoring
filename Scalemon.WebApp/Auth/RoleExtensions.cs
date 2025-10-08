using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;

namespace Scalemon.WebApp.Auth;

public static class RoleExtensions
{
    private static readonly Dictionary<string, int> RoleRank =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["viewer"] = 0,
            ["editor"] = 1,
            ["admin"] = 2,
        };

    public static bool HasRoleAtLeast(this ClaimsPrincipal user, string minimumRole)
    {
        if (!(user.Identity?.IsAuthenticated ?? false))
        {
            return false;
        }

        if (!RoleRank.TryGetValue(minimumRole, out var required))
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRole));
        }

        return user.FindAll(ClaimTypes.Role)
                   .Select(claim => RoleRank.TryGetValue(claim.Value, out var level) ? level : -1)
                   .Any(level => level >= required);
    }
}
