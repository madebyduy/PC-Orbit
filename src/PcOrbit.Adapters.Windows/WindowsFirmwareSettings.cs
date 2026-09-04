using System.Text.Json;
using PcOrbit.Core.Firmware;
using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// Firmware settings through the manufacturer's own WMI interface.
/// </summary>
/// <remarks>
/// <para>
/// Three vendors publish one. Lenovo exposes <c>Lenovo_BiosSetting</c> with
/// <c>Lenovo_SetBiosSetting</c> and <c>Lenovo_SaveBiosSettings</c> under <c>root\WMI</c>; HP
/// exposes <c>HP_BIOSEnumeration</c> and <c>HP_BIOSSettingInterface</c> under
/// <c>root\HP\InstrumentedBIOS</c>; Dell exposes <c>DCIM_BIOSEnumeration</c> under
/// <c>root\dcim\sysman</c>, but only once Dell Command | Monitor has been installed, which is why
/// Dell is detected and reported rather than driven.
/// </para>
/// <para>
/// Registration is not availability, and neither is an empty answer. Every ACPI-WMI class under
/// <c>root\WMI</c> returns zero instances to a process without administrator rights — including
/// <c>MSAcpi_ThermalZoneTemperature</c>, which works on every machine ever made. The first version
/// of this read that emptiness as "your model does not have this feature" and told standard users
/// so, definitively, about hardware nobody had been able to ask about.
/// </para>
/// <para>
/// So elevation is established first and reported as part of the answer. Three separate facts: the
/// classes exist, we were allowed to enumerate them, and the firmware returned something. Only the
/// third is a statement about the machine.
/// </para>
/// <para>
/// Never by writing UEFI <c>Setup</c> variables at an offset (spec decision 18).
/// </para>
/// </remarks>
public sealed class WindowsFirmwareSettings(PowerShellRunner? powerShell = null) : IFirmwareSettings
{
    /// <summary>
    /// Enumerates whichever vendor interface this machine has.
    /// </summary>
    /// <remarks>
    /// Lenovo's <c>CurrentSetting</c> is one string, <c>"Name,Value"</c>, sometimes with a
    /// <c>";[Optional:A;B;C]"</c> tail listing what it will accept. Where that tail is absent,
    /// <c>Lenovo_GetBiosSelections</c> answers the same question, so both are tried — a setting
    /// whose accepted values cannot be established is returned with none, and a setting with no
    /// accepted values is not offered for writing.
    /// </remarks>
    private const string ReadScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        # Administrator rights decide whether an empty result means anything at all.
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $elevated = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)

        function Emit($vendor, $registered, $password, $settings, $problem) {
            [pscustomobject]@{
                Vendor     = $vendor
                Registered = [bool] $registered
                Elevated   = [bool] $elevated
                Password   = [bool] $password
                Settings   = @($settings)
                Problem    = $problem
            } | ConvertTo-Json -Depth 4 -Compress
            exit 0
        }

        # ---------------------------------------------------------------- Lenovo
        $lenovoClass = Get-CimClass -Namespace root\WMI -ClassName Lenovo_BiosSetting -ErrorAction SilentlyContinue

        if ($lenovoClass) {
            $rows = @(Get-CimInstance -Namespace root\WMI -ClassName Lenovo_BiosSetting -ErrorAction SilentlyContinue |
                      Where-Object { $_.CurrentSetting })

            $password = $false
            $pw = Get-CimInstance -Namespace root\WMI -ClassName Lenovo_BiosPasswordSettings -ErrorAction SilentlyContinue
            if ($pw -and $pw.PasswordState -and [int] $pw.PasswordState -ne 0) { $password = $true }

            $settings = @()

            foreach ($row in $rows) {
                # "Name,Value" and sometimes ";[Optional:A;B;C]" after it.
                $text = [string] $row.CurrentSetting
                $head = ($text -split ';')[0]
                $comma = $head.IndexOf(',')
                if ($comma -lt 1) { continue }

                $name = $head.Substring(0, $comma).Trim()
                $value = $head.Substring($comma + 1).Trim()

                $options = @()
                if ($text -match '\[Optional:([^\]]+)\]') {
                    $options = @($matches[1] -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
                }

                if ($options.Count -eq 0) {
                    $sel = Get-CimInstance -Namespace root\WMI -ClassName Lenovo_GetBiosSelections -ErrorAction SilentlyContinue |
                           Invoke-CimMethod -MethodName GetBiosSelections -Arguments @{ Item = $name } -ErrorAction SilentlyContinue
                    if ($sel -and $sel.Selections) {
                        $options = @(([string] $sel.Selections) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
                    }
                }

                $settings += [pscustomobject]@{ Name = $name; Value = $value; Options = @($options) }
            }

            # Registered, not "present". What an empty list means is the caller's to decide, and
            # it cannot be decided here without knowing whether this process was allowed to ask.
            Emit 'Lenovo' $true $password $settings $null
        }

        # ---------------------------------------------------------------- HP
        $hp = Get-CimInstance -Namespace root\HP\InstrumentedBIOS -ClassName HP_BIOSEnumeration -ErrorAction SilentlyContinue

        if ($hp) {
            $settings = @()

            foreach ($row in @($hp)) {
                if (-not $row.Name) { continue }
                $settings += [pscustomobject]@{
                    Name    = [string] $row.Name
                    Value   = [string] $row.CurrentValue
                    Options = @($row.PossibleValues | ForEach-Object { [string] $_ } | Where-Object { $_ })
                }
            }

            $password = $false
            $pw = Get-CimInstance -Namespace root\HP\InstrumentedBIOS -ClassName HP_BIOSSetting -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -eq 'Setup Password' }
            if ($pw -and $pw.IsSet) { $password = $true }

            Emit 'HP' $true $password $settings $null
        }

        # ---------------------------------------------------------------- Dell
        $dell = Get-CimClass -Namespace root\dcim\sysman -ClassName DCIM_BIOSEnumeration -ErrorAction SilentlyContinue

        if ($dell) {
            $rows = @(Get-CimInstance -Namespace root\dcim\sysman -ClassName DCIM_BIOSEnumeration -ErrorAction SilentlyContinue)
            $settings = @()

            foreach ($row in $rows) {
                if (-not $row.AttributeName) { continue }
                $settings += [pscustomobject]@{
                    Name    = [string] $row.AttributeName
                    Value   = [string] (@($row.CurrentValue) -join ',')
                    Options = @($row.PossibleValues | ForEach-Object { [string] $_ } | Where-Object { $_ })
                }
            }

            Emit 'Dell' $true $false $settings $null
        }

        Emit $null $false $false @() $null
        """;

    /// <summary>
    /// Writes one setting, saves it, and says only what the vendor call said.
    /// </summary>
    /// <remarks>
    /// Lenovo takes the change and the save as two calls — <c>SetBiosSetting("Name,Value")</c> then
    /// <c>SaveBiosSettings()</c> — and a set that is never saved is discarded at the next boot,
    /// which would look exactly like a change that worked and then reverted. Both are required to
    /// return <c>Success</c>; anything else is passed back verbatim rather than summarised, because
    /// "Access Denied" and "Invalid Parameter" mean very different things to whoever reads it.
    /// </remarks>
    private const string WriteScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        $vendor   = $env:PCORBIT_ARG0
        $name     = $env:PCORBIT_ARG1
        $value    = $env:PCORBIT_ARG2
        $password = $env:PCORBIT_ARG3

        $ok = $false
        $problem = $null

        try {
            if ($vendor -eq 'Lenovo') {
                $suffix = if ($password) { ",$password,ascii,us" } else { "" }

                $set = Get-CimInstance -Namespace root\WMI -ClassName Lenovo_SetBiosSetting |
                       Invoke-CimMethod -MethodName SetBiosSetting -Arguments @{ parameter = "$name,$value$suffix" }

                if ($set.return -ne 'Success') {
                    throw "the firmware refused the change: $($set.return)"
                }

                $saveArg = if ($password) { "$password,ascii,us" } else { "" }

                $save = Get-CimInstance -Namespace root\WMI -ClassName Lenovo_SaveBiosSettings |
                        Invoke-CimMethod -MethodName SaveBiosSettings -Arguments @{ parameter = $saveArg }

                if ($save.return -ne 'Success') {
                    throw "the change was accepted but not saved, so it would be discarded at the next start: $($save.return)"
                }

                $ok = $true
            }
            elseif ($vendor -eq 'HP') {
                $iface = Get-CimInstance -Namespace root\HP\InstrumentedBIOS -ClassName HP_BIOSSettingInterface

                $args = @{ Name = $name; Value = $value; Password = if ($password) { "<utf-16/>$password" } else { "" } }
                $set = $iface | Invoke-CimMethod -MethodName SetBIOSSetting -Arguments $args

                if ([int] $set.Return -ne 0) {
                    throw "the firmware refused the change, code $($set.Return)"
                }

                $ok = $true
            }
            else {
                throw "this build does not write firmware settings on $vendor hardware."
            }
        }
        catch {
            $problem = $_.Exception.Message
        }
        finally {
            # Not stored, not logged, not put in evidence. Gone as soon as the call is over.
            $password = $null
            Remove-Variable password -ErrorAction SilentlyContinue
        }

        [pscustomobject]@{ Applied = [bool] $ok; Problem = $problem } | ConvertTo-Json -Depth 3 -Compress
        exit 0
        """;

    private readonly PowerShellRunner _powerShell = powerShell ?? PowerShellRunner.Default;

    public async Task<FirmwareInterface> ReadAsync(CancellationToken cancellationToken = default)
    {
        (JsonDocument? document, string? problem) = await WindowsChangeSources
            .ReadJsonAsync(_powerShell, ReadScript, cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null)
        {
            return new FirmwareInterface(FirmwareAvailability.NoInterface, null, false, [], problem);
        }

        if (document is null)
        {
            return FirmwareInterface.None();
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            string? vendor = WindowsChangeSources.Text(root, "Vendor");
            bool registered = WindowsChangeSources.Flag(root, "Registered") ?? false;
            bool elevated = WindowsChangeSources.Flag(root, "Elevated") ?? false;
            bool password = WindowsChangeSources.Flag(root, "Password") ?? false;

            List<FirmwareSetting> settings = [];

            if (root.TryGetProperty("Settings", out JsonElement list))
            {
                foreach (JsonElement row in Rows(list))
                {
                    if (WindowsChangeSources.Text(row, "Name") is not { Length: > 0 } name)
                    {
                        continue;
                    }

                    settings.Add(new FirmwareSetting(
                        name,
                        WindowsChangeSources.Text(row, "Value"),
                        [.. Options(row)],
                        FirmwareRiskTable.For(name),
                        new Evidence(
                            EvidenceSourceKind.VendorApi,
                            $"{vendor}'s own firmware interface, over WMI",
                            Confidence.High,
                            Query: VendorQuery(vendor),
                            RawResult: WindowsChangeSources.Text(row, "Value"))));
                }
            }

            return new FirmwareInterface(
                Availability(registered, elevated, settings.Count, vendor),
                vendor,
                password,
                [.. settings.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)],
                WindowsChangeSources.Text(root, "Problem"));
        }
    }

    public async Task<FirmwareWriteResult> SetAsync(
        FirmwareSetting setting,
        string value,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        // The allowlist, and it is the firmware's own. Both halves come from the enumeration this
        // caller was handed, so nothing it invents can reach the vendor call (ADR 0005).
        if (setting.Risk == FirmwareRisk.Refused)
        {
            return new FirmwareWriteResult(setting.Name, value, false, false, false,
                "this product does not write that setting at any confirmation level.");
        }

        if (!setting.Accepts(value))
        {
            return new FirmwareWriteResult(setting.Name, value, false, false, false,
                $"the firmware did not offer '{value}' as a value for {setting.Name}.");
        }

        FirmwareInterface before = await ReadAsync(cancellationToken).ConfigureAwait(false);

        if (before.Vendor is not { Length: > 0 } vendor)
        {
            return new FirmwareWriteResult(setting.Name, value, false, false, false,
                "no manufacturer firmware interface answered on this machine.");
        }

        (JsonDocument? document, string? problem) = await WindowsChangeSources
            .ReadJsonAsync(
                _powerShell,
                WriteScript,
                [vendor, setting.Name, value, password ?? string.Empty],
                cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null)
        {
            return new FirmwareWriteResult(setting.Name, value, false, false, false, problem);
        }

        bool applied;
        string? reported;

        using (document)
        {
            applied = document is not null
                && (WindowsChangeSources.Flag(document.RootElement, "Applied") ?? false);

            reported = document is null ? null : WindowsChangeSources.Text(document.RootElement, "Problem");
        }

        if (!applied)
        {
            return new FirmwareWriteResult(setting.Name, value, false, false, false, reported);
        }

        // The vendor call said Success. That is a claim, so the firmware is asked again — and the
        // answer decides what this reports (spec 6.4). Firmware records the value now and acts on
        // it at the next start, so a verified write still needs a restart to mean anything.
        FirmwareInterface after = await ReadAsync(cancellationToken).ConfigureAwait(false);

        bool verified = after.Settings.Any(s =>
            string.Equals(s.Name, setting.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Current, value, StringComparison.OrdinalIgnoreCase));

        return new FirmwareWriteResult(setting.Name, value, true, verified, RestartRequired: true, reported);
    }

    /// <summary>
    /// What an answer of "nothing" actually means.
    /// </summary>
    /// <remarks>
    /// The whole point of this method is the middle case. Registered classes returning nothing to a
    /// process that was never allowed to enumerate them says nothing about the firmware, and the
    /// first version of this reported it as a settled fact about the hardware. It is
    /// <see cref="FirmwareAvailability.NeedsElevation"/>, and the answer to it is a button, not a
    /// menu path.
    /// </remarks>
    private static FirmwareAvailability Availability(bool registered, bool elevated, int settings, string? vendor)
    {
        if (settings > 0)
        {
            return FirmwareAvailability.Available;
        }

        if (!registered)
        {
            return FirmwareAvailability.NoInterface;
        }

        if (!elevated)
        {
            return FirmwareAvailability.NeedsElevation;
        }

        // Dell registers the namespace through Command | Monitor. Without it the classes are absent
        // entirely, so reaching here with Dell named means something odder — treat it as the tool
        // being the missing piece, which is the actionable reading.
        return vendor == "Dell" ? FirmwareAvailability.NeedsVendorTool : FirmwareAvailability.ModelDoesNotImplement;
    }

    private static string VendorQuery(string? vendor) => vendor switch
    {
        "Lenovo" => @"root\WMI:Lenovo_BiosSetting",
        "HP" => @"root\HP\InstrumentedBIOS:HP_BIOSEnumeration",
        "Dell" => @"root\dcim\sysman:DCIM_BIOSEnumeration",
        _ => "(no vendor interface)",
    };

    private static IEnumerable<JsonElement> Rows(JsonElement list) =>
        list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [list];

    private static IEnumerable<string> Options(JsonElement row)
    {
        if (!row.TryGetProperty("Options", out JsonElement options))
        {
            yield break;
        }

        foreach (JsonElement option in Rows(options))
        {
            if (option.ValueKind == JsonValueKind.String && option.GetString() is { Length: > 0 } value)
            {
                yield return value;
            }
        }
    }
}
