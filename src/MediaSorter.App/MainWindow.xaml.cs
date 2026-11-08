using System.Collections.Specialized;
using System.Windows;

namespace MediaSorter.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        };
    }
}
