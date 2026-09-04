using System.Globalization;
using System.Text.Json;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// Reads the driver for every device Windows knows about, plus its problem code.
/// </summary>
/// <remarks>
/// <c>Win32_PnPSignedDriver</c> for the driver facts and <c>Win32_PnPEntity.ConfigManagerErrorCode</c>
/// for whether the device is actually working — joined on the device id, because the two live in
/// different classes and the second is the only one that constitutes a fault. Both read without
/// elevation.
/// </remarks>
public sealed class WindowsDriverInventory(PowerShellRunner? powerShell = null) : IDriverInventory
{
    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        # ConfigManagerErrorCode is Windows' own verdict on the device: 0 is working, anything
        # else is a fault it is already reporting in Device Manager.
        $problems = @{}
        Get-CimInstance -ClassName Win32_PnPEntity |
            Where-Object { $_.ConfigManagerErrorCode -ne $null -and $_.ConfigManagerErrorCode -ne 0 } |
            ForEach-Object { if ($_.PNPDeviceID) { $problems[$_.PNPDeviceID] = [int] $_.ConfigManagerErrorCode } }

        $drivers = Get-CimInstance -ClassName Win32_PnPSignedDriver |
            Where-Object { $_.DeviceName -and $_.DriverVersion }

        if (-not $drivers) { '[]'; exit 0 }

        $drivers | ForEach-Object {
            [pscustomobject]@{
                DeviceName   = [string] $_.DeviceName
                Manufacturer = [string] $_.Manufacturer
                Version      = [string] $_.DriverVersion
                Date         = if ($_.DriverDate) { $_.DriverDate.ToString('yyyy-MM-dd') } else { $null }
                Provider     = [string] $_.DriverProviderName
                Signed       = if ($_.IsSigned -ne $null) { [bool] $_.IsSigned } else { $null }
                Class        = [string] $_.DeviceClass
                ProblemCode  = if ($_.DeviceID -and $problems.ContainsKey($_.DeviceID)) { $problems[$_.DeviceID] } else { $null }
            }
        } | ConvertTo-Json -Depth 3 -Compress
        """;

    private readonly PowerShellRunner _powerShell = powerShell ?? PowerShellRunner.Default;

    public async Task<DriverInventoryResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        (JsonDocument? document, string? problem) = await WindowsChangeSources
            .ReadJsonAsync(_powerShell, Script, cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null)
        {
            return new DriverInventoryResult(
                [],
                $"the driver list could not be read, so this is not evidence that every device is "
                + $"working: {problem}");
        }

        if (document is null)
        {
            return DriverInventoryResult.Empty;
        }

        using (document)
        {
            List<DriverEntry> drivers = [];

            foreach (JsonElement row in WindowsChangeSources.Rows(document))
            {
                string? name = WindowsChangeSources.Text(row, "DeviceName");

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                drivers.Add(new DriverEntry(
                    name,
                    WindowsChangeSources.Text(row, "Manufacturer"),
                    WindowsChangeSources.Text(row, "Version"),
                    ParseDate(WindowsChangeSources.Text(row, "Date")),
                    WindowsChangeSources.Text(row, "Provider"),
                    IsSigned(row),
                    WindowsChangeSources.Number(row, "ProblemCode"),
                    WindowsChangeSources.Text(row, "Class"),
                    new Evidence(
                        EvidenceSourceKind.Wmi,
                        "Win32_PnPSignedDriver, joined to Win32_PnPEntity.ConfigManagerErrorCode",
                        Confidence.High,
                        Query: "Win32_PnPSignedDriver")));
            }

            return new DriverInventoryResult(
            [
                // Faulty devices first: they are the only entries that mean anything is wrong.
                .. drivers
                    .OrderByDescending(d => d.HasProblem)
                    .ThenBy(d => d.DeviceName, StringComparer.CurrentCultureIgnoreCase),
            ]);
        }
    }

    private static bool? IsSigned(JsonElement row) =>
        row.TryGetProperty("Signed", out JsonElement value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            ? date
            : null;
}
