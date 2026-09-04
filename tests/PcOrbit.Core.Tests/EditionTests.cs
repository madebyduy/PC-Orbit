using PcOrbit.Adapters.Windows;
using PcOrbit.Core.Abstractions;

namespace PcOrbit.Core.Tests;

/// <summary>
/// Edition conversion's two expensive places to be wrong: reading what Windows offered, and
/// handling the product key (ADR 0006).
/// </summary>
public sealed class EditionTests
{
    /// <summary>Real DISM output, as it comes back on a Home machine.</summary>
    private const string HomeOutput = """
        Deployment Image Servicing and Management tool
        Version: 10.0.26100.1

        Editions that can be upgraded to:

        Target Edition : Professional
        Target Edition : ProfessionalWorkstation
        Target Edition : Education

        The operation completed successfully.
        """;

    // ---------------------------------------------------------------- what Windows offered

    [Fact]
    public void TheOfferedEditionsAreTakenFromDismRatherThanAssumed()
    {
        IReadOnlyList<TargetEdition> targets = EditionOutput.ParseTargets(HomeOutput);

        Assert.Equal(
            ["Education", "Professional", "ProfessionalWorkstation"],
            targets.Select(t => t.Id));
    }

    [Fact]
    public void ARunOfWordsIsSplitSoAnEditionReadsAsAName()
    {
        TargetEdition workstation = EditionOutput.ParseTargets(HomeOutput)
            .Single(t => t.Id == "ProfessionalWorkstation");

        Assert.Equal("Professional Workstation", workstation.DisplayName);
    }

    /// <summary>
    /// An installation Windows will not move offers nothing. Empty is a real answer here, and the
    /// UI has to be able to tell it from "we could not ask".
    /// </summary>
    [Fact]
    public void OutputWithNoTargetsProducesNone()
    {
        Assert.Empty(EditionOutput.ParseTargets("The operation completed successfully."));
    }

    /// <summary>
    /// DISM refuses without elevation, and that must not read as "this machine cannot change
    /// edition" — those two send the user to completely different places.
    /// </summary>
    [Fact]
    public void DismRefusingForWantOfRightsIsRecognisedAsThat()
    {
        Assert.True(EditionOutput.NeedsElevation("Error: 740\n\nElevated permissions are required to run DISM."));
        Assert.False(EditionOutput.NeedsElevation(HomeOutput));
    }

    // ---------------------------------------------------------------- the product key

    [Theory]
    [InlineData("VK7JG-NPHTM-C97JM-9MPGT-3V66T", true)]
    [InlineData("W269N-WFGWX-YVC9B-4J6C9-T83GX", true)]
    [InlineData("vk7jg-nphtm-c97jm-9mpgt-3v66t", true)]
    [InlineData(" VK7JG-NPHTM-C97JM-9MPGT-3V66T ", true)]
    [InlineData("not-a-key", false)]
    [InlineData("VK7JG-NPHTM-C97JM-9MPGT", false)]
    [InlineData("VK7JG NPHTM C97JM 9MPGT 3V66T", false)]
    [InlineData("", false)]
    public void OnlySomethingShapedLikeAKeyReachesDism(string key, bool accepted)
    {
        Assert.Equal(accepted, EditionOutput.LooksLikeProductKey(key));
    }

    /// <summary>
    /// The key must not survive into anything durable. DISM echoes its arguments into some failure
    /// messages and this product writes failures to an append-only log.
    /// </summary>
    [Fact]
    public void AKeyIsRemovedFromAnythingOnItsWayToALog()
    {
        const string key = "VK7JG-NPHTM-C97JM-9MPGT-3V66T";

        string scrubbed = EditionOutput.Scrub(
            $"Error: /ProductKey:{key} was not accepted for this edition.",
            key);

        Assert.DoesNotContain(key, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("was not accepted", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrubbingIsCaseInsensitiveAndSurvivesSurroundingSpace()
    {
        const string key = "VK7JG-NPHTM-C97JM-9MPGT-3V66T";

        Assert.DoesNotContain(
            "vk7jg",
            EditionOutput.Scrub($"key vk7jg-nphtm-c97jm-9mpgt-3v66t rejected", $"  {key}  "),
            StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- licence

    [Theory]
    [InlineData(1, LicenceState.Licensed)]
    [InlineData(2, LicenceState.Grace)]
    [InlineData(5, LicenceState.Notification)]
    [InlineData(0, LicenceState.Unlicensed)]
    [InlineData(null, LicenceState.Unknown)]
    public void LicenceStatusIsMappedFromWindowsOwnCodes(int? status, LicenceState expected)
    {
        Assert.Equal(expected, EditionOutput.ToLicenceState(status));
    }

    /// <summary>
    /// An edition change does not carry activation with it, so starting unactivated means finishing
    /// unactivated — and that belongs in front of the user before the change, not after.
    /// </summary>
    [Fact]
    public void AnUnactivatedInstallationIsFlaggedBeforeAnythingIsChanged()
    {
        var licensed = new LicenceStatus(LicenceState.Licensed, "RETAIL", null, Model.Evidence.Missing("test"));
        var notified = new LicenceStatus(LicenceState.Notification, "RETAIL", null, Model.Evidence.Missing("test"));

        Assert.False(licensed.NeedsAttentionBeforeChanging);
        Assert.True(notified.NeedsAttentionBeforeChanging);
    }
}
