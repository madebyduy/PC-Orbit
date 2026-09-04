namespace PcOrbit.Core.Model;

/// <summary>How confident we are in a reading or a relation (spec 8.3.1, 11.3).</summary>
public enum Confidence
{
    None = 0,
    Low,
    Medium,
    High,
}

/// <summary>Where a fact came from. Ordered from "the machine told us" to "we guessed".</summary>
public enum EvidenceSourceKind
{
    Unknown = 0,
    Wmi,
    Registry,
    PowerShell,
    Win32Api,

    /// <summary>
    /// A file or folder read directly. Distinct from <see cref="Win32Api"/> because what makes it
    /// trustworthy is different: the answer is whatever is on the disk at that moment, and it can
    /// change between the reading and the change made from it.
    /// </summary>
    FileSystem,

    VendorApi,
    Documentation,

    /// <summary>Derived, not measured. Never enough on its own to authorise a write (spec 11.3).</summary>
    Inference,
}

/// <summary>
/// Why we believe something. Every reading carries one, so the UI can always answer
/// "how do you know?" and a support report can be audited later (spec 6.1, 6.4).
/// </summary>
/// <param name="SourceKind">Class of source.</param>
/// <param name="Source">Human-readable origin, e.g. the WMI class or the cmdlet name.</param>
/// <param name="Confidence">How much weight this evidence carries.</param>
/// <param name="Query">The exact query issued, when there is one. Shown in Advanced mode.</param>
/// <param name="RawResult">
/// The raw answer, trimmed. Kept because "the value we read" and "the value we displayed"
/// drifting apart is a bug class we want visible (spec 20.1).
/// </param>
public sealed record Evidence(
    EvidenceSourceKind SourceKind,
    string Source,
    Confidence Confidence,
    string? Query = null,
    string? RawResult = null)
{
    /// <summary>A read that did not work. Records the reason instead of pretending.</summary>
    public static Evidence Missing(string reason) =>
        new(EvidenceSourceKind.Unknown, reason, Confidence.None);

    public static Evidence Inferred(string reason, Confidence confidence = Confidence.Medium) =>
        new(EvidenceSourceKind.Inference, reason, confidence);
}
