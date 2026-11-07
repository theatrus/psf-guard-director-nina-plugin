namespace PsfGuard.Director.Runtime;

// Past recovery journals remain evidence. A reconciled new session gets a new
// journal and fresh server authority instead of replaying a previous permit.
public static class CoordinatorRecoveryHistory
{
    public static string? Archive(string stateRoot, Guid rig)
    {
        if (!Path.IsPathFullyQualified(stateRoot) || rig == Guid.Empty) throw new ArgumentException("Exact recovery scope required.");
        var root = Path.GetFullPath(stateRoot);
        var source = Path.GetFullPath(Path.Combine(root, "recovery", rig.ToString("D")));
        if (!Directory.Exists(source)) return null;
        var destination = Path.GetFullPath(Path.Combine(root, "recovery-history", rig.ToString("D"), Guid.NewGuid().ToString("N")));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Recovery paths escaped the session state folder.");
        foreach (var path in new[] { root, Path.GetDirectoryName(source)!, source })
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Recovery history refuses linked folders.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        foreach (var path in new[] { Path.Combine(root, "recovery-history"), Path.GetDirectoryName(destination)! })
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Recovery history refuses linked folders.");
        Directory.Move(source, destination);
        return destination;
    }
}
