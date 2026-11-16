using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tezgah.App.Services;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.App.ViewModels;

/// <summary>Shows an <see cref="UninstallPlan"/>, lets the user untick optional steps, confirms and executes it.</summary>
public sealed partial class UninstallViewModel : ObservableObject
{
    private readonly IUninstallService _service;
    private readonly IStrings _s;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private bool _isLoading = true;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    [NotifyPropertyChangedFor(nameof(IsTypedConfirmationValid))]
    private string _typedText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private bool _isExecuting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private bool _isDone;

    [ObservableProperty]
    private bool _succeeded;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    private UninstallPlan? _plan;

    public UninstallViewModel(string itemId, string itemName, IUninstallService service, IStrings strings)
    {
        ItemId = itemId;
        ItemName = itemName;
        _service = service;
        _s = strings;
    }

    public string ItemId { get; }

    public string ItemName { get; }

    public string Title => _s.Format("Uninstall_Title", ItemName);

    public ObservableCollection<UninstallStepViewModel> Steps { get; } = [];

    public ObservableCollection<PlanWarning> Warnings { get; } = [];

    public ObservableCollection<StepOutcome> Outcomes { get; } = [];

    public bool IsBlocked => Plan?.IsBlocked == true;

    public string? BlockReason => Plan?.Warnings.FirstOrDefault(w => w.Level == WarningLevel.Blocker)?.Message;

    public bool RequiresTypedConfirmation => Plan?.RequiresTypedConfirmation == true;

    public string TypePrompt => _s.Format("Uninstall_TypeToConfirm", ItemName);

    public bool IsTypedConfirmationValid => string.Equals(TypedText, ItemName, StringComparison.Ordinal);

    public string TotalText => _s.Format("Uninstall_Total", Formatting.Bytes(Steps.Where(s => s.IsSelected).Sum(s => s.Step.SizeBytes ?? 0)));

    public event EventHandler? CloseRequested;

    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            IsLoading = true;
            var plan = await _service.PlanAsync(ItemId, ct);
            Steps.Clear();
            foreach (var step in plan.Steps)
            {
                Steps.Add(new UninstallStepViewModel(step, _s, () => OnPropertyChanged(nameof(TotalText))));
            }

            Warnings.Clear();
            foreach (var w in plan.Warnings.OrderByDescending(w => w.Level))
            {
                Warnings.Add(w);
            }

            Plan = plan;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = _s.Format("Uninstall_PlanFailed", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnPlanChanged(UninstallPlan? value)
    {
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(BlockReason));
        OnPropertyChanged(nameof(RequiresTypedConfirmation));
        OnPropertyChanged(nameof(TotalText));
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    private bool CanExecute() =>
        Plan is not null && !IsLoading && !IsBlocked && !IsExecuting && !IsDone
        && (!RequiresTypedConfirmation || IsTypedConfirmationValid);

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        if (Plan is not { } plan)
        {
            return;
        }

        IsExecuting = true;
        string? note = null;
        try
        {
            var selected = Steps.Where(s => s.IsSelected).Select(s => s.Step).ToList();
            // Progress<T> captures the UI SynchronizationContext, so reports from worker threads are marshalled.
            var progress = new Progress<string>(p => ProgressText = p);
            var result = await _service.ExecuteAsync(plan, selected, RequiresTypedConfirmation ? TypedText : null, progress, CancellationToken.None);
            Outcomes.Clear();
            foreach (var o in result.Outcomes)
            {
                Outcomes.Add(o);
            }

            Succeeded = result.Success;
            note = result.AuditWarning;

            var freed = result.Outcomes.Where(o => o.Success).Sum(o => o.Step.SizeBytes ?? 0);
            ResultText = result.Success
                ? _s.Format("Uninstall_Done", ItemName, Formatting.Bytes(freed))
                : _s.Format("Uninstall_Partial", result.Outcomes.Count(o => !o.Success));
        }
        catch (Tezgah.Core.Uninstall.UninstallConfirmationException ex) when (ex.PlanChanged)
        {
            // The folder changed after the plan was made (new unsaved work): show the fresh plan and its warnings.
            IsExecuting = false;
            TypedText = string.Empty;
            await LoadAsync(CancellationToken.None);
            Warnings.Insert(0, new PlanWarning(WarningLevel.Danger, ex.Message));
            return;
        }
        catch (Exception ex)
        {
            Succeeded = false;
            ResultText = _s.Format("Uninstall_Failed", ex.Message);
        }
        finally
        {
            IsExecuting = false;
        }

        IsDone = true;
        ProgressText = note ?? string.Empty;
    }

    private bool CanClose() => !IsExecuting;

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}

public sealed partial class UninstallStepViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly IStrings _s;

    [ObservableProperty]
    private bool _isSelected = true;

    public UninstallStepViewModel(UninstallStep step, IStrings strings, Action changed)
    {
        ArgumentNullException.ThrowIfNull(step);
        _isSelected = !step.Optional || step.DefaultSelected;
        Step = step;
        _s = strings;
        _changed = changed;
    }

    public UninstallStep Step { get; }

    public bool IsLocked => !Step.Optional;

    public string Description => Step.Description;

    public string? Detail => Step.Command?.Display ?? Step.Path;

    public string SizeText => Formatting.Bytes(Step.SizeBytes);

    public string KindText => _s.Text("StepKind_" + Step.Kind);

    public UninstallStepKind Kind => Step.Kind;


    partial void OnIsSelectedChanged(bool value)
    {
        if (!value && IsLocked)
        {
            IsSelected = true;
            return;
        }

        _changed();
    }
}
