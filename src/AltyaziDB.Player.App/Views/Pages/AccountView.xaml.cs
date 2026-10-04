using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AltyaziDB.Player.App.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace AltyaziDB.Player.App.Views.Pages;

public partial class AccountView : UserControl
{
    private AccountPanelViewModel? _account;
    private bool _syncingPassword;

    public AccountView()
    {
        InitializeComponent();
    }

    private void AccountView_OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachAccount((DataContext as MainViewModel)?.Account);
    }

    private void AccountView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        AttachAccount(null);
    }

    private void AccountView_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            AttachAccount((e.NewValue as MainViewModel)?.Account);
        }
    }

    private void AttachAccount(AccountPanelViewModel? account)
    {
        if (ReferenceEquals(_account, account))
        {
            SyncPassword();
            return;
        }

        if (_account is not null)
        {
            _account.PropertyChanged -= Account_PropertyChanged;
        }

        _account = account;

        if (_account is not null)
        {
            _account.PropertyChanged += Account_PropertyChanged;
        }

        SyncPassword();
    }

    private void Account_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountPanelViewModel.Password))
        {
            if (Dispatcher.CheckAccess()) SyncPassword();
            else Dispatcher.InvokeAsync(SyncPassword);
        }
    }

    private void SyncPassword()
    {
        if (_account is null) return;
        if (string.Equals(PasswordBox.Password, _account.Password, StringComparison.Ordinal)) return;

        _syncingPassword = true;
        try
        {
            PasswordBox.Password = _account.Password;
        }
        finally
        {
            _syncingPassword = false;
        }
    }

    private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword) return;
        if (sender is PasswordBox { DataContext: AccountPanelViewModel model } box)
        {
            model.Password = box.Password;
        }
    }
}
