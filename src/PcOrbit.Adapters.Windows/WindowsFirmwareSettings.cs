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
/// Registration is not availability. This was written against a Lenovo that has all four Lenovo
/// classes registered by its driver and returns <em>zero instances</em> from every one of them:
/// that provider only populates on the commercial ThinkPad and ThinkCentre lines. So "the classes
/// exist" and "the firmware will talk to us" are read as two separate facts, and a consumer model
/// is told plainly that its firmware does not expose this rather than being shown switches that
/// quietly do nothing.
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

        function Emit($vendor, $present, $password, $settings, $problem) {
            [pscustomobject]@{
                Vendor   = $vendor
                Present  = [bool] $present
                Password = [bool] $password
                Settings = @($settings)
                Problem  = $problem
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

            # Registered but silent is the consumer-model answer, and it is reported as such.
            Emit 'Lenovo' ($rows.Count -gt 0) $password $settings $null
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

            Emit 'HP' ($settings.Count -gt 0) $password $settings $null
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

            Emit 'Dell' ($settings.Count -gt 0) $false $settings $null
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
            return new FirmwareInterface(false, null, false, [], problem);
        }

        if (document is null)
        {
            return FirmwareInterface.None();
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            string? vendor = WindowsChangeSources.Text(root, "Vendor");
            bool present = WindowsChangeSources.Flag(root, "Present") ?? false;
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
                present,
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
