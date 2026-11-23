using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>
/// "Check filed papers" on the Insurance page: papers filed before policies were saved from them (or filed with the
/// follow-up unticked) are read again, and each policy or premium letter adds or updates its policy and premium,
/// as filing it today would. Oldest first, so the latest letter's premium is the one that counts.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckFiledPapersCommand))]
    public partial bool IsCheckingPapers { get; set; }

    [ObservableProperty]
    public partial string CheckPapersStatus { get; set; } = "";

    /// <summary>What reads a paper; replaced in tests, where there is no local AI.</summary>
    internal Func<HouseholdDocument, CancellationToken, Task<FollowUp?>>? ReadPaperForTest { get; set; }

    private CancellationTokenSource? _checkPapersCancellation;

    /// <summary>
    /// Papers already read by a check. Read again, an older premium letter would put back an older premium, since
    /// only the latest letter is linked to its policy.
    /// </summary>
    private HashSet<int> _checkedPapers = [];

    /// <summary>Insurance papers no policy was saved from yet, oldest first.</summary>
    internal IReadOnlyList<HouseholdDocument> PapersToCheck() => Documents
        .Where(d => IsInsuranceDocument(d) && !_checkedPapers.Contains(d.Id) && !_policies.Any(p => p.DocumentId == d.Id) &&
                    _documentStore.Exists(d))
        // Without the AI only a document filed as a policy says what it is.
        .Where(d => IsImportEnabled || ReadPaperForTest is not null || d.Kind == DocumentKind.Policy)
        .OrderBy(d => d.Date).ThenBy(d => d.Id)
        .ToList();

    private async Task<FollowUp?> ReadPaperAsync(HouseholdDocument document, CancellationToken token)
    {
        if (ReadPaperForTest is { } read)
            return await read(document, token);
        if (!IsImportEnabled)
            return FollowUpForKind(document.Kind);
        var content = await DocumentReader.ReadAsync(_documentStore.FullPath(document.RelativePath));
        var details = await _ai.ExtractDetailsAsync(content, AiEndpoint, AiModel, Today, HouseholdNames, token);
        return details.FollowUp;
    }

    [RelayCommand(CanExecute = nameof(CanCheckFiledPapers))]
    private async Task CheckFiledPapersAsync()
    {
        var papers = PapersToCheck();
        if (papers.Count == 0)
        {
            ShowNotice(IsImportEnabled
                ? Loc.T("No insurance papers left to check: every one is already linked to a policy.")
                : Loc.T("No policies to check. Turn on Local AI in Settings to read premium letters and other insurance papers too."));
            return;
        }

        var cancellation = _checkPapersCancellation = new CancellationTokenSource();
        IsCheckingPapers = true;
        var undos = new List<Func<Task>>();
        var (added, updated, failed) = (new List<string>(), new List<string>(), 0);
        try
        {
            for (var i = 0; i < papers.Count && !cancellation.IsCancellationRequested; i++)
            {
                var paper = papers[i];
                CheckPapersStatus = Loc.F("Reading {0} of {1}: {2}…", i + 1, papers.Count, paper.Title);
                try
                {
                    var read = await ReadPaperAsync(paper, cancellation.Token);
                    _checkedPapers.Add(paper.Id);
                    if (read is not { Kind: FollowUpKind.Insurance } followUp)
                        continue;
                    var isNew = MatchingPolicy(paper, followUp) is null;
                    var insurer = PolicyFrom(paper, followUp, MatchingPolicy(paper, followUp)).Insurer;
                    var (_, undo) = await SavePolicyFromDocumentAsync(paper, followUp);
                    undos.Add(undo);
                    var names = isNew ? added : updated;
                    if (!added.Contains(insurer) && !names.Contains(insurer))
                        names.Add(insurer);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    failed++; // e.g. a damaged file; the others are still read
                }
            }
        }
        finally
        {
            try
            {
                await repository.SetCheckedPapersAsync(string.Join(",", _checkedPapers.Order()));
            }
            catch (Exception)
            {
                // Only means the next check reads these papers again.
            }
            IsCheckingPapers = false;
            CheckPapersStatus = "";
            _checkPapersCancellation = null;
        }

        var checkedCount = papers.Count(p => _checkedPapers.Contains(p.Id));
        var parts = new List<string> { Loc.Plural(checkedCount, "Checked {0} paper", "Checked {0} papers") };
        if (added.Count > 0)
            parts.Add(Loc.F("added {0}", string.Join(", ", added)));
        if (updated.Count > 0)
            parts.Add(Loc.F("updated {0}", string.Join(", ", updated)));
        if (added.Count == 0 && updated.Count == 0)
            parts.Add(Loc.T("no new policies or premiums found"));
        if (failed > 0)
            parts.Add(Loc.Plural(failed, "{0} couldn’t be read", "{0} couldn’t be read"));
        CurrentPage = AppPage.Insurance;
        // Undone newest first, so each policy gets back what it had before.
        var checkedNow = papers.Where(p => _checkedPapers.Contains(p.Id)).Select(p => p.Id).ToList();
        ShowNotice(string.Join(" · ", parts), undos.Count == 0 ? null : async () =>
        {
            for (var i = undos.Count - 1; i >= 0; i--)
                await undos[i]();
            // Undone, the papers can be checked again.
            _checkedPapers.ExceptWith(checkedNow);
            await repository.SetCheckedPapersAsync(string.Join(",", _checkedPapers.Order()));
        });
    }

    private bool CanCheckFiledPapers() => !IsCheckingPapers;

    [RelayCommand]
    private void StopCheckingPapers() => _checkPapersCancellation?.Cancel();
}
