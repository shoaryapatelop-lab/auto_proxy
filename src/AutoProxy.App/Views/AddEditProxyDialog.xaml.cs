using System.Windows;
using AutoProxy.App.ViewModels;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;

namespace AutoProxy.App.Views;

public partial class AddEditProxyDialog : Window
{
    private readonly ProxyEditorModel _model;

    public AddEditProxyDialog(
        ProxyProfile? original, ICredentialManager credentials, ILogService log)
    {
        InitializeComponent();

        _model = new ProxyEditorModel(original, credentials, log);
        Title = _model.Title;

        NameBox.Text = _model.Name;
        HostBox.Text = _model.Host;
        PortBox.Text = _model.PortText;
        AuthCheck.IsChecked = _model.AuthenticationRequired;
        UsernameBox.Text = _model.Username;
        EnabledCheck.IsChecked = _model.Enabled;

        UpdateAuthVisibility();
    }

    public ProxyProfile? Result { get; private set; }

    public ProxyProfile? ShowDialogAndCapture()
    {
        ShowDialog();
        return Result;
    }

    private void AuthCheck_Changed(object sender, RoutedEventArgs e) => UpdateAuthVisibility();

    private void UpdateAuthVisibility()
    {
        if (AuthPanel is null) return;
        AuthPanel.Visibility = AuthCheck.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _model.Name = NameBox.Text;
        _model.Host = HostBox.Text;
        _model.PortText = PortBox.Text;
        _model.AuthenticationRequired = AuthCheck.IsChecked == true;
        _model.Username = UsernameBox.Text;
        _model.Password = PasswordBox.Password;
        _model.Enabled = EnabledCheck.IsChecked == true;

        var profile = _model.Validate();
        if (profile is null)
        {
            ShowError(_model.ValidationError ?? "Please correct the invalid fields.");
            return;
        }

        if (!_model.SaveCredentials(profile))
        {
            ShowError("Could not store the credentials securely. Please try again.");
            return;
        }

        Result = profile;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
