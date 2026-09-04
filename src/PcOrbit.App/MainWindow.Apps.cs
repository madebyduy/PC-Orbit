using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Apps;
using PcOrbit.Core.Setup;

namespace PcOrbit.App;

// ---------------------------------------------------------------- row models

public sealed record EditionRow(string Name, string Detail, Brush RowBg);

/// <param name="Glyph">A tick or a warning triangle. The row's whole meaning is in it.</param>
public sealed record ReadinessRow(string Name, string Glyph, Brush Tone, Brush RowBg);

/// <summary>
/// The three pages behind Apps &amp; Windows.
/// </summary>
/// <remarks>
/// <para>
/// All three drive services that already existed and had no way in from the app: the winget
/// catalogue, Office through Microsoft's own deployment tool, and Windows itself — activation,
/// edition, and an in-place reinstall from an ISO. They sit in one section because they answer one
/// question, "what software is on this machine", at three scales.
/// </para>
/// <para>
/// The rule that shapes all of them is the same one that shapes the rest of the product: nothing
/// is claimed that was not read back. An installer's exit code is not evidence that anything
/// arrived, so every result here says whether the machine was asked again and agreed.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private OfficePlan _officePlan = new(OfficeProduct.Microsoft365Apps, "en-us", []);
    private string? _isoPath;
    private MediaContents? _media;

    /// <summary>
    /// The languages offered for Office.
    /// </summary>
    /// <remarks>
    /// A short list rather than the whole of Microsoft's, and an allowlist rather than a text box:
    /// the value goes into a configuration file that Microsoft's tool then acts on, so it is a
    /// parameter reaching a command and gets treated like every other one.
    /// </remarks>
    private static readonly (string Tag, string Label)[] OfficeLanguages =
    [
        ("vi-vn", "Tiếng Việt"),
        ("en-us", "English (United States)"),
        ("ja-jp", "日本語"),
        ("ko-kr", "한국어"),
        ("zh-cn", "简体中文"),
    ];

    private static readonly (OfficeProduct Product, string Key)[] OfficeProducts =
    [
        (OfficeProduct.Microsoft365Apps, "app.office.product.m365"),
        (OfficeProduct.HomeBusiness2024, "app.office.product.homeBusiness"),
        (OfficeProduct.ProPlus2024, "app.office.product.proPlus"),
    ];

    private void ApplyAppsPageStrings()
    {
        ApplyStoreStrings();

        OfficeStateTitle.Text = T("app.office.state");
        OfficeStateNote.Text = T("app.office.stateNote");
        OfficePlanTitle.Text = T("app.office.plan");
        OfficePlanHint.Text = T("app.office.planHint");
        OfficeProductLabel.Text = T("app.office.product");
        OfficeLanguageLabel.Text = T("app.office.language");
        OfficeExcludeLabel.Text = T("app.office.exclude");
        OfficeInstall.Content = T("app.office.install");

        LicenceTitle.Text = T("app.licence.title");
        LicenceNote.Text = T("app.licence.note");
        EditionTitle.Text = T("app.edition.title");
        EditionHint.Text = T("app.edition.hint");
        EditionKeyNote.Text = T("app.edition.keyNote");

        ReinstallTitle.Text = T("app.reinstall.title");
        ReinstallHint.Text = T("app.reinstall.hint");
        ReinstallPick.Content = T("app.reinstall.pick");
        ReinstallScopeLabel.Text = T("app.reinstall.scope");
        ReinstallStartBtn.Content = T("app.reinstall.start");

        if (_isoPath is null)
        {
            ReinstallFile.Text = T("app.reinstall.none");
        }

        BuildOfficeControls();
        BuildScopeChoices();
    }

    // ---------------------------------------------------------------- Office

    private void BuildOfficeControls()
    {
        OfficeProductBox.Items.Clear();

        foreach ((OfficeProduct product, string key) in OfficeProducts)
        {
            OfficeProductBox.Items.Add(new ComboBoxItem { Content = T(key), Tag = product });
        }

        OfficeProductBox.SelectedIndex = Array.FindIndex(OfficeProducts, p => p.Product == _officePlan.Product);

        OfficeLanguage.Items.Clear();

        foreach ((string tag, string label) in OfficeLanguages)
        {
            OfficeLanguage.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        }

        OfficeLanguage.SelectedIndex = Math.Max(
            0,
            Array.FindIndex(OfficeLanguages, l => string.Equals(l.Tag, _officePlan.Language, StringComparison.Ordinal)));

        OfficeExclude.Items.Clear();

        foreach (string component in OfficePlan.ExcludableApps)
        {
            var box = new CheckBox
            {
                Content = component,
                Tag = component,
                Margin = new Thickness(0, 0, 18, 8),
                IsChecked = _officePlan.ExcludedApps.Contains(component),
            };

            box.Checked += OnOfficePlanChanged;
            box.Unchecked += OnOfficePlanChanged;

            OfficeExclude.Items.Add(box);
        }

        RenderOfficeSummary();
    }

    private async Task RenderOfficeAsync()
    {
        if (_host is null)
        {
            return;
        }

        OfficeState state = await _host.Office.ReadAsync();

        OfficeStateValue.Text = state.Installed switch
        {
            true => T("app.office.installed"),
            false => T("app.office.notInstalled"),
            null => T("status.unknown"),
        };

        OfficeStateValue.Foreground = state.Installed == true ? B("Good") : B("Ink");

        string detail = string.Join(" · ", new[] { state.Product, state.Version }.Where(v => !string.IsNullOrWhiteSpace(v)));

        OfficeStateNote.Text = detail.Length > 0
            ? $"{detail} — {T("app.office.stateNote")}"
            : T("app.office.stateNote");
    }

    private void OnOfficePlanChanged(object sender, RoutedEventArgs e)
    {
        if (!Ready || OfficeProductBox is null)
        {
            return;
        }

        var product = OfficeProductBox.SelectedItem is ComboBoxItem { Tag: OfficeProduct p }
            ? p
            : OfficeProduct.Microsoft365Apps;

        string language = OfficeLanguage.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "en-us";

        List<string> excluded = [.. OfficeExclude.Items
            .OfType<CheckBox>()
            .Where(b => b.IsChecked == true)
            .Select(b => (string)b.Tag)];

        _officePlan = new OfficePlan(product, language, excluded);

        RenderOfficeSummary();
    }

    private void RenderOfficeSummary()
    {
        if (OfficeSummary is null)
        {
            return;
        }

        string productLabel = T(OfficeProducts.First(p => p.Product == _officePlan.Product).Key);

        string languageLabel = OfficeLanguages
            .FirstOrDefault(l => string.Equals(l.Tag, _officePlan.Language, StringComparison.Ordinal))
            .Label ?? _officePlan.Language;

        string excluded = _officePlan.ExcludedApps.Count == 0
            ? T("app.office.nothingExcluded")
            : T("app.office.excluding", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["apps"] = string.Join(", ", _officePlan.ExcludedApps),
            });

        OfficeSummary.Text = T("app.office.summary", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["product"] = productLabel,
            ["language"] = languageLabel,
            ["bits"] = _officePlan.SixtyFourBit ? "64" : "32",
            ["excluded"] = excluded,
        });
    }

    private async void OnOfficeInstall(object sender, RoutedEventArgs e)
    {
        if (_host is null)
        {
            return;
        }

        string productLabel = T(OfficeProducts.First(p => p.Product == _officePlan.Product).Key);

        if (MessageBox.Show(
                this,
                T("app.office.confirm", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["product"] = productLabel,
                }),
                T("app.title"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        OfficeInstall.IsEnabled = false;

        // Progress is shown on the page rather than behind a modal veil. This runs for tens of
        // minutes, and a dialog that owns the window for that long is indistinguishable from one
        // that has hung.
        var progress = new Progress<string>(stage => OfficeProgress.Text = OfficeStageText(stage));

        try
        {
            OfficeInstallResult result = await _host.Office.InstallAsync(_officePlan, progress);

            OfficeProgress.Text = result switch
            {
                { Completed: true } => T("app.office.completed"),

                // Which stage it stopped at, in words. The service's own stage token is a
                // developer's name for it and has no business on a page like this.
                { Problem: { } p } => T("app.office.failedAt", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stage"] = OfficeStageName(result.Stage),
                    ["problem"] = p,
                }),
                _ => T("app.office.failedAt", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stage"] = OfficeStageName(result.Stage),
                    ["problem"] = T("status.unknown"),
                }),
            };

            await RenderOfficeAsync();
        }
        finally
        {
            OfficeInstall.IsEnabled = true;
        }
    }

    /// <summary>
    /// What to show while the installer is running.
    /// </summary>
    /// <remarks>
    /// The whole install is a single blocking call, so in practice only <c>tool</c> is reported
    /// while it runs and the rest arrive at the end. That is why the <c>tool</c> line describes the
    /// entire run rather than the first step of it: a page that says "fetching the deployment tool"
    /// for forty minutes is not progress, it is a stuck screen. Unknown stages fall back to the
    /// same line rather than to a missing key.
    /// </remarks>
    private string OfficeStageText(string stage) => stage switch
    {
        "download" => T("app.office.stage.download"),
        "configure" => T("app.office.stage.configure"),
        "done" => T("app.office.completed"),
        _ => T("app.office.stage.tool"),
    };

    private string OfficeStageName(string stage) => stage switch
    {
        "download" => T("app.office.stageName.download"),
        "configure" => T("app.office.stageName.configure"),
        _ => T("app.office.stageName.tool"),
    };

    // ---------------------------------------------------------------- Windows itself

    private async Task RenderWindowsAsync()
    {
        if (_host is null)
        {
            return;
        }

        EditionOptions options = await _host.Editions.ReadAsync();

        LicenceStateValue.Text = options.Licence.State switch
        {
            LicenceState.Licensed => T("app.licence.licensed"),
            LicenceState.Grace => T("app.licence.grace"),
            LicenceState.Notification => T("app.licence.notification"),
            LicenceState.Unlicensed => T("app.licence.unlicensed"),
            _ => T("app.licence.unknown"),
        };

        LicenceStateValue.Foreground = options.Licence.State switch
        {
            LicenceState.Licensed => B("Good"),
            LicenceState.Unknown => B("Muted"),
            _ => B("Warn"),
        };

        LicenceNote.Text = options.Licence.NeedsAttentionBeforeChanging
            ? T("app.licence.warnBeforeChange") + " " + T("app.licence.note")
            : T("app.licence.note");

        EditionCurrent.Text = T("app.edition.current", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["edition"] = options.CurrentEdition,
        });

        if (options.Problem is { } problem)
        {
            EditionTargets.ItemsSource = new[]
            {
                new EditionRow(
                    T("app.edition.unreadable", new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["problem"] = problem,
                    }),
                    string.Empty,
                    Brushes.Transparent),
            };

            return;
        }

        EditionTargets.ItemsSource = options.Targets.Count == 0
            ? new[] { new EditionRow(T("app.edition.none"), string.Empty, Brushes.Transparent) }
            : [.. options.Targets.Select((t, i) => new EditionRow(t.DisplayName, T("app.edition.canBecome"), Stripe(i)))];
    }

    private void BuildScopeChoices()
    {
        if (ReinstallScopeBox is null)
        {
            return;
        }

        int selected = Math.Max(0, ReinstallScopeBox.SelectedIndex);

        ReinstallScopeBox.Items.Clear();
        ReinstallScopeBox.Items.Add(new ComboBoxItem
        {
            Content = T("app.reinstall.scope.everything"),
            Tag = ReinstallScope.KeepEverything,
        });
        ReinstallScopeBox.Items.Add(new ComboBoxItem
        {
            Content = T("app.reinstall.scope.filesOnly"),
            Tag = ReinstallScope.KeepFilesOnly,
        });

        ReinstallScopeBox.SelectedIndex = selected;
    }

    private async void OnPickIso(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Windows ISO|*.iso",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _isoPath = dialog.FileName;

        // The file's name, not its path. The user picked it a second ago and knows where it is;
        // a full path here would be the one piece of plumbing on an otherwise plain-language page.
        ReinstallFile.Text = System.IO.Path.GetFileName(_isoPath);
        ReinstallDetail.Visibility = Visibility.Collapsed;

        await BusyAsync2(T("app.reinstall.reading"), async () =>
        {
            _media = await _host!.Media.InspectAsync(_isoPath);

            if (!_media.IsWindowsMedia)
            {
                ReinstallFile.Text = T("app.reinstall.notWindows");
                return;
            }

            ReinstallMedia.Text = T("app.reinstall.media", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["build"] = _media.Build ?? T("status.unknown"),
                ["architecture"] = _media.Architecture ?? T("status.unknown"),
                ["count"] = _media.Editions.Count.ToString(CultureInfo.CurrentCulture),
            });

            ReinstallDetail.Visibility = Visibility.Visible;
            RenderReinstallChecks();
        });
    }

    private void OnReinstallScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Ready && _media is not null)
        {
            RenderReinstallChecks();
        }
    }

    /// <summary>
    /// What would stop this, and what the user should know before it starts.
    /// </summary>
    /// <remarks>
    /// Both are shown, not just the blockers. A warning that goes unmentioned until the machine is
    /// two restarts into a reinstall is not a warning.
    /// </remarks>
    private void RenderReinstallChecks()
    {
        if (_host is null || _media is null || _snapshot is null)
        {
            return;
        }

        var scope = ReinstallScopeBox.SelectedItem is ComboBoxItem { Tag: ReinstallScope s }
            ? s
            : ReinstallScope.KeepEverything;

        ReinstallReadiness readiness = _host.Media.Assess(_snapshot, _media, scope);

        List<ReadinessRow> rows =
        [
            .. readiness.Blockers.Select((b, i) => new ReadinessRow(b, GlyphOf("GlyphWarn"), B("Bad"), Stripe(i))),
            .. readiness.Warnings.Select((w, i) => new ReadinessRow(w, GlyphOf("GlyphInfo"), B("Warn"), Stripe(i + readiness.Blockers.Count))),
        ];

        if (rows.Count == 0)
        {
            rows.Add(new ReadinessRow(T("app.reinstall.ready"), GlyphOf("GlyphCheck"), B("Good"), Brushes.Transparent));
        }

        ReinstallChecks.ItemsSource = rows;
        ReinstallStartBtn.IsEnabled = readiness.CanProceed;
    }

    private async void OnReinstallStart(object sender, RoutedEventArgs e)
    {
        if (_host is null || _media is null || _snapshot is null || _isoPath is null)
        {
            return;
        }

        var scope = ReinstallScopeBox.SelectedItem is ComboBoxItem { Tag: ReinstallScope s }
            ? s
            : ReinstallScope.KeepEverything;

        ReinstallReadiness readiness = _host.Media.Assess(_snapshot, _media, scope);

        if (!readiness.CanProceed)
        {
            MessageBox.Show(
                this,
                T("app.reinstall.blocked") + "\n\n" + string.Join("\n", readiness.Blockers),
                T("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        string scopeLabel = T(scope == ReinstallScope.KeepEverything
            ? "app.reinstall.scope.everything"
            : "app.reinstall.scope.filesOnly");

        if (MessageBox.Show(
                this,
                T("app.reinstall.confirm", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["scope"] = scopeLabel,
                }),
                T("app.title"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        ReinstallStart result = await _host.Media.StartAsync(_isoPath, readiness);

        MessageBox.Show(
            this,
            result.Problem is { } problem
                ? T("app.reinstall.failed", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["problem"] = problem,
                })
                : T("app.reinstall.launched"),
            T("app.title"),
            MessageBoxButton.OK,
            result.Launched ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    /// <summary>
    /// A glyph character from the application's resources.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>G</c>, which fetches a <see cref="System.Windows.Media.Geometry"/> for a
    /// drawn icon. These are font characters, and the two are not interchangeable.
    /// </remarks>
    private static string GlyphOf(string key) => (string)Application.Current.Resources[key];

    /// <summary>The busy overlay, with the message already resolved.</summary>
    private async Task BusyAsync2(string message, Func<Task> work)
    {
        BusyText.Text = message;
        BusyOverlay.Visibility = Visibility.Visible;

        try
        {
            await work();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, T("app.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }
}
