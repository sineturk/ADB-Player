using System.Windows.Controls;
using System.Windows.Input;
using UserControl = System.Windows.Controls.UserControl;
using AltyaziDB.Player.App.ViewModels;

namespace AltyaziDB.Player.App.Views.Pages;

public partial class LibraryView : UserControl
{
    private bool _artworkPageLoading;

    public LibraryView() => InitializeComponent();

    private async void Library_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (sender is System.Windows.Controls.ListBox listBox
            && string.Equals(listBox.Name, nameof(LibraryGridItems), StringComparison.Ordinal))
        {
            await vm.Library.PlaySelectedGridAsync();
            return;
        }

        await vm.Library.PlaySelectedAsync();
    }

    private async void SeriesEpisode_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await vm.Library.PlaySelectedAsync();
    }

    private async void LibraryItems_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_artworkPageLoading
            || e.OriginalSource is not ScrollViewer viewer
            || viewer.ScrollableHeight <= 0)
        {
            return;
        }

        var isGrid = sender is System.Windows.Controls.ListBox listBox
                     && string.Equals(listBox.Name, nameof(LibraryGridItems), StringComparison.Ordinal);

        if (isGrid)
        {
            if (e.VerticalChange <= 0 || viewer.VerticalOffset < 120)
                return;
        }
        else if (viewer.VerticalOffset < viewer.ScrollableHeight - 260)
        {
            return;
        }

        if (DataContext is not MainViewModel vm)
            return;

        try
        {
            _artworkPageLoading = true;
            await vm.Library.LoadMoreArtworkAsync();
        }
        finally
        {
            _artworkPageLoading = false;
        }
    }
}
