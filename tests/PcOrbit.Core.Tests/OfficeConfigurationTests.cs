using System.Xml.Linq;
using PcOrbit.Adapters.Windows;
using PcOrbit.Core.Apps;

namespace PcOrbit.Core.Tests;

/// <summary>
/// The Office deployment configuration: the document the installer obeys, and therefore the one
/// place here where getting the XML wrong changes what lands on the machine.
/// </summary>
public sealed class OfficeConfigurationTests
{
    private static readonly WindowsOfficeService Service = new();

    private static XElement Configure(OfficePlan plan) => XElement.Parse(Service.DescribeConfiguration(plan));

    private static OfficePlan Plan(
        OfficeProduct product = OfficeProduct.Microsoft365Apps,
        string language = "en-us",
        IReadOnlyList<string>? excluded = null,
        bool sixtyFour = true) =>
        new(product, language, excluded ?? [], sixtyFour);

    [Fact]
    public void TheConfigurationIsWellFormedXml()
    {
        // Parsing is the assertion: a document the installer cannot read is the failure mode that
        // matters, and it is invisible until setup.exe refuses it.
        XElement configuration = Configure(Plan());

        Assert.Equal("Configuration", configuration.Name.LocalName);
        Assert.NotNull(configuration.Element("Add"));
    }

    [Theory]
    [InlineData(OfficeProduct.Microsoft365Apps, "O365BusinessRetail")]
    [InlineData(OfficeProduct.HomeBusiness2024, "HomeBusiness2024Retail")]
    [InlineData(OfficeProduct.ProPlus2024, "ProPlus2024Volume")]
    public void EachProductCarriesMicrosoftsOwnIdentifier(OfficeProduct product, string expected)
    {
        XElement? element = Configure(Plan(product)).Element("Add")?.Element("Product");

        Assert.Equal(expected, element?.Attribute("ID")?.Value);
    }

    [Fact]
    public void TheLanguageIsWhatTheUserChose()
    {
        XElement? language = Configure(Plan(language: "vi-vn"))
            .Element("Add")?.Element("Product")?.Element("Language");

        Assert.Equal("vi-vn", language?.Attribute("ID")?.Value);
    }

    [Theory]
    [InlineData(true, "64")]
    [InlineData(false, "32")]
    public void TheArchitectureIsCarriedThrough(bool sixtyFour, string expected)
    {
        Assert.Equal(expected, Configure(Plan(sixtyFour: sixtyFour)).Element("Add")?.Attribute("OfficeClientEdition")?.Value);
    }

    /// <summary>
    /// The reason for using the deployment tool rather than a plain installer: a machine that will
    /// never run Access does not have to carry it.
    /// </summary>
    [Fact]
    public void ExcludedAppsBecomeExcludeAppElements()
    {
        XElement configuration = Configure(Plan(excluded: ["Access", "Publisher"]));

        string[] excluded =
        [
            .. configuration.Element("Add")!.Element("Product")!
                .Elements("ExcludeApp")
                .Select(e => e.Attribute("ID")!.Value),
        ];

        Assert.Equal(["Access", "Publisher"], excluded);
    }

    /// <summary>
    /// An app name the deployment tool does not know is dropped rather than written out. The
    /// document is the whole instruction the installer obeys, and it should carry nothing that did
    /// not come from the tool's own vocabulary.
    /// </summary>
    [Fact]
    public void AnAppTheToolDoesNotKnowIsNotWrittenIntoTheDocument()
    {
        XElement configuration = Configure(Plan(excluded: ["Access", "NotARealOfficeApp"]));

        string[] excluded =
        [
            .. configuration.Element("Add")!.Element("Product")!
                .Elements("ExcludeApp")
                .Select(e => e.Attribute("ID")!.Value),
        ];

        Assert.Equal(["Access"], excluded);
    }

    /// <summary>
    /// Built with an XML writer rather than by pasting strings, so a value carrying a quote or an
    /// angle bracket is escaped instead of closing a tag early.
    /// </summary>
    [Fact]
    public void AValueCarryingMarkupCannotRewriteTheDocument()
    {
        var plan = Plan(language: "\"/><Product ID=\"Evil\"><Language ID=\"x");

        XElement configuration = Configure(plan);

        // Still one product, and it is still the one that was asked for.
        XElement[] products = [.. configuration.Element("Add")!.Elements("Product")];

        Assert.Single(products);
        Assert.Equal("O365BusinessRetail", products[0].Attribute("ID")?.Value);
    }

    [Fact]
    public void TheInstallIsSilentAndSaysSoInTheDocumentTheUserWasShown()
    {
        XElement? display = Configure(Plan()).Element("Display");

        Assert.Equal("None", display?.Attribute("Level")?.Value);
        Assert.Equal("TRUE", display?.Attribute("AcceptEULA")?.Value);
    }
}
