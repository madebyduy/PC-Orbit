using PcOrbit.Core.Actions;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Guides;
using PcOrbit.Core.Model;
using PcOrbit.Core.Outcomes;
using PcOrbit.Core.Serialization;

namespace PcOrbit.Core.Tests;

/// <summary>
/// Guards on the shipped data files and their loaders.
/// </summary>
/// <remarks>
/// Spec step 3 is explicit that the graph, outcome and manifest contracts get settled before
/// anything depends on them. These tests are what stops a well-meaning data contribution from
/// quietly promising something the app cannot deliver.
/// </remarks>
public sealed class ShippedDataTests
{
    [Fact]
    public void TheShippedGraphIsValid() => Assert.Empty(ShippedData.Graph().Validate());

    [Fact]
    public void EveryCapabilityAnActionMentionsExistsInTheGraph()
    {
        CapabilityGraph graph = ShippedData.Graph();

        foreach (ActionDefinition action in ShippedData.Catalog().All)
        {
            foreach (CapabilityAssertion assertion in action.Provides.Concat(action.Requires))
            {
                Assert.True(
                    graph.Contains(assertion.Capability),
                    $"Action '{action.Id}' mentions '{assertion.Capability}', which the graph does not declare.");
            }

            foreach (VerificationStep step in action.Verify)
            {
                Assert.True(
                    graph.Contains(step.Capability),
                    $"Action '{action.Id}' verifies '{step.Capability}', which the graph does not declare.");
            }
        }
    }

    [Fact]
    public void EveryCapabilityAnOutcomeMentionsExistsInTheGraph()
    {
        CapabilityGraph graph = ShippedData.Graph();

        foreach (Outcome outcome in ShippedData.Outcomes())
        {
            foreach (CapabilityRequirement requirement in outcome.Requires)
            {
                Assert.True(graph.Contains(requirement.Capability), $"'{requirement.Capability}' is not in the graph.");
            }

            foreach (VerificationCheck check in outcome.Verify)
            {
                Assert.True(graph.Contains(check.Check), $"'{check.Check}' is not in the graph.");
            }
        }
    }

    /// <summary>Spec 28: every action declares detect, preflight, apply, verify and rollback.</summary>
    [Fact]
    public void EveryActionDeclaresVerificationAndReversibility()
    {
        foreach (ActionDefinition action in ShippedData.Catalog().All)
        {
            Assert.NotEmpty(action.Verify);
            Assert.NotEmpty(action.Provides);
            Assert.False(string.IsNullOrWhiteSpace(action.Executor));
            Assert.False(string.IsNullOrWhiteSpace(action.Version));

            // "none" is an allowed answer, but it has to be a stated one, so the UI can warn
            // before Apply instead of after (spec 9.1, 21.7).
            Assert.True(Enum.IsDefined(action.Reversible.Mode));
        }
    }

    /// <summary>
    /// Spec 19.2 puts BIOS flashing in Critical and spec 12.3 stops the compiler planning it.
    /// Nothing shipped should even be asking.
    /// </summary>
    [Fact]
    public void NothingShippedClaimsCriticalRisk() =>
        Assert.DoesNotContain(ShippedData.Catalog().All, a => a.Risk == RiskClass.Critical);

    /// <summary>
    /// Decision 19 and spec 8.3.5: no automatic firmware write ships until a vendor adapter has
    /// been verified on real hardware. The Dell manifest is parked in pending-verification, which
    /// the loader does not read — this test proves the gate actually holds.
    /// </summary>
    [Fact]
    public void NoLoadedActionWritesFirmwareAutomatically()
    {
        IReadOnlyList<ActionDefinition> automaticFirmwareWrites =
        [
            .. ShippedData.Catalog().All.Where(a =>
                a.WriteMode == WriteMode.Auto
                && a.Provides.Any(p => p.Capability.Value.StartsWith("firmware.", StringComparison.Ordinal))),
        ];

        Assert.Empty(automaticFirmwareWrites);
    }

    [Fact]
    public void TheParkedDellManifestExistsButIsNotLoaded()
    {
        string parked = Path.Combine(
            ShippedData.Directory,
            "actions",
            "pending-verification",
            "firmware.dell.actions.json");

        Assert.True(File.Exists(parked), "the parked vendor manifest should still be in the repo as a contract example");
        Assert.Null(ShippedData.Catalog().ById("firmware.virtualization.enable.auto.dell"));
    }

    [Fact]
    public void EveryGuidedActionNamesAGuideThatExists()
    {
        IReadOnlyDictionary<string, GuideData> guides = ShippedData.Guides();

        foreach (ActionDefinition action in ShippedData.Catalog().All.Where(a => a.IsManualStep))
        {
            Assert.False(string.IsNullOrWhiteSpace(action.GuideId), $"'{action.Id}' is guided but names no guide.");
            Assert.True(guides.ContainsKey(action.GuideId!), $"'{action.Id}' names guide '{action.GuideId}', which is missing.");
        }
    }

    /// <summary>
    /// Spec 10.3, no exceptions: anything touching firmware or boot runs the BitLocker preflight.
    /// </summary>
    [Fact]
    public void EveryFirmwareActionDeclaresTheBitLockerPreflight()
    {
        IEnumerable<ActionDefinition> firmwareActions = ShippedData.Catalog().All.Where(a =>
            a.Restart == RestartKind.Firmware
            || a.Provides.Any(p => p.Capability.Value.StartsWith("firmware.", StringComparison.Ordinal)));

        foreach (ActionDefinition action in firmwareActions)
        {
            Assert.Contains(PreflightKind.Bitlocker, action.Preflight);
        }
    }

    [Fact]
    public void EveryActionThatNeedsAdministratorRightsSaysSo()
    {
        foreach (ActionDefinition action in ShippedData.Catalog().All
            .Where(a => a.Privilege == PrivilegeLevel.Administrator))
        {
            Assert.Contains(PreflightKind.Elevation, action.Preflight);
        }
    }

    /// <summary>Every graph node is reachable by search in both languages (spec 21.2, 21.11).</summary>
    [Fact]
    public void ObservableCapabilitiesHaveSearchAliases()
    {
        foreach (CapabilityNode node in ShippedData.Graph().Nodes.Where(n => n.Observable))
        {
            Assert.NotEmpty(node.Aliases);
        }
    }
}

public sealed class LoaderGuardTests
{
    /// <summary>
    /// A manifest may only name a reviewed executor, never carry script. The loader also refuses
    /// an Auto firmware claim with no vendor restriction (spec 8.3.5, 17.1, 19.1).
    /// </summary>
    [Fact]
    public void RejectsAnAutoFirmwareActionWithNoVendorRestriction()
    {
        const string json = """
            [{
              "id": "firmware.something.enable.auto",
              "version": "1.0.0",
              "executor": "firmware.vendor.dell",
              "provides": [{ "capability": "firmware.cpu.virtualization", "state": "enabled" }],
              "writeMode": "auto",
              "privilege": "administrator",
              "risk": "high",
              "restart": "windows",
              "reversible": { "mode": "automatic" },
              "verify": [{ "capability": "firmware.cpu.virtualization", "expected": "enabled" }]
            }]
            """;

        DataFileException error = Assert.Throws<DataFileException>(() => ActionCatalogLoader.Load(json));
        Assert.Contains("vendor", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsAnActionWithNoVerification()
    {
        const string json = """
            [{
              "id": "windows.feature.enable.something",
              "version": "1.0.0",
              "executor": "windows.optional-feature.enable",
              "provides": [{ "capability": "windows.feature.wsl", "state": "enabled" }],
              "writeMode": "auto",
              "privilege": "administrator",
              "risk": "medium",
              "restart": "windows",
              "reversible": { "mode": "automatic" },
              "verify": []
            }]
            """;

        DataFileException error = Assert.Throws<DataFileException>(() => ActionCatalogLoader.Load(json));
        Assert.Contains("verif", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsAnOutcomeWithNoVerification()
    {
        const string json = """
            {
              "id": "outcome.something",
              "version": "1.0.0",
              "requires": [{ "capability": "windows.feature.wsl", "state": "enabled" }],
              "verify": []
            }
            """;

        DataFileException error = Assert.Throws<DataFileException>(() => OutcomeLoader.Load(json));
        Assert.Contains("verif", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The compiler plans from requires edges, so spec 11.3 will not let one exist on weak
    /// evidence. Enforced at load time rather than at review time.
    /// </summary>
    [Fact]
    public void RejectsARequiresEdgeWithLowConfidenceEvidence()
    {
        const string json = """
            {
              "graphVersion": "1.0.0",
              "nodes": [
                { "id": "a.thing", "kind": "osFeature", "displayKey": "cap.a.thing" },
                { "id": "b.thing", "kind": "firmwareCapability", "displayKey": "cap.b.thing" }
              ],
              "edges": [{
                "from": "a.thing",
                "kind": "requires",
                "to": "b.thing",
                "evidence": { "sourceKind": "inference", "source": "someone on a forum said so", "confidence": "low" }
              }]
            }
            """;

        DataFileException error = Assert.Throws<DataFileException>(() => CapabilityGraphLoader.Load(json));
        Assert.Contains("confidence", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsAnEdgeWithNoEvidenceAtAll()
    {
        const string json = """
            {
              "graphVersion": "1.0.0",
              "nodes": [
                { "id": "a.thing", "kind": "osFeature", "displayKey": "cap.a.thing" },
                { "id": "b.thing", "kind": "firmwareCapability", "displayKey": "cap.b.thing" }
              ],
              "edges": [{ "from": "a.thing", "kind": "supports", "to": "b.thing" }]
            }
            """;

        DataFileException error = Assert.Throws<DataFileException>(() => CapabilityGraphLoader.Load(json));
        Assert.Contains("evidence", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsAnEdgePointingAtAnUndeclaredCapability()
    {
        const string json = """
            {
              "graphVersion": "1.0.0",
              "nodes": [{ "id": "a.thing", "kind": "osFeature", "displayKey": "cap.a.thing" }],
              "edges": [{
                "from": "a.thing",
                "kind": "requires",
                "to": "b.missing",
                "evidence": { "sourceKind": "documentation", "source": "docs", "confidence": "high" }
              }]
            }
            """;

        DataFileException error = Assert.Throws<DataFileException>(() => CapabilityGraphLoader.Load(json));
        Assert.Contains("not declared", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Spec 18.4: a Tier 2 claim needs evidence from a real machine, not a vendor manual. The
    /// loader refuses guidedVerified with an empty verifiedOn list.
    /// </summary>
    [Fact]
    public void RejectsAVerifiedGuideEntryWithNoMachineItWasVerifiedOn()
    {
        const string json = """
            {
              "id": "guide.test",
              "version": "1.0.0",
              "capability": "firmware.cpu.virtualization",
              "targetState": "enabled",
              "entries": [{
                "match": { "vendors": ["ASUS"] },
                "tier": "guidedVerified",
                "settingName": "SVM Mode",
                "menuPath": ["Advanced", "CPU Configuration"],
                "verifiedOn": []
              }],
              "fallback": { "tier": "guidedGeneric", "settingName": "Virtualization", "menuPath": ["Advanced"] }
            }
            """;

        DataFileException error = Assert.Throws<DataFileException>(() => GuideDataLoader.Load(json));
        Assert.Contains("verified", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsGuideDataWithNoFallback()
    {
        const string json = """
            {
              "id": "guide.test",
              "version": "1.0.0",
              "capability": "firmware.cpu.virtualization",
              "targetState": "enabled",
              "entries": []
            }
            """;

        DataFileException error = Assert.Throws<DataFileException>(() => GuideDataLoader.Load(json));
        Assert.Contains("fallback", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class GuideSelectionTests
{
    private static GuideData FirmwareGuide =>
        ShippedData.Guides()["guide.firmware.virtualization"];

    /// <summary>A date inside every shipped pack's life, so these tests are about selection.</summary>
    private static DateOnly Today => new(2026, 9, 4);

    /// <summary>
    /// The entry for this machine, asserting there is one. Selection returns null only for an
    /// expired pack, which these tests are not about — <see cref="GuidePackExpiryTests"/> is.
    /// </summary>
    private static GuideEntry Select(MachineIdentity machine)
    {
        GuideEntry? entry = FirmwareGuide.SelectFor(machine, Today);
        Assert.NotNull(entry);
        return entry;
    }

    [Fact]
    public void PicksTheAmdWordingForAnAmdBoard()
    {
        GuideEntry entry = Select(Machines.AsusAmdDesktop);

        Assert.Equal("SVM Mode", entry.SettingName);
        Assert.Contains("CPU Configuration", entry.MenuPath);
    }

    /// <summary>
    /// Spec 8.3.3 lists the vendor-specific names precisely because "Virtualization" is not what
    /// the user will see on screen. An Intel machine must not be told to look for SVM Mode.
    /// </summary>
    [Fact]
    public void PicksTheIntelWordingForAnIntelMachine()
    {
        MachineIdentity asusIntel = Machines.AsusAmdDesktop with
        {
            CpuVendor = CpuVendor.Intel,
            CpuName = "Intel(R) Core(TM) i7-13700K",
        };

        GuideEntry entry = Select(asusIntel);

        Assert.Contains("Virtualization Technology", entry.SettingName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SVM", entry.SettingName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FallsBackToGenericInstructionsForAnUnknownVendor()
    {
        MachineIdentity obscure = Machines.AsusAmdDesktop with
        {
            SystemVendor = "Some Vendor Nobody Has Heard Of",
            BaseBoardVendor = "Some Vendor Nobody Has Heard Of",
        };

        GuideEntry entry = Select(obscure);

        Assert.Equal(GuideTier.GuidedGeneric, entry.Tier);
        Assert.Equal("guide.note.unknown-model", entry.NoteKey);
    }

    /// <summary>Spec 18.4: the tier a machine is shown comes from evidence, not from hope.</summary>
    [Fact]
    public void TierComesFromWhatWeCanActuallyDo()
    {
        ActionCatalog catalog = ShippedData.Catalog();

        Assert.Equal(
            SupportTier.GuidedGeneric,
            SupportTierResolver.Resolve(Machines.AsusAmdDesktop, catalog, FirmwareGuide, Today));

        // Dell too: the vendor adapter is not verified, so no machine gets Tier 1 today.
        Assert.Equal(
            SupportTier.GuidedGeneric,
            SupportTierResolver.Resolve(Machines.DellIntelLaptop, catalog, FirmwareGuide, Today));

        // A virtual machine has no firmware setup a user can reach.
        Assert.Equal(
            SupportTier.ReadOnly,
            SupportTierResolver.Resolve(Machines.VirtualMachine, catalog, FirmwareGuide, Today));
    }

    [Fact]
    public void UnrecognisedHardwareIsReadOnly()
    {
        MachineIdentity unknown = MachineIdentity.Unknown;

        Assert.Equal(
            SupportTier.ReadOnly,
            SupportTierResolver.Resolve(unknown, ShippedData.Catalog(), FirmwareGuide, Today));
    }
}

public sealed class CapabilityGraphTests
{
    [Fact]
    public void RequirementClosureReturnsDependenciesFirst()
    {
        CapabilityGraph graph = ShippedData.Graph();

        IReadOnlyList<CapabilityId> order = graph.RequirementClosure(
            [CapabilityId.Parse("workload.docker-wsl2-ready")],
            Machines.AsusAmdDesktop);

        int Position(string capability) => order.ToList().FindIndex(c => c.Value == capability);

        Assert.True(Position("cpu.virtualization") < Position("firmware.cpu.virtualization"));
        Assert.True(Position("firmware.cpu.virtualization") < Position("windows.feature.virtual-machine-platform"));
        Assert.True(Position("windows.feature.virtual-machine-platform") < Position("workload.wsl2-ready"));
        Assert.True(Position("workload.wsl2-ready") < Position("workload.docker-wsl2-ready"));
    }

    [Fact]
    public void DetectsARequiresCycleAsADataBug()
    {
        const string json = """
            {
              "graphVersion": "1.0.0",
              "nodes": [
                { "id": "a.thing", "kind": "osFeature", "displayKey": "cap.a" },
                { "id": "b.thing", "kind": "osFeature", "displayKey": "cap.b" }
              ],
              "edges": [
                {
                  "from": "a.thing", "kind": "requires", "to": "b.thing",
                  "evidence": { "sourceKind": "documentation", "source": "docs", "confidence": "high" }
                },
                {
                  "from": "b.thing", "kind": "requires", "to": "a.thing",
                  "evidence": { "sourceKind": "documentation", "source": "docs", "confidence": "high" }
                }
              ]
            }
            """;

        DataFileException error = Assert.Throws<DataFileException>(() => CapabilityGraphLoader.Load(json));
        Assert.Contains("cycle", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A relation scoped to one vendor does not apply to a different one.</summary>
    [Fact]
    public void ScopedEdgesOnlyApplyToMatchingMachines()
    {
        var scope = new GraphScope(Vendors: ["Dell Inc."]);

        Assert.True(scope.Matches(Machines.DellIntelLaptop));
        Assert.False(scope.Matches(Machines.AsusAmdDesktop));
    }

    [Fact]
    public void AnOsBuildScopeIgnoresAnUnknownBuildRatherThanExcludingTheMachine()
    {
        var scope = new GraphScope(OsMinBuild: 26100);

        Assert.True(scope.Matches(MachineIdentity.Unknown with { OsBuild = 0 }));
        Assert.False(scope.Matches(Machines.AsusAmdDesktop with { OsBuild = 19045 }));
    }
}
