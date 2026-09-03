using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Localization;

namespace PcOrbit.App;

/// <summary>
/// Safe Apply on the desktop: apply, then ask "can you still see this?", and revert on silence
/// (spec 10.1). Built in code because it must work even while the display mode is in flux.
/// </summary>
/// <remarks>
/// The countdown is the point — a user who cannot see the screen cannot answer, and no answer
/// means "put it back". A button extends the window instead of forcing a rushed decision
/// (spec 21.12), and extending is not confirming.
/// </remarks>
public sealed class WpfSafeApplyConfirmation(IStringCatalog strings) : ISafeApplyConfirmation
{
    private readonly IStringCatalog _strings = strings ?? throw new ArgumentNullException(nameof(strings));

    public async Task<bool> ConfirmAsync(
        string messageKey,
        IReadOnlyDictionary<string, string> arguments,
        int withinSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var confirmed = false;
            var remaining = withinSeconds;

            var message = new TextBlock
            {
                Text = _strings.Format(messageKey, arguments),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 15,
                Foreground = (Brush)Application.Current.Resources["Ink"],
            };

            var countdown = new TextBlock
            {
                FontSize = 34,
                FontWeight = FontWeights.ExtraBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 12),
                Foreground = (Brush)Application.Current.Resources["Accent"],
            };

            var keep = new Button
            {
                Content = _strings.Format("app.safeApply.keep"),
                Style = (Style)Application.Current.Resources["PrimaryButton"],
                MinWidth = 150,
                Margin = new Thickness(0, 0, 10, 0),
            };

            var more = new Button
            {
                Content = _strings.Format("app.safeApply.moreTime"),
                Style = (Style)Application.Current.Resources["SecondaryButton"],
                MinWidth = 130,
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            buttons.Children.Add(keep);
            buttons.Children.Add(more);

            var panel = new StackPanel { Margin = new Thickness(26) };
            panel.Children.Add(message);
            panel.Children.Add(countdown);
            panel.Children.Add(buttons);

            var dialog = new Window
            {
                Title = _strings.Format("app.title"),
                Content = panel,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)Application.Current.Resources["PageBg"],
                WindowStyle = WindowStyle.ToolWindow,
            };

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

            void Render() => countdown.Text = remaining.ToString(System.Globalization.CultureInfo.CurrentCulture);

            timer.Tick += (_, _) =>
            {
                remaining--;
                Render();

                if (remaining <= 0)
                {
                    // Silence means revert — the case Safe Apply exists for.
                    timer.Stop();
                    dialog.Close();
                }
            };

            keep.Click += (_, _) =>
            {
                confirmed = true;
                timer.Stop();
                dialog.Close();
            };

            more.Click += (_, _) =>
            {
                remaining = withinSeconds;
                Render();
            };

            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                dialog.Dispatcher.Invoke(() =>
                {
                    timer.Stop();
                    dialog.Close();
                }));

            Render();
            timer.Start();
            dialog.ShowDialog();
            timer.Stop();

            return confirmed;
        });
    }
}
