using System.ComponentModel;
using System.Windows;
using Tezgah.App.ViewModels;

namespace Tezgah.App.Views;

public partial class SettingsDialog : Window
{
    public SettingsDialog(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
    }
}

public partial class CloneDialog : Window
{
    private readonly CloneViewModel _vm;

    public CloneDialog(CloneViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) => UrlBox.Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_vm.IsBusy)
        {
            // first close request cancels the clone; window stays until git has stopped
            _vm.CancelCommand.Execute(null);
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
