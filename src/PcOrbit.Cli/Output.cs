using System.Globalization;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Compiler;
using PcOrbit.Core.Localization;
using PcOrbit.Core.Model;

namespace PcOrbit.Cli;

/// <summary>
/// Console formatting.
/// </summary>
/// <remarks>
/// Status is never carried by colour alone — every state also has a word or a symbol, because
/// spec 21.12 requires that and because a redirected stdout has no colour at all.
/// </remarks>
public sealed class Output(IStringCatalog strings, bool verbose)
{
    private readonly IStringCatalog _strings = strings;

    public bool Verbose { get; } = verbose;

    public static void Line(string text = "") => Console.WriteLine(text);

    public static void Error(string text) => Console.Error.WriteLine(text);

    public static void Heading(string text)
    {
        Line();
        Line(text);
        Line(new string('-', Math.Min(text.Length, 78)));
    }

    public string Text(string key, IReadOnlyDictionary<string, string>? arguments = null) =>
        _strings.Format(key, arguments);

    /// <summary>The user-facing name of a capability, from the string catalog (spec 21.11).</summary>
    public string CapabilityName(CapabilityId id, string displayKey)
    {
        string name = _strings.Format(displayKey);

        // A missing translation shows the technical id rather than a broken marker: the id is at
        // least actionable, and it tells the reader which key needs writing.
        return name.StartsWith("[[", StringComparison.Ordinal) ? id.Value : name;
    }

    public string Status(CapabilityValue value) => value.Status switch
    {
        CapabilityStatus.Value => value.Raw ?? Text("status.unknown"),
        CapabilityStatus.Unknown => Text("status.unknown"),
        CapabilityStatus.Supported => Text("status.supported"),
        CapabilityStatus.NotSupported => Text("status.notSupported"),
        CapabilityStatus.Enabled => Text("status.enabled"),
        CapabilityStatus.Disabled => Text("status.disabled"),
        CapabilityStatus.Present => Text("status.present"),
        CapabilityStatus.Absent => Text("status.absent"),
        _ => Text("status.unknown"),
    };

    /// <summary>Spec 21.7 — the cost of the plan, before there is any Apply button to press.</summary>
    public void PlanCostHeader(PlanCost cost)
    {
        ArgumentNullException.ThrowIfNull(cost);

        Line(Text("plan.cost.header", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["changes"] = Number(cost.Changes),
            ["restarts"] = Number(cost.Restarts),
            ["minutes"] = Number(cost.EstimatedMinutes),
            ["manual"] = Number(cost.ManualSteps),
        }));

        if (cost.IrreversibleChanges > 0)
        {
            Line(Text("plan.cost.irreversible", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = Number(cost.IrreversibleChanges),
            }));
        }
    }

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Named ICU arguments, kept terse because every localised line needs some.</summary>
    public static Dictionary<string, string> Args(params (string Key, string Value)[] pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        Dictionary<string, string> arguments = new(StringComparer.Ordinal);

        foreach ((string key, string value) in pairs)
        {
            arguments[key] = value;
        }

        return arguments;
    }

    // These three come from the catalog rather than a switch over English words, because they
    // appear in the plan the user reads before pressing Apply (spec 21.7, 21.11).
    public string Restart(RestartKind kind) => Text($"cli.restart.{Camel(kind.ToString())}");

    public string WriteModeLabel(WriteMode mode) => Text($"cli.writeMode.{Camel(mode.ToString())}");

    public string Reversibility(ReversibilityMode mode) => Text($"cli.reversible.{Camel(mode.ToString())}");

    public static string Camel(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];
}

/// <summary>
/// Safe Apply on the console: apply, then ask, then revert if the answer does not come.
/// </summary>
/// <remarks>
/// <para>
/// The countdown is the point. A user who cannot see the screen cannot answer, and silence has to
/// mean "put it back" (spec 10.1). Pressing Enter confirms; anything else, including no input at
/// all, does not.
/// </para>
/// <para>
/// Spec 21.12 asks for a way to extend the countdown for users who need longer: typing <c>+</c>
/// adds another window rather than forcing a rushed decision.
/// </para>
/// </remarks>
public sealed class ConsoleSafeApplyConfirmation : ISafeApplyConfirmation
{
    public async Task<bool> ConfirmAsync(
        string messageKey,
        IReadOnlyDictionary<string, string> arguments,
        int withinSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string from = arguments.TryGetValue("from", out string? f) ? f : "?";
        string to = arguments.TryGetValue("to", out string? t) ? t : "?";

        Console.WriteLine();
        Console.WriteLine($"  Display switched from {from} Hz to {to} Hz.");
        Console.WriteLine($"  Can you still read this? Press Enter to keep it, or + for more time.");

        int remaining = withinSeconds;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Console.Write($"\r  Reverting in {remaining,2}s...  ");

            if (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    Console.WriteLine("  Kept.");
                    return true;
                }

                if (key.KeyChar is '+' or '=')
                {
                    remaining = withinSeconds;
                    continue;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            remaining--;
        }

        Console.WriteLine();
        return false;
    }
}
