using System.Windows;
using System.Windows.Controls;

namespace AltyaziDB.Player.App.Services;

public static class PasswordBoxBinding
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword",
        typeof(string),
        typeof(PasswordBoxBinding),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(PasswordBoxBinding),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty IsUpdatingProperty = DependencyProperty.RegisterAttached(
        "IsUpdating",
        typeof(bool),
        typeof(PasswordBoxBinding));

    public static string GetBoundPassword(DependencyObject value) =>
        (string)value.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject value, string password) =>
        value.SetValue(BoundPasswordProperty, password);

    public static bool GetIsEnabled(DependencyObject value) =>
        (bool)value.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject value, bool enabled) =>
        value.SetValue(IsEnabledProperty, enabled);

    private static bool GetIsUpdating(DependencyObject value) =>
        (bool)value.GetValue(IsUpdatingProperty);

    private static void SetIsUpdating(DependencyObject value, bool updating) =>
        value.SetValue(IsUpdatingProperty, updating);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not PasswordBox passwordBox)
        {
            return;
        }

        if ((bool)args.OldValue)
        {
            passwordBox.PasswordChanged -= OnPasswordChanged;
        }

        if ((bool)args.NewValue)
        {
            passwordBox.PasswordChanged += OnPasswordChanged;
        }
    }

    private static void OnBoundPasswordChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not PasswordBox passwordBox || GetIsUpdating(passwordBox))
        {
            return;
        }

        var password = args.NewValue as string ?? string.Empty;
        if (!string.Equals(passwordBox.Password, password, StringComparison.Ordinal))
        {
            passwordBox.Password = password;
        }
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs args)
    {
        if (sender is not PasswordBox passwordBox)
        {
            return;
        }

        SetIsUpdating(passwordBox, true);
        SetBoundPassword(passwordBox, passwordBox.Password);
        SetIsUpdating(passwordBox, false);
    }
}
