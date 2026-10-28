using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TranssionLab.ViewModels;

namespace TranssionLab.App;

public partial class MainWindow : Window
{
    public MainWindow(MainShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            try
            {
                await viewModel.GlobalScanDevicesCommand.ExecuteAsync().ConfigureAwait(true);
            }
            catch
            {
                // Startup scan errors are already captured in StatusBanner and StructuredLogger
            }
        };
        Closing += (_, _) =>
        {
            if (viewModel.CancelWaitingCommand.CanExecute(null))
            {
                viewModel.CancelWaitingCommand.Execute(null);
            }
            viewModel.Dispose();
        };
    }

    private void OnSidebarPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = FindDescendantScrollViewer(SidebarNavListBox);
        if (sv is not null)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - (e.Delta / 2.5));
            e.Handled = true;
        }
    }

    private void OnSidebarScrollUpClick(object sender, RoutedEventArgs e)
    {
        var sv = FindDescendantScrollViewer(SidebarNavListBox);
        sv?.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset - 96));
    }

    private void OnSidebarScrollDownClick(object sender, RoutedEventArgs e)
    {
        var sv = FindDescendantScrollViewer(SidebarNavListBox);
        sv?.ScrollToVerticalOffset(sv.VerticalOffset + 96);
    }

    private void OnLiveConsoleTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.ScrollToEnd();
        }
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
        {
            return sv;
        }

        int children = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < children; i++)
        {
            var found = FindDescendantScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
