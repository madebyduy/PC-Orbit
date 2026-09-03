namespace PcOrbit.Core.Abstractions;

/// <summary>
/// Asks the user whether the machine still works after a risky-but-reversible change.
/// </summary>
/// <remarks>
/// <para>
/// Spec 10.1 Safe Apply: snapshot the old state, apply, ask the user to confirm the machine is
/// still usable, and revert automatically on timeout or failed verification. The confirmation has
/// to be a port because the answer comes from a person, and because the console, the desktop UI
/// and a test all supply it differently.
/// </para>
/// <para>
/// Spec 21.12 also requires a way to extend the countdown for users who need longer — an
/// implementation is free to offer that, and returning true after an extension is still a
/// confirmation.
/// </para>
/// </remarks>
public interface ISafeApplyConfirmation
{
    /// <summary>
    /// True if the user confirmed within the window. False means revert — including when the user
    /// never answered, which is the case Safe Apply exists for: they cannot see the screen.
    /// </summary>
    Task<bool> ConfirmAsync(
        string messageKey,
        IReadOnlyDictionary<string, string> arguments,
        int withinSeconds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Never confirms. The correct default for anything unattended: no answer means revert, so an
/// automated run cannot leave a machine on a display mode nobody can see.
/// </summary>
public sealed class NeverConfirms : ISafeApplyConfirmation
{
    public static NeverConfirms Instance { get; } = new();

    public Task<bool> ConfirmAsync(
        string messageKey,
        IReadOnlyDictionary<string, string> arguments,
        int withinSeconds,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
}
