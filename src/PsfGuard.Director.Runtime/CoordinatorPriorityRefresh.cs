namespace PsfGuard.Director.Runtime;

/// <summary>Detects changed priority intent, not execution progress or new authority.</summary>
public static class CoordinatorPriorityRefresh
{
    public static bool Required(DirectorProgram held, DirectorProgram preview)
    {
        // Topology changes need the broader plan-reconciliation workflow. A
        // preview never authorizes dispatch; the caller must release and obtain
        // a fresh commissioned allocation before running its priorities.
        if (held.Configuration.Id != preview.Configuration.Id || preview.ObservingPreferences is not null
            || held.Assignment.Goals.Length != preview.Assignment.Goals.Length) return false;
        var priorities = held.Assignment.Goals.ToDictionary(g => g.Id, g => g.Priority, StringComparer.Ordinal);
        if (preview.Assignment.Goals.Any(g => !priorities.ContainsKey(g.Id))) return false;
        return held.ObservingPreferences is not null || preview.Assignment.Goals.Any(g => priorities[g.Id] != g.Priority);
    }
}
