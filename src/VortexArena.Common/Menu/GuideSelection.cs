using System;
using System.Collections.Generic;

namespace VortexArena.Common.Menu;

/// <summary>Remembers one stable entry ID per topic while its visible rows are rebuilt.</summary>
public sealed class GuideSelection
{
    private readonly Dictionary<string, string> _selectedIds = new(StringComparer.Ordinal);

    public void Remember(string topic, string entryId) => _selectedIds[topic] = entryId;

    public void RememberInitial(string topic, string entryId) => _selectedIds.TryAdd(topic, entryId);

    public int Resolve<T>(string topic, IReadOnlyList<T> visibleEntries, Func<T, string> entryId)
    {
        if (visibleEntries.Count == 0)
            return -1;

        if (!_selectedIds.TryGetValue(topic, out string? selectedId))
            return 0;

        for (int i = 0; i < visibleEntries.Count; i++)
            if (entryId(visibleEntries[i]) == selectedId)
                return i;

        return 0;
    }
}
