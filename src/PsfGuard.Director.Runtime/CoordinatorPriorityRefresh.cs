namespace PsfGuard.Director.Runtime;

/// <summary>Detects changed plan intent without treating image progress as new work.</summary>
public static class CoordinatorPriorityRefresh
{
    // A preview is only a reason to release and request work. It never replaces
    // the issued program or authorizes a capture. Ignore rolling time windows
    // and image progress so an unchanged plan does not repeatedly restart.
    public static bool RequiredWhileWaiting(DirectorProgram held, DirectorProgram preview)
    {
        if (held.Configuration.Id != preview.Configuration.Id) return false;
        var goals = preview.Assignment.Goals.ToDictionary(g => g.Id, StringComparer.Ordinal);
        if (held.Assignment.Goals.Length != goals.Count) return true;
        foreach (var goal in held.Assignment.Goals)
        {
            if (!goals.TryGetValue(goal.Id, out var next) || goal.Requested != next.Requested
                || goal.Priority != next.Priority || goal.ExposureMs != next.ExposureMs) return true;
            var binding = held.Bindings.Single(b => b.GoalId == goal.Id);
            var nextBinding = preview.Bindings.Single(b => b.GoalId == goal.Id);
            if (binding != nextBinding
                || held.Targets.Single(t => t.Id == binding.TargetId) != preview.Targets.Single(t => t.Id == nextBinding.TargetId)
                || held.Recipes.Single(r => r.Id == binding.RecipeId) != preview.Recipes.Single(r => r.Id == nextBinding.RecipeId)) return true;
        }
        return Required(held, preview);
    }

    public static bool Required(DirectorProgram held, DirectorProgram preview)
    {
        // During acquisition only priority changes are considered. The waiting
        // loop also checks plan contents with RequiredWhileWaiting. Both paths
        // must release and obtain a new allocation before running changed work.
        if (held.Configuration.Id != preview.Configuration.Id || preview.ObservingPreferences is not null
            || held.Assignment.Goals.Length != preview.Assignment.Goals.Length) return false;
        var priorities = held.Assignment.Goals.ToDictionary(g => g.Id, g => g.Priority, StringComparer.Ordinal);
        if (preview.Assignment.Goals.Any(g => !priorities.ContainsKey(g.Id))) return false;
        return held.ObservingPreferences is not null || preview.Assignment.Goals.Any(g => priorities[g.Id] != g.Priority);
    }
}
