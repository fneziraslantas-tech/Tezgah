using System.ComponentModel;
using System.Windows;
using Tezgah.App.ViewModels;

namespace Tezgah.App.Views;

public partial class CleanupDialog : Window
{
    private readonly CleanupViewModel _vm;

    public CleanupDialog(CleanupViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        e.Cancel = _vm.IsBusy;
        base.OnClosing(e);
    }
}
