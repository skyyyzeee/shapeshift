using Shapeshift.Core;

namespace Shapeshift.Engine.Tweaks;

/// <summary>Твики профиля, найденные в каталоге, — в порядке каталога (он задаёт порядок действий).</summary>
public sealed record TweakSelection(IReadOnlyList<Tweak> Tweaks, IReadOnlyList<string> Problems)
{
    /// <param name="release">Выпуск образа; без него применимость не проверяется.</param>
    public static TweakSelection Resolve(IReadOnlyList<string> ids, TweakCatalog catalog, Release? release)
    {
        var problems = new List<string>();

        foreach (var id in ids.Where(id => catalog.Find(id) is null))
            problems.Add($"unknown tweak '{id}'.");

        var selected = catalog.Tweaks.Where(t => ids.Contains(t.Id)).ToList();

        for (var i = 0; i < selected.Count; i++)
        {
            for (var j = i + 1; j < selected.Count; j++)
            {
                if (TweakCatalog.InConflict(selected[i], selected[j]))
                    problems.Add($"tweaks '{selected[i].Id}' and '{selected[j].Id}' conflict; choose one.");
            }
        }

        if (release is not null)
        {
            foreach (var tweak in selected.Where(t => !t.Releases.Matches(release)))
                problems.Add($"tweak '{tweak.Id}' does not apply to {release}.");
        }

        return new TweakSelection(selected, problems);
    }
}
