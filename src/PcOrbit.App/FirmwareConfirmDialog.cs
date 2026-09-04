using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcOrbit.Core.Firmware;
using PcOrbit.Core.Localization;

namespace PcOrbit.App;

/// <param name="Password">Null when none was asked for. Never stored anywhere by the caller.</param>
public sealed record FirmwareConfirmation(bool Confirmed, string? Password);

/// <summary>
/// The confirmation before a firmware setting is written.
/// </summary>
/// <remarks>
/// <para>
/// Built in code rather than in XAML because how hard it is to say yes depends on the change. A
/// routine setting gets a normal dialog. A serious one — anything that can stop the machine
/// starting, or put an encrypted disk behind a recovery key — makes you type the setting's name
/// first. That is not friction for its own sake: the whole failure mode here is a person clicking
/// through a dialog they did not read, on the one screen in this product where the cost of that is
/// a PC that does not boot.
/// </para>
/// <para>
/// Consequences are listed before the buttons, not after, and each is a sentence about what will
/// happen to this machine rather than a description of the setting.
/// </para>
/// <para>
/// The password box, when there is one, is a <see cref="PasswordBox"/>: its contents never become a
/// bindable string, are never written to the plan, the evidence or the log, and are handed straight
/// to the vendor call. This product does not store firmware passwords.
/// </para>
/// </remarks>
public static class FirmwareConfirmDialog
{
    public static FirmwareConfirmation Show(Window owner, IStringCatalog strings, FirmwareChangePlan plan)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(plan);

        string T(string key, IReadOnlyDictionary<string, string>? args = null) => strings.Format(key, args);

        var body = new StackPanel { Margin = new Thickness(26, 22, 26, 20) };

        body.Children.Add(new TextBlock
        {
            Text = T("app.firmware.confirmTitle", Args(("name", plan.Setting.Name))),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(new TextBlock
        {
            Text = T("app.firmware.confirmChange", Args(
                ("name", plan.Setting.Name),
                ("from", plan.Setting.Current ?? T("status.unknown")),
                ("to", plan.Wanted))),
            FontSize = 13.5,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
        });

        foreach (string consequence in plan.Consequences)
        {
            body.Children.Add(new TextBlock
            {
                Text = "•  " + T(consequence),
                FontSize = 13,
                Margin = new Thickness(0, 9, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // Typing the name is the gate for a serious change. Case-insensitive and trimmed — the point
        // is to have read which setting this is, not to be good at typing.
        TextBox? typed = null;

        if (plan.NeedsTypedConfirmation)
        {
            body.Children.Add(new TextBlock
            {
                Text = T("app.firmware.typeToConfirm", Args(("name", plan.Setting.Name))),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 18, 0, 6),
                TextWrapping = TextWrapping.Wrap,
            });

            typed = new TextBox { FontSize = 14, Padding = new Thickness(8, 6, 8, 6) };
            body.Children.Add(typed);
        }

        PasswordBox? password = null;

        if (plan.NeedsPassword)
        {
            body.Children.Add(new TextBlock
            {
                Text = T("app.firmware.passwordPrompt"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 18, 0, 6),
                TextWrapping = TextWrapping.Wrap,
            });

            password = new PasswordBox { FontSize = 14, Padding = new Thickness(8, 6, 8, 6) };
            body.Children.Add(password);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
        };

        var cancel = new Button
        {
            Content = T("app.cancel"),
            MinWidth = 110,
            Height = 36,
            Margin = new Thickness(0, 0, 10, 0),
            IsCancel = true,
        };

        var proceed = new Button
        {
            Content = T("app.firmware.confirmGo"),
            MinWidth = 150,
            Height = 36,

            // Off until the name has been typed. A serious change should not have a live button
            // sitting under the cursor while the text explaining it is still unread.
            IsEnabled = !plan.NeedsTypedConfirmation,
        };

        if (typed is not null)
        {
            typed.TextChanged += (_, _) => proceed.IsEnabled =
                typed.Text.Trim().Equals(plan.Setting.Name, StringComparison.OrdinalIgnoreCase);
        }

        buttons.Children.Add(cancel);
        buttons.Children.Add(proceed);
        body.Children.Add(buttons);

        var dialog = new Window
        {
            Title = T("app.title"),
            Owner = owner,
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            SizeToContent = SizeToContent.Height,
            Width = 560,
            MaxHeight = 700,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Colors.White),
        };

        var confirmed = false;

        proceed.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };

        cancel.Click += (_, _) => dialog.Close();

        dialog.ShowDialog();

        return new FirmwareConfirmation(confirmed, confirmed ? password?.Password : null);
    }

    private static Dictionary<string, string> Args(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);

        foreach ((string key, string value) in pairs)
        {
            result[key] = value;
        }

        return result;
    }
}
