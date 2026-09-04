using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcOrbit.Core.Apps;

namespace PcOrbit.App;

/// <summary>
/// One product in the store. Mutable and observable, because a card changes while you watch it:
/// ticked, then "Waiting…", then "Installing…", then a verdict.
/// </summary>
public sealed class AppCard(CatalogApp app) : INotifyPropertyChanged
{
    private bool _selected;
    private bool _busy;
    private bool? _installed;
    private string _state = string.Empty;
    private string _actionLabel = string.Empty;
    private Brush _tone = Brushes.Gray;
    private ImageSource? _icon;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CatalogApp App { get; } = app;

    public string Id => App.Id;

    public string Name => App.Name;

    public string Publisher => App.Publisher;

    public string Description { get; init; } = string.Empty;

    public string Glyph { get; init; } = string.Empty;

    public Brush Accent { get; init; } = Brushes.Gray;

    public Brush Tile { get; init; } = Brushes.LightGray;

    /// <summary>Whether the service can act at all — false without winget, and every button is off.</summary>
    public bool Available { get; set; }

    public bool? Installed
    {
        get => _installed;
        set => Set(ref _installed, value, nameof(Installed), nameof(CanAct));
    }

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value, nameof(Selected));
    }

    public bool Busy
    {
        get => _busy;
        set => Set(ref _busy, value, nameof(Busy), nameof(CanAct), nameof(CanSelect));
    }

    public string State
    {
        get => _state;
        set => Set(ref _state, value, nameof(State));
    }

    public string ActionLabel
    {
        get => _actionLabel;
        set => Set(ref _actionLabel, value, nameof(ActionLabel));
    }

    public Brush Tone
    {
        get => _tone;
        set => Set(ref _tone, value, nameof(Tone));
    }

    public ImageSource? Icon
    {
        get => _icon;
        set => Set(ref _icon, value, nameof(Icon), nameof(GlyphVisible));
    }

    public Visibility GlyphVisible => Icon is null ? Visibility.Visible : Visibility.Collapsed;

    public bool CanAct => Available && !Busy;

    public bool CanSelect => Available && !Busy;

    private void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;

        foreach (string name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

public sealed record AppGroupView(string Key, string Name, string Count, string SelectLabel, IReadOnlyList<AppCard> Apps);

/// <summary>
/// The store: cards with real icons, tick several, install them together.
/// </summary>
/// <remarks>
/// <para>
/// Installs run up to <see cref="ParallelInstalls"/> at a time. winget itself is happy to run
/// several times over; what serialises is Windows Installer, which allows one MSI at a time and
/// makes the others wait. So MSI-based packages queue on their own and everything else — most
/// browsers, editors and runtimes ship EXE or MSIX installers — genuinely overlaps. Three is the
/// number where the downloads saturate a typical connection without the machine becoming unusable
/// while it happens.
/// </para>
/// <para>
/// Every verdict on a card comes from asking the machine again, never from the installer's exit
/// code (spec 6.4). And the catalogue is still the allowlist: "Reinstall from list" reads ids from
/// a file and installs only those that are in this catalogue, whatever else the file says.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private const int ParallelInstalls = 3;

    private readonly List<AppCard> _appCards = [];
    private InstalledApps _installedApps = InstalledApps.Unknown("not read yet");
    private bool _appsAvailable;

    private void ApplyStoreStrings()
    {
        AppsSearch.Tag = T("app.apps.search");
        AppsSelectMissing.Content = T("app.apps.selectMissing");
        AppsSelectAll.Content = T("app.apps.selectAll");
        AppsClear.Content = T("app.apps.clear");
        AppsBackup.Content = T("app.apps.backup");
        AppsRestore.Content = T("app.apps.restore");
        AppsHint.Text = T("app.apps.parallelNote", Args(("count", N(ParallelInstalls))));
        AppsNoMatch.Text = T("app.apps.noMatch");
        UpdateSelectionButtons();
    }

    // ---------------------------------------------------------------- loading

    private async Task RenderAppsAsync()
    {
        if (_host is null)
        {
            return;
        }

        _appsAvailable = await _host.AppService.IsAvailableAsync();

        _installedApps = _appsAvailable
            ? await _host.AppService.InstalledAsync()
            : InstalledApps.Unknown(T("app.apps.unavailable"));

        string? problem = !_appsAvailable
            ? T("app.apps.unavailable")
            : _installedApps.IsKnown ? null : T("app.apps.unreadable");

        AppsProblem.Text = problem ?? string.Empty;
        AppsProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;

        if (_appCards.Count == 0)
        {
            foreach (CatalogApp app in _host.Apps.All)
            {
                (Brush tint, Brush ink) = Tone(app.CategoryKey);

                _appCards.Add(new AppCard(app)
                {
                    Description = T(app.DescriptionKey),
                    Glyph = GlyphOf(CategoryGlyph(app.CategoryKey)),
                    Accent = ink,
                    Tile = tint,
                });
            }
        }

        foreach (AppCard card in _appCards)
        {
            card.Available = _appsAvailable;
            RefreshCardState(card);
        }

        ApplyAppsFilter();

        // Icons after the cards are on screen, not before: the first paint should not wait on a
        // shell call per installed product and a network round-trip per absent one.
        _ = LoadIconsAsync();
    }

    private void RefreshCardState(AppCard card)
    {
        card.Installed = _installedApps.IsKnown ? _installedApps.Ids.Contains(card.Id) : null;

        string? foundAs = _installedApps.NameOnThisMachine(card.Id);

        card.State = card.Installed switch
        {
            true when foundAs is not null && !foundAs.Equals(card.Name, StringComparison.Ordinal) =>
                T("app.apps.installedAs", Args(("name", foundAs))),
            true => T("app.apps.isInstalled"),
            false => T("app.apps.notInstalled"),
            null => T("app.apps.unknownState"),
        };

        card.Tone = card.Installed == true ? B("Good") : B("Muted");
        card.ActionLabel = card.Installed == true ? T("app.apps.remove") : T("app.apps.install");
    }

    private async Task LoadIconsAsync()
    {
        foreach (AppCard card in _appCards.ToList())
        {
            if (card.Icon is not null)
            {
                continue;
            }

            ImageSource? icon = await AppIcons.ForAsync(card.App, _installedApps.IconPathOf(card.Id));

            if (icon is not null)
            {
                card.Icon = icon;
            }
        }
    }

    // ---------------------------------------------------------------- search + selection

    private void OnAppsSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (Ready && AppGroups is not null)
        {
            ApplyAppsFilter();
        }
    }

    private void ApplyAppsFilter()
    {
        if (_host is null)
        {
            return;
        }

        string[] words = AppsSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        bool Matches(AppCard c) => words.All(w =>
            c.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
            || c.Publisher.Contains(w, StringComparison.OrdinalIgnoreCase)
            || c.Description.Contains(w, StringComparison.OrdinalIgnoreCase));

        List<AppGroupView> groups = [];

        foreach (string category in _host.Apps.Categories)
        {
            List<AppCard> cards = [.. _appCards.Where(c => c.App.CategoryKey == category && Matches(c))];

            if (cards.Count > 0)
            {
                groups.Add(new AppGroupView(
                    category,
                    T(category),
                    N(cards.Count),
                    T("app.apps.selectGroup"),
                    cards));
            }
        }

        AppGroups.ItemsSource = groups;
        AppsNoMatch.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAppSelectionChanged(object sender, RoutedEventArgs e) => UpdateSelectionButtons();

    private void OnSelectMissing(object sender, RoutedEventArgs e)
    {
        foreach (AppCard card in _appCards)
        {
            card.Selected = card.CanSelect && card.Installed == false;
        }

        UpdateSelectionButtons();
    }

    private void OnSelectAllApps(object sender, RoutedEventArgs e)
    {
        foreach (AppCard card in _appCards)
        {
            card.Selected = card.CanSelect;
        }

        UpdateSelectionButtons();
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        foreach (AppCard card in _appCards)
        {
            card.Selected = false;
        }

        UpdateSelectionButtons();
    }

    private void OnSelectGroup(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string category })
        {
            return;
        }

        foreach (AppCard card in _appCards.Where(c => c.App.CategoryKey == category))
        {
            card.Selected = card.CanSelect;
        }

        UpdateSelectionButtons();
    }

    private void UpdateSelectionButtons()
    {
        int toInstall = _appCards.Count(c => c.Selected && c.Installed != true);
        int toRemove = _appCards.Count(c => c.Selected && c.Installed == true);

        AppsInstallSelected.Content = T("app.apps.installSelected", Args(("count", N(toInstall))));
        AppsRemoveSelected.Content = T("app.apps.removeSelected", Args(("count", N(toRemove))));
        AppsInstallSelected.IsEnabled = _appsAvailable && toInstall > 0;
        AppsRemoveSelected.IsEnabled = _appsAvailable && toRemove > 0;
    }

    // ---------------------------------------------------------------- acting

    private async void OnAppAction(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        AppCard? card = _appCards.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

        if (card is null)
        {
            return;
        }

        bool remove = card.Installed == true;

        if (!ConfirmBatch([card], remove))
        {
            return;
        }

        await RunBatchAsync([card], remove);
    }

    private async void OnInstallSelected(object sender, RoutedEventArgs e)
    {
        List<AppCard> cards = [.. _appCards.Where(c => c.Selected && c.Installed != true)];

        if (cards.Count > 0 && ConfirmBatch(cards, remove: false))
        {
            await RunBatchAsync(cards, remove: false);
        }
    }

    private async void OnRemoveSelected(object sender, RoutedEventArgs e)
    {
        List<AppCard> cards = [.. _appCards.Where(c => c.Selected && c.Installed == true)];

        if (cards.Count > 0 && ConfirmBatch(cards, remove: true))
        {
            await RunBatchAsync(cards, remove: true);
        }
    }

    private bool ConfirmBatch(IReadOnlyList<AppCard> cards, bool remove)
    {
        string names = string.Join("\n", cards.Select(c => "•  " + c.Name));

        return MessageBox.Show(
            this,
            T(remove ? "app.apps.confirmBatchRemove" : "app.apps.confirmBatchInstall", Args(
                ("count", N(cards.Count)),
                ("names", names),
                ("parallel", N(ParallelInstalls)))),
            T("app.title"),
            MessageBoxButton.OKCancel,
            remove ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.OK;
    }

    /// <summary>
    /// Runs the batch, several at a time, and lets each card report on itself.
    /// </summary>
    /// <remarks>
    /// The semaphore is the whole scheduling policy. Each install is its own winget process, and the
    /// cards flip from Waiting to Installing as they acquire a slot, so the user watches the queue
    /// drain rather than a spinner. After the last one the installed set is read again from the
    /// machine and every card is re-judged from that — a card's verdict is never the installer's
    /// word for it.
    /// </remarks>
    private async Task RunBatchAsync(IReadOnlyList<AppCard> cards, bool remove)
    {
        if (_host is null)
        {
            return;
        }

        foreach (AppCard card in cards)
        {
            card.Busy = true;
            card.Selected = false;
            card.State = T("app.apps.queued");
            card.Tone = B("Muted");
        }

        UpdateSelectionButtons();

        using var slots = new SemaphoreSlim(ParallelInstalls);
        int ok = 0;
        int failed = 0;

        IEnumerable<Task> work = cards.Select(async card =>
        {
            await slots.WaitAsync();

            try
            {
                card.State = T(remove ? "app.apps.removing" : "app.apps.installing");
                card.Tone = B("Accent");

                AppChangeResult result = remove
                    ? await _host.AppService.UninstallAsync(card.Id)
                    : await _host.AppService.InstallAsync(card.Id);

                switch (result)
                {
                    case { Problem: not null }:
                        Interlocked.Increment(ref failed);
                        card.State = T("app.apps.failedShort") + " · " + result.Problem;
                        card.Tone = B("Bad");
                        break;

                    case { Verified: true }:
                        Interlocked.Increment(ref ok);
                        card.State = T(remove ? "app.apps.removedShort" : "app.apps.doneShort");
                        card.Tone = B("Good");
                        AppIcons.Forget(card.Id);
                        break;

                    default:
                        Interlocked.Increment(ref failed);
                        card.State = T("app.apps.unverifiedShort");
                        card.Tone = B("Warn");
                        break;
                }
            }
            finally
            {
                slots.Release();
            }
        });

        await Task.WhenAll(work);

        // The machine's word, not the installers'.
        _installedApps = await _host.AppService.InstalledAsync();

        foreach (AppCard card in cards)
        {
            card.Busy = false;
            RefreshCardState(card);
            card.Icon = null;
        }

        UpdateSelectionButtons();
        _ = LoadIconsAsync();

        if (cards.Count > 1)
        {
            MessageBox.Show(
                this,
                T("app.apps.batchDone", Args(("ok", N(ok)), ("failed", N(failed)))),
                T("app.title"),
                MessageBoxButton.OK,
                failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }

    // ---------------------------------------------------------------- backup and restore

    private sealed record AppListFile(string Kind, int Version, IReadOnlyList<string> Ids);

    private static readonly JsonSerializerOptions ListWriteOptions = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions ListReadOptions = new() { PropertyNameCaseInsensitive = true };

    private void OnBackupApps(object sender, RoutedEventArgs e)
    {
        List<string> ids = [.. _appCards.Where(c => c.Installed == true).Select(c => c.Id)];

        if (ids.Count == 0)
        {
            MessageBox.Show(this, T("app.apps.backupNothing"), T("app.title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PC Orbit app list|*.pcorbit-apps.json",
            FileName = "my-apps.pcorbit-apps.json",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(
                dialog.FileName,
                JsonSerializer.Serialize(new AppListFile("pcorbit-apps", 1, ids), ListWriteOptions));

            MessageBox.Show(
                this,
                T("app.apps.backupDone", Args(("count", N(ids.Count)))),
                T("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, T("app.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Selects, from a saved list, everything in this catalogue that is not installed here.
    /// </summary>
    /// <remarks>
    /// Selects rather than installs. The file might be from another PC with a different idea of
    /// what belongs on this one, so the person gets to look at what was ticked before pressing the
    /// button. Ids outside the catalogue are dropped in silence-with-a-sentence: the message says
    /// they were ignored, because this product only installs what it ships.
    /// </remarks>
    private void OnRestoreApps(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PC Orbit app list|*.pcorbit-apps.json|JSON|*.json",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        AppListFile? list;

        try
        {
            list = JsonSerializer.Deserialize<AppListFile>(File.ReadAllText(dialog.FileName), ListReadOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            list = null;
        }

        if (list is not { Kind: "pcorbit-apps" })
        {
            MessageBox.Show(this, T("app.apps.restoreBad"), T("app.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        HashSet<string> wanted = new(list.Ids, StringComparer.OrdinalIgnoreCase);
        int selected = 0;

        foreach (AppCard card in _appCards)
        {
            card.Selected = wanted.Contains(card.Id) && card.Installed == false && card.CanSelect;
            selected += card.Selected ? 1 : 0;
        }

        UpdateSelectionButtons();

        MessageBox.Show(
            this,
            selected == 0 ? T("app.apps.restoreNone") : T("app.apps.restorePlan", Args(("count", N(selected)))),
            T("app.title"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // ---------------------------------------------------------------- category look

    /// <summary>
    /// The icon, and the two colours around it, for a catalogue category. Shown until — and instead
    /// of — a real logo: the fallback for a product with nothing on disk and no site icon.
    /// </summary>
    private static string CategoryGlyph(string categoryKey) => categoryKey switch
    {
        "apps.category.browsers" => "GlyphBrowser",
        "apps.category.media" => "GlyphMedia",
        "apps.category.utilities" => "GlyphUtility",
        "apps.category.communication" => "GlyphChat",
        "apps.category.development" => "GlyphCode",
        "apps.category.security" => "GlyphSecurity",
        _ => "GlyphPackage",
    };

    private static (Brush Tint, Brush Ink) Tone(string categoryKey)
    {
        (string tint, string ink) = categoryKey switch
        {
            "apps.category.browsers" => ("#DBEAFE", "#1D4ED8"),
            "apps.category.media" => ("#FCE7F3", "#BE185D"),
            "apps.category.utilities" => ("#E0F2FE", "#0369A1"),
            "apps.category.communication" => ("#EDE9FE", "#6D28D9"),
            "apps.category.development" => ("#DCFCE7", "#15803D"),
            "apps.category.security" => ("#FEF3C7", "#B45309"),
            _ => ("#E2E8F0", "#475569"),
        };

        return (Freeze(tint), Freeze(ink));
    }

    private static Brush Freeze(string colour)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(colour)!;

        brush.Freeze();

        return brush;
    }
}
