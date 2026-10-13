namespace Psm.Data.Model;

public enum EdgeKind
{
    /// <summary>Source should be near the target (reached through a corridor).</summary>
    Adjacent,

    /// <summary>Source opens directly onto the target through a shared wall (source has Shared Access).</summary>
    SharedWall,
}

/// <summary>
/// Directed instance-level adjacency: the source room pulls toward the target room.
/// Kind is decided by the source row's Shared Access flag, not by whether the target is the ID parent.
/// </summary>
public sealed record Edge(string FromKey, string ToKey, EdgeKind Kind);
