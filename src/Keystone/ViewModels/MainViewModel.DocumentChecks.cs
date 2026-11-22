using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>
/// Checks in the form for filing one document: whether it was filed before, and whether it also belongs under
/// Insurance or Contracts &amp; dates.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The fingerprint of the file being added.</summary>
    private string _documentSourceHash = "";

    /// <summary>What the local AI said the document also is, e.g. an insurance policy.</summary>
    private FollowUp? _documentAiFollowUp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocumentDuplicate), nameof(DocumentDuplicateText), nameof(DocumentSubmitText))]
    public partial Duplicate? DocumentDuplicate { get; set; }

    public bool HasDocumentDuplicate => DocumentDuplicate is not null;

    public string DocumentDuplicateText => DuplicateText(DocumentDuplicate);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocumentFollowUp))]
    public partial FollowUp? DocumentFollowUp { get; set; }

    public bool HasDocumentFollowUp => DocumentFollowUp is not null && !IsEditingDocument;

    [ObservableProperty]
    public partial string DocumentFollowUpText { get; set; } = "";

    /// <summary>After saving, open the Insurance or Contracts form filled in from the document.</summary>
    [ObservableProperty]
    public partial bool DocumentAddFollowUp { get; set; } = true;

    internal static string DuplicateText(Duplicate? duplicate) => duplicate switch
    {
        null => "",
        { Match: DuplicateMatch.SameFile } d =>
            Loc.F("You already filed this file on {0} as “{1}”.", Format.Date(DateOnly.FromDateTime(d.Document.AddedAt)), d.Document.Title),
        { Match: DuplicateMatch.SameBill } d =>
            Loc.F("This looks like the bill “{0}” from {1}: it has the same payment reference.", d.Document.Title, Format.Date(d.Document.Date)),
        var d =>
            Loc.F("This looks like “{0}” from {1}: same sender, amount and date.", d.Document.Title, Format.Date(d.Document.Date)),
    };

    /// <summary>Takes the file's fingerprint and checks whether it was filed before.</summary>
    private async Task CheckDocumentFileAsync(string path)
    {
        try
        {
            _documentSourceHash = await Task.Run(() => DuplicateFinder.Hash(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _documentSourceHash = "";
        }
        RefreshDocumentDuplicate();
    }

    /// <summary>Checks again once the QR code or the AI has filled in sender, amount, date and reference.</summary>
    private void RefreshDocumentDuplicate()
    {
        if (IsEditingDocument || _documentSourcePath is null)
            return;
        var found = DuplicateFinder.Find(Documents, _documentSourceHash, DocumentSender, DocumentAmount,
            DocumentDate is { } date ? DateOnly.FromDateTime(date) : null, DocumentPaymentReference);
        if (found?.Document == DocumentDuplicate?.Document && found?.Match == DocumentDuplicate?.Match)
            return;
        DocumentDuplicate = found;
        // The bill is already in the expenses through the first copy: don't count it twice.
        if (found?.Document.TransactionId is not null)
            DocumentCreateEntry = false;
    }

    /// <summary>The AI's suggestion, or else what the chosen type says (a policy belongs under Insurance).</summary>
    private void RefreshDocumentFollowUp()
    {
        var followUp = IsEditingDocument ? null : _documentAiFollowUp ?? FollowUpForKind(SelectedDocumentKind);
        DocumentFollowUp = followUp;
        DocumentFollowUpText = followUp is null ? "" : FollowUpText(DraftDocument(), followUp);
    }

    /// <summary>The form's values as a document, for describing the follow-up before it is saved.</summary>
    private HouseholdDocument DraftDocument() => new()
    {
        Kind = SelectedDocumentKind,
        Title = DocumentTitle.Trim(),
        Sender = DocumentSender.Trim(),
        PersonId = DocumentPerson.Id,
        Date = DateOnly.FromDateTime(DocumentDate ?? DateTime.Today),
        Amount = DocumentAmount,
    };

    partial void OnDocumentKindIndexChanged(int value) => RefreshDocumentFollowUp();

    partial void OnDocumentSenderChanged(string value)
    {
        if (DocumentFollowUp is not null)
            RefreshDocumentFollowUp();
    }

    partial void OnDocumentPersonChanged(PersonOption value)
    {
        if (DocumentFollowUp is not null)
            RefreshDocumentFollowUp();
    }

    /// <summary>Opens the document already filed instead of this copy.</summary>
    [RelayCommand]
    private void ShowDuplicateDocument()
    {
        if (DocumentDuplicate?.Document is { } document)
            EditDocument(document);
    }

    /// <summary>Doesn't file this copy.</summary>
    [RelayCommand]
    private void SkipDuplicateDocument() => ClosePanel();

    private void ResetDocumentChecks()
    {
        _documentSourceHash = "";
        _documentAiFollowUp = null;
        DocumentDuplicate = null;
        DocumentFollowUp = null;
        DocumentFollowUpText = "";
        DocumentAddFollowUp = true;
    }
}
