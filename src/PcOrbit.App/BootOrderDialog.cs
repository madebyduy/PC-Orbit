using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcOrbit.Core.Localization;

namespace PcOrbit.App;

/// <summary>
/// Reordering a list-valued firmware setting — the boot order — by moving entries up and down.
/// </summary>
/// <remarks>
/// <para>
/// <c>BootOrder</c> reads as <c>USBCD:USBFDD:NVMe0:…</c> and accepts the same names as options, so
/// the one-value picker every other row uses would have replaced the whole list with one device.
/// This is the control that fits the shape of the value: the current order as a list, two buttons
/// to move an entry, and a result that is the same list in a new order. Nothing can be added that
/// was not there and nothing can be removed; the firmware's own list of devices is the allowlist
/// and its current order is the starting point.
/// </para>
/// <para>
/// The confirmation that follows is the ordinary one for a Serious setting — consequences, then
/// typing the setting's name — because a boot order that no longer starts the disk Windows is on is
/// a machine that boots to nothing.
/// </para>
/// </remarks>
public static class BootOrderDialog
{
    /// <summary>Returns the new order joined the way the firmware wrote it, or null when cancelled or unchanged.</summary>
    public static string? Show(Window owner, IStringCatalog strings, string settingName, string current)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(strings);

        string T(string key, IReadOnlyDictionary<string, string>? args = null) => strings.Format(key, args);

        List<string> order = [.. current.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        var list = new ListBox
        {
            ItemsSource = order,
            Height = 260,
            FontSize = 13.5,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)),
        };

        var up = new Button { Content = T("app.firmware.reorder.up"), MinWidth = 96, Height = 34, Margin = new Thickness(0, 0, 0, 8) };
        var down = new Button { Content = T("app.firmware.reorder.down"), MinWidth = 96, Height = 34 };

        void Move(int delta)
        {
            int index = list.SelectedIndex;
            int target = index + delta;

            if (index < 0 || target < 0 || target >= order.Count)
            {
                return;
            }

            (order[index], order[target]) = (order[target], order[index]);
            list.ItemsSource = null;
            list.ItemsSource = order;
            list.SelectedIndex = target;
        }

        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);

        var body = new StackPanel { Margin = new Thickness(26, 22, 26, 20) };

        body.Children.Add(new TextBlock
        {
            Text = T("app.firmware.reorder.title", new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = settingName }),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
        });

        body.Children.Add(new TextBlock
        {
            Text = T("app.firmware.reorder.hint"),
            FontSize = 13,
            Margin = new Thickness(0, 8, 0, 14),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
        });

        var row = new DockPanel();
        var buttons = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(up);
        buttons.Children.Add(down);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(list);
        body.Children.Add(row);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = T("app.cancel"), MinWidth = 110, Height = 36, Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
        var apply = new Button { Content = T("app.firmware.reorder.apply"), MinWidth = 150, Height = 36 };
        footer.Children.Add(cancel);
        footer.Children.Add(apply);
        body.Children.Add(footer);

        var dialog = new Window
        {
            Title = T("app.title"),
            Owner = owner,
            Content = body,
            SizeToContent = SizeToContent.Height,
            Width = 520,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Colors.White),
        };

        var accepted = false;
        apply.Click += (_, _) => { accepted = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        dialog.ShowDialog();

        if (!accepted)
        {
            return null;
        }

        string joined = string.Join(':', order);

        return string.Equals(joined, current, StringComparison.Ordinal) ? null : joined;
    }
}
