using System.Collections.Immutable;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Actions;

/// <summary>
/// Every action the app knows how to perform, with the selection rule the compiler uses.
/// </summary>
public sealed class ActionCatalog
{
    private readonly ImmutableDictionary<string, ActionDefinition> _byId;
    private readonly ImmutableArray<ActionDefinition> _all;

    public ActionCatalog(IEnumerable<ActionDefinition> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        _all = [.. actions.OrderBy(a => a.Id, StringComparer.Ordinal)];

        var duplicates = _all
            .GroupBy(a => a.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new ArgumentException(
                $"Duplicate action ids: {string.Join(", ", duplicates)}.",
                nameof(actions));
        }

        _byId = _all.ToImmutableDictionary(a => a.Id, a => a, StringComparer.Ordinal);
    }

    public static ActionCatalog Empty { get; } = new([]);

    public IReadOnlyList<ActionDefinition> All => _all;

    /// <summary>
    /// Version of the catalog as a whole, recorded in plan provenance so we can tell whether a
    /// stored plan was built from the action definitions we ship today (spec 9.5, 12.3).
    /// </summary>
    public string Version =>
        _all.Length == 0 ? "empty" : string.Join(",", _all.Select(a => $"{a.Id}@{a.Version}"));

    public ActionDefinition? ById(string id) =>
        _byId.TryGetValue(id, out ActionDefinition? action) ? action : null;

    /// <summary>
    /// Actions that would satisfy <paramref name="capability"/> = <paramref name="desired"/> on
    /// this machine, best route first.
    /// </summary>
    /// <remarks>
    /// Spec 12.3: prefer the less risky and more reversible route. Concretely, in order:
    /// lowest risk, then most reversible, then the route that needs least from the user
    /// (Auto before Guided), then fastest, then id — the last tiebreak exists purely so the
    /// same snapshot always produces the same plan.
    /// </remarks>
    public IReadOnlyList<ActionDefinition> CandidatesFor(
        CapabilityId capability,
        CapabilityValue desired,
        MachineIdentity machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        return
        [
            .. _all
                .Where(a => a.CanProvide(capability, desired))
                .Where(a => a.Compatibility.Matches(machine))
                .Where(a => a.WriteMode is not (WriteMode.Unsupported or WriteMode.ReadOnly))
                .OrderBy(a => a.Risk)
                .ThenByDescending(a => a.Reversible.Mode)
                .ThenByDescending(a => a.WriteMode)
                .ThenBy(a => a.EstimatedSeconds)
                .ThenBy(a => a.Id, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Actions that target the capability but cannot be used here, with the reason. The compiler
    /// reports these instead of silently saying "not possible" (spec 6.6, 8.3.4).
    /// </summary>
    public IReadOnlyList<(ActionDefinition Action, string Reason)> RejectedFor(
        CapabilityId capability,
        CapabilityValue desired,
        MachineIdentity machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        List<(ActionDefinition, string)> rejected = [];

        foreach (ActionDefinition action in _all.Where(a => a.CanProvide(capability, desired)))
        {
            if (action.WriteMode is WriteMode.Unsupported or WriteMode.ReadOnly)
            {
                rejected.Add((action, $"write mode is {action.WriteMode}"));
            }
            else if (!action.Compatibility.Matches(machine))
            {
                rejected.Add((action, "not compatible with this machine"));
            }
        }

        return rejected;
    }
}
