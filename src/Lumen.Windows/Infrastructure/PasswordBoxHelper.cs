using System.Windows;
using System.Windows.Controls;

namespace Lumen.Windows.Infrastructure;

public static class PasswordBoxHelper
{
    public static readonly DependencyProperty MonitorProperty =
        DependencyProperty.RegisterAttached(
            "Monitor",
            typeof(bool),
            typeof(PasswordBoxHelper),
            new PropertyMetadata(false, OnMonitorChanged));

    public static readonly DependencyProperty PasswordProperty =
        DependencyProperty.RegisterAttached(
            "Password",
            typeof(string),
            typeof(PasswordBoxHelper),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnPasswordChanged));

    private static bool _updating;

    public static bool GetMonitor(DependencyObject obj) =>
        (bool)obj.GetValue(MonitorProperty);

    public static void SetMonitor(DependencyObject obj, bool value) =>
        obj.SetValue(MonitorProperty, value);

    public static string GetPassword(DependencyObject obj) =>
        (string)obj.GetValue(PasswordProperty);

    public static void SetPassword(DependencyObject obj, string value) =>
        obj.SetValue(PasswordProperty, value);

    private static void OnMonitorChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is not PasswordBox box)
        {
            return;
        }

        box.PasswordChanged -= OnBoxPasswordChanged;
        if (args.NewValue is true)
        {
            box.PasswordChanged += OnBoxPasswordChanged;
        }
    }

    private static void OnPasswordChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is not PasswordBox box || _updating)
        {
            return;
        }

        box.Password = args.NewValue as string ?? "";
    }

    private static void OnBoxPasswordChanged(object sender, RoutedEventArgs args)
    {
        if (sender is not PasswordBox box)
        {
            return;
        }

        _updating = true;
        SetPassword(box, box.Password);
        _updating = false;
    }
}
