using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProstoKVN;

/// <summary>
/// Self-contained onboarding surface embedded inside MainWindow.
/// No modal or secondary Windows are created.
/// </summary>
public partial class OnboardingView : UserControl
{
    private bool isBusy;

    public event EventHandler<SubscriptionImporter.Result>? SetupCompleted;
    public event EventHandler? Skipped;

    public OnboardingView()
    {
        InitializeComponent();
    }

    public string SubscriptionUrl
    {
        get => LinkBox.Text.Trim();
        set => LinkBox.Text = value ?? "";
    }

    public void FocusLink()
    {
        Dispatcher.BeginInvoke(new Action(() => LinkBox.Focus()),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText()) LinkBox.Text = Clipboard.GetText().Trim();
            LinkBox.Focus();
            LinkBox.CaretIndex = LinkBox.Text.Length;
        }
        catch
        {
            SetFeedback("Не удалось прочитать буфер обмена. Вставьте ссылку через Ctrl+V.", true);
        }
    }

    private void SetFeedback(string message, bool isError = false)
    {
        FeedbackText.Text = message;
        FeedbackText.Foreground = isError ? Brushes.Orange
            : new SolidColorBrush(Color.FromRgb(155, 191, 235));
    }

    private void SetBusy(bool busy)
    {
        isBusy = busy;
        SubmitButton.IsEnabled = !busy;
        PasteButton.IsEnabled = !busy;
        LaterButton.IsEnabled = !busy;
        LinkBox.IsEnabled = !busy;
        SubmitButton.Content = busy ? "Загружаю серверы..." : "Проверить и добавить серверы";
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (isBusy) return;
        string url = SubscriptionUrl;
        if (!SubscriptionImporter.IsValidUrl(url))
        {
            SetFeedback("Нужна корректная HTTPS-ссылка подписки.", true);
            LinkBox.Focus();
            return;
        }
        SetBusy(true);
        SetFeedback("Проверяю подписку и получаю серверы...");
        try
        {
            var result = await SubscriptionImporter.LoadAsync(url);
            SetupCompleted?.Invoke(this, result);
        }
        catch (Exception ex)
        {
            // Avoid echoing subscription URL/token from remote error messages.
            SetFeedback(ex is InvalidOperationException ? ex.Message
                : "Не удалось загрузить серверы. Проверьте интернет и подписку.", true);
        }
        finally { SetBusy(false); }
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (!isBusy) Skipped?.Invoke(this, EventArgs.Empty);
    }
}
