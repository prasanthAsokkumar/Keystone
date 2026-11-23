using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;

namespace Keystone.ViewModels;

/// <summary>
/// A short message at the bottom of the window, e.g. "Deleted Rent" with an Undo button, or where an export was saved.
/// Deleting stays one click; a mistake is one click to take back.
/// </summary>
public partial class MainViewModel
{
    /// <summary>How long the message, and the chance to undo, stays.</summary>
    public static TimeSpan NoticeDuration { get; set; } = TimeSpan.FromSeconds(12);

    private Func<Task>? _undo;
    private CancellationTokenSource? _noticeTimer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? NoticeText { get; set; }

    public bool HasNotice => !string.IsNullOrEmpty(NoticeText);

    [ObservableProperty]
    public partial bool CanUndo { get; set; }

    /// <summary>Shows a message; with <paramref name="undo"/>, also an Undo button.</summary>
    public void ShowNotice(string text, Func<Task>? undo = null)
    {
        _noticeTimer?.Cancel();
        _undo = undo;
        CanUndo = undo is not null;
        NoticeText = text;

        var timer = _noticeTimer = new CancellationTokenSource();
        _ = HideNoticeLaterAsync(timer.Token);
    }

    private async Task HideNoticeLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(NoticeDuration, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        DismissNotice();
    }

    [RelayCommand]
    private void DismissNotice()
    {
        _noticeTimer?.Cancel();
        _undo = null;
        CanUndo = false;
        NoticeText = null;
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        var undo = _undo;
        DismissNotice();
        if (undo is null)
            return;
        try
        {
            await undo();
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not undo: {0}", ex.Message);
        }
    }

    /// <summary>Puts a deleted entry back as it was: same id, its changed months and its linked documents.</summary>
    private Func<Task> UndoDeleteTransaction(Transaction transaction, int[] linkedDocuments, BillInstalment[]? parts = null) => async () =>
    {
        await repository.AddTransactionAsync(transaction);
        if (linkedDocuments.Length > 0)
            await repository.LinkDocumentsAsync(linkedDocuments, transaction.Id);
        if (parts is { Length: > 0 })
        {
            await repository.LinkInstalmentsAsync(parts.Select(p => p.Id).ToArray(), transaction.Id);
            foreach (var part in parts)
                part.TransactionId = transaction.Id;
        }
        foreach (var document in Documents.Where(d => linkedDocuments.Contains(d.Id)))
            document.TransactionId = transaction.Id;
        InsertSorted(transaction);
        Recalculate();
    };

    /// <summary>Puts a deleted debt back with its extra payments and borrowing.</summary>
    private Func<Task> UndoDeleteDebt(Debt debt, int index) => async () =>
    {
        await repository.AddDebtAsync(debt);
        Debts.Insert(Math.Min(index, Debts.Count), CreateDebtItem(debt));
        Recalculate();
    };
}
