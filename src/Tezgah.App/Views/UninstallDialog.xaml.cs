using System.ComponentModel;
using System.Windows;
using Tezgah.App.ViewModels;

namespace Tezgah.App.Views;

public partial class UninstallDialog : Window
{
    private readonly UninstallViewModel _vm;

    public UninstallDialog(UninstallViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += async (_, _) =>
        {
            await viewModel.LoadAsync(CancellationToken.None);
            if (viewModel.RequiresTypedConfirmation)
            {
                ConfirmBox.Focus();
            }
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // never abandon a half-executed uninstall
        e.Cancel = _vm.IsExecuting;
        base.OnClosing(e);
    }
}
