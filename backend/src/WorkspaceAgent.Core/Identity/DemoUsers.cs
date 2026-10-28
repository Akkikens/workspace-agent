namespace WorkspaceAgent.Identity;

/// <summary>Roles the semantic layer knows about. Row filters and column grants are keyed by these.</summary>
public enum UserRole { Engineer, ProgramManager, Director }

public sealed record UserContext(string Id, string Name, UserRole Role, string? BusinessUnit)
{
    public string RoleName => Role switch
    {
        UserRole.Engineer => "engineer",
        UserRole.ProgramManager => "program_manager",
        UserRole.Director => "director",
        _ => throw new ArgumentOutOfRangeException(nameof(Role)),
    };
}

/// <summary>
/// Demo identities. There is no sign-in: the HTTP host maps the X-Demo-User header to one of these.
/// A real deployment would map Entra ID (OIDC) claims to the same <see cref="UserContext"/>.
/// </summary>
public static class DemoUsers
{
    public static readonly UserContext Alice = new("alice", "Alice Chen", UserRole.Engineer, "Modem");
    public static readonly UserContext Bob = new("bob", "Bob Patel", UserRole.ProgramManager, "Camera");
    public static readonly UserContext Carol = new("carol", "Carol Diaz", UserRole.Director, null);

    public static IReadOnlyList<UserContext> All { get; } = [Alice, Bob, Carol];

    public static UserContext? Find(string? id) =>
        All.FirstOrDefault(u => string.Equals(u.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
}
