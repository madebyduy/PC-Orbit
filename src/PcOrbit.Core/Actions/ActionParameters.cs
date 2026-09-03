namespace PcOrbit.Core.Actions;

/// <summary>
/// Reads manifest parameters, refusing anything not on an explicit allowlist.
/// </summary>
/// <remarks>
/// Spec 19.1: the privileged side accepts an action id plus schema-valid parameters, never a
/// command line. This is where "schema-valid" is enforced in code. Every executor that puts a
/// parameter anywhere near a command must come through <see cref="OneOf"/> — a value the manifest
/// author invented is a bug, and a value an attacker invented is worse.
/// </remarks>
public static class ActionParameters
{
    public static string Required(ActionDefinition action, string name)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return action.Parameters.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ActionParameterException(action.Id, name, "is missing");
    }

    /// <summary>The parameter value, but only if it is one of <paramref name="allowed"/>.</summary>
    public static string OneOf(ActionDefinition action, string name, IEnumerable<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);

        string value = Required(action, name);
        List<string> permitted = [.. allowed];

        return permitted.Contains(value, StringComparer.Ordinal)
            ? value
            : throw new ActionParameterException(
                action.Id,
                name,
                $"is '{value}', which is not one of the allowed values: {string.Join(", ", permitted)}");
    }
}

/// <summary>
/// A manifest asked for something the executor will not do. Fails the step loudly: this is a
/// packaging or authoring error, and quietly guessing an alternative is exactly wrong here.
/// </summary>
public sealed class ActionParameterException(string actionId, string parameterName, string problem)
    : Exception($"Action '{actionId}': parameter '{parameterName}' {problem}.")
{
    public string ActionId { get; } = actionId;

    public string ParameterName { get; } = parameterName;
}
