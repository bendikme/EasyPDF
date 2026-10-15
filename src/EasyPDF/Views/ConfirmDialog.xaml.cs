using System.Windows;
using EasyPDF.Services;

namespace EasyPDF.Views;

/// <summary>
/// Asks before something leaves the document. DialogResult is true only when the confirm button
/// was clicked; Enter, Escape and Cancel all leave it false.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string target, string confirmText)
    {
        InitializeComponent();

        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        TargetText.Text = target;
        ConfirmButton.Content = confirmText;
        Loaded += (_, _) =>
        {
            CancelButton.Focus();
            _shown = Environment.TickCount64;
        };
    }

    /// <summary>
    /// The dialog opens on the click of a link; the second click of a double-click, or a click
    /// made in a hurry, must not land on the confirm button and open what was only just shown.
    /// </summary>
    private const int ConfirmDelayMs = 400;

    private long _shown = long.MaxValue;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _shown < ConfirmDelayMs) return;
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Warn("Closing the confirmation dialog failed.", ex);
            Close();
        }
    }
}
