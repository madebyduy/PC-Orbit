using System.Reflection;
using PcOrbit.Adapters.Windows;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Tests;

/// <summary>
/// Ties the graph's <c>observable</c> flag to whether anything can actually read the node.
/// </summary>
/// <remarks>
/// <para>
/// The flag is load-bearing: <c>observable: false</c> tells the checkup not to raise findings from
/// a node and tells the health verdict not to count it as something we failed to read. So the way
/// it goes wrong is not a node wrongly marked false — it is a node left at the schema's <c>true</c>
/// default that no scanner has ever been taught to read. That node then reads Unknown on every
/// machine forever and is counted against every scan, and nothing anywhere says so.
/// </para>
/// <para>
/// Writing <c>observable: true</c> onto all thirty-odd nodes would not have caught that; it would
/// just have restated the default in thirty places. This does catch it, which is why it exists
/// instead (ADR 0004).
/// </para>
/// </remarks>
public sealed class ObservableCoverageTests
{
    /// <summary>
    /// Every capability id the Windows adapter declares.
    /// </summary>
    /// <remarks>
    /// Reflection deliberately: a property added there is picked up here without anybody
    /// remembering to update a list. Note what this does and does not prove — a declared id means
    /// the adapter <em>knows about</em> the capability, not that it can read it on any given
    /// machine. <c>firmware.rebar</c> is declared and always reads Unknown, on purpose.
    /// </remarks>
    private static IReadOnlySet<CapabilityId> ScannerCapabilities { get; } = new HashSet<CapabilityId>(
    [
        .. typeof(WindowsCapabilities)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(CapabilityId))
            .Select(p => (CapabilityId)p.GetValue(null)!),

        // The registry tweaks declare their own ids, because the table that names the key is also
        // the table that names the capability — keeping them together is what stops the two
        // drifting apart.
        .. PcOrbit.Adapters.Windows.Executors.RegistryTweaks.All.Select(t => t.Capability),
    ]);

    [Fact]
    public void EveryObservableNodeHasSomethingThatCanReadIt()
    {
        CapabilityGraph graph = ShippedData.Graph();

        List<string> orphans = [];

        foreach (CapabilityNode node in graph.Nodes)
        {
            if (!node.Observable)
            {
                continue;
            }

            // Workloads are derived from requires edges rather than read, and a constraint is
            // something the user confirms. Neither has a scanner and neither should.
            if (node.Kind is NodeKind.Workload or NodeKind.Constraint or NodeKind.VerificationRule)
            {
                continue;
            }

            if (!ScannerCapabilities.Contains(node.Id))
            {
                orphans.Add(node.Id.Value);
            }
        }

        Assert.True(
            orphans.Count == 0,
            $"These graph nodes are marked observable but no adapter declares a reader for them, so they "
            + $"will read Unknown on every machine and be counted against every scan: {string.Join(", ", orphans)}. "
            + "Either teach the scanner to read them, or set observable: false with the reason.");
    }

    /// <summary>
    /// The other direction: a capability the adapter reads that the graph does not declare would
    /// appear in a scan with no display name and no place in any plan.
    /// </summary>
    [Fact]
    public void EveryCapabilityTheScannerReadsIsDeclaredInTheGraph()
    {
        CapabilityGraph graph = ShippedData.Graph();

        List<string> undeclared =
        [
            .. ScannerCapabilities
                .Where(id => !graph.Contains(id))
                .Select(id => id.Value)
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            undeclared.Count == 0,
            $"The Windows adapter reads these, but the graph does not declare them, so they have no "
            + $"display name and cannot appear in a plan: {string.Join(", ", undeclared)}.");
    }
}
