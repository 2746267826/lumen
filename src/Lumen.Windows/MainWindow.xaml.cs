using System.Windows;
using System.Windows.Input;
using Lumen.Windows.ViewModels;

namespace Lumen.Windows;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainViewModel vm)
        {
            return;
        }

        if (vm.IsInviteOpen || vm.IsDetailOpen || vm.IsComposeOpen || vm.IsShareDialogOpen)
        {
            if (vm.IsInviteOpen)
            {
                return;
            }

            if (vm.IsDetailOpen)
            {
                vm.CloseDetailCommand.Execute(null);
            }
            else if (vm.IsComposeOpen)
            {
                vm.CloseComposeCommand.Execute(null);
            }
            else
            {
                vm.CloseShareDialogCommand.Execute(null);
            }

            e.Handled = true;
        }
    }
}
