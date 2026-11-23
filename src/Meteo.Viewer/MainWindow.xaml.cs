using System.Windows;

namespace Meteo.Viewer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Closed += (_, _) => _vm.Dispose();
    }

    /// <summary>
    /// ←/→ always step through hours, even when a list or the slider has focus
    /// (those controls would otherwise consume the arrow keys for their own navigation).
    /// </summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Right) { _vm.NextHourCommand.Execute(null); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Left) { _vm.PrevHourCommand.Execute(null); e.Handled = true; }
        else base.OnPreviewKeyDown(e);
    }
}
