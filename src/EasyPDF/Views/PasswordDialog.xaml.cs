using System.Windows;
using EasyPDF.Resources;
using EasyPDF.Services;

namespace EasyPDF.Views;

/// <summary>Asks for a document's password. <see cref="Password"/> stays null unless the user confirmed.</summary>
public partial class PasswordDialog : Window
{
    public PasswordDialog(string fileName, bool wrongBefore)
    {
        InitializeComponent();
        PromptText.Text = UiLanguage.Format(Strings.Password_Prompt, fileName);
        ErrorText.Visibility = wrongBefore ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    public string? Password { get; private set; }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) =>
        OkButton.IsEnabled = PasswordInput.Password.Length > 0;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (PasswordInput.Password.Length == 0) return;
        Password = PasswordInput.Password;
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Warn("Closing the password dialog failed.", ex);
            Close();
        }
    }
}
