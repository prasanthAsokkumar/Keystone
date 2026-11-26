using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Keystone.ViewModels;

/// <summary>
/// Shorter forms: fields most things don't need are folded under "More details". A new form opens with them folded;
/// they unfold by themselves when one of them has something in it, e.g. when editing a policy with a number, or when
/// the local AI filled in notes, so nothing entered is ever hidden.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] public partial bool InsuranceMoreOpen { get; set; }
    [ObservableProperty] public partial bool ContractMoreOpen { get; set; }
    [ObservableProperty] public partial bool PersonHealthOpen { get; set; }
    [ObservableProperty] public partial bool PersonMoreOpen { get; set; }
    [ObservableProperty] public partial bool MedicineMoreOpen { get; set; }
    [ObservableProperty] public partial bool EmploymentMoreOpen { get; set; }

    private static bool Has(string? text) => !string.IsNullOrWhiteSpace(text);

    // When a form opens, its folds show whatever already has content; filling fields in afterwards unfolds them too.
    partial void OnIsInsurancePanelOpenChanged(bool value)
    {
        if (value)
            InsuranceMoreOpen = Has(PolicyNumber) || Has(PolicyNotes) || PolicyDocument?.Document is not null;
    }

    partial void OnIsContractPanelOpenChanged(bool value)
    {
        if (value)
            ContractMoreOpen = Has(ContractCustomerNumber) || Has(ContractNotes) || ContractDocument?.Document is not null;
    }

    partial void OnIsPersonPanelOpenChanged(bool value)
    {
        if (!value)
            return;
        PersonHealthOpen = Has(PersonAllergies) || Has(PersonConditions) || PersonBloodTypeIndex > 0 || Has(PersonDoctorName) ||
                           Has(PersonEmergencyName) || Has(PersonEmergencyPhone);
        PersonMoreOpen = Has(PersonAhvNumber) || Has(PersonNotes);
    }

    partial void OnIsMedicinePanelOpenChanged(bool value)
    {
        if (value)
            MedicineMoreOpen = MedicineStock is not null || MedicinePackSize is not null || Has(MedicinePrescribedBy) ||
                               MedicinePrescriptionUntil is not null || MedicineEndsOn is not null || Has(MedicineNotes);
    }

    partial void OnIsEmploymentPanelOpenChanged(bool value)
    {
        if (value)
            EmploymentMoreOpen = EmploymentStartedOn is not null || EmploymentEndedOn is not null || Has(EmploymentAddress) ||
                                 Has(EmploymentPhone) || Has(EmploymentHrName) || Has(EmploymentHrPhone) || Has(EmploymentHrEmail) ||
                                 Has(EmploymentSickPayInsurer) || Has(EmploymentAccidentInsurer) || Has(EmploymentNotes);
    }

    partial void OnPolicyNumberChanged(string value) { if (Has(value)) InsuranceMoreOpen = true; }
    partial void OnPolicyNotesChanged(string value) { if (Has(value)) InsuranceMoreOpen = true; }
    partial void OnPolicyDocumentChanged(DocumentOption? value) { if (value?.Document is not null) InsuranceMoreOpen = true; }

    partial void OnContractCustomerNumberChanged(string value) { if (Has(value)) ContractMoreOpen = true; }
    partial void OnContractNotesChanged(string value) { if (Has(value)) ContractMoreOpen = true; }
    partial void OnContractDocumentChanged(DocumentOption? value) { if (value?.Document is not null) ContractMoreOpen = true; }

    partial void OnPersonAllergiesChanged(string value) { if (Has(value)) PersonHealthOpen = true; }
    partial void OnPersonConditionsChanged(string value) { if (Has(value)) PersonHealthOpen = true; }
    partial void OnPersonBloodTypeIndexChanged(int value) { if (value > 0) PersonHealthOpen = true; }
    partial void OnPersonDoctorNameChanged(string value) { if (Has(value)) PersonHealthOpen = true; }
    partial void OnPersonEmergencyNameChanged(string value) { if (Has(value)) PersonHealthOpen = true; }
    partial void OnPersonEmergencyPhoneChanged(string value) { if (Has(value)) PersonHealthOpen = true; }
    partial void OnPersonAhvNumberChanged(string value) { if (Has(value)) PersonMoreOpen = true; }
    partial void OnPersonNotesChanged(string value) { if (Has(value)) PersonMoreOpen = true; }

    partial void OnMedicineStockChanged(decimal? value) { if (value is not null) MedicineMoreOpen = true; }
    partial void OnMedicinePackSizeChanged(int? value) { if (value is not null) MedicineMoreOpen = true; }
    partial void OnMedicinePrescribedByChanged(string value) { if (Has(value)) MedicineMoreOpen = true; }
    partial void OnMedicinePrescriptionUntilChanged(DateTime? value) { if (value is not null) MedicineMoreOpen = true; }
    partial void OnMedicineEndsOnChanged(DateTime? value) { if (value is not null) MedicineMoreOpen = true; }
    partial void OnMedicineNotesChanged(string value) { if (Has(value)) MedicineMoreOpen = true; }

    partial void OnEmploymentStartedOnChanged(DateTime? value) { if (value is not null) EmploymentMoreOpen = true; }
    partial void OnEmploymentEndedOnChanged(DateTime? value) { if (value is not null) EmploymentMoreOpen = true; }
    partial void OnEmploymentAddressChanged(string value) { if (Has(value)) EmploymentMoreOpen = true; }
    partial void OnEmploymentPhoneChanged(string value) { if (Has(value)) EmploymentMoreOpen = true; }
    partial void OnEmploymentHrNameChanged(string value) { if (Has(value)) EmploymentMoreOpen = true; }
    partial void OnEmploymentHrPhoneChanged(string value) { if (Has(value)) EmploymentMoreOpen = true; }
    partial void OnEmploymentHrEmailChanged(string value) { if (Has(value)) EmploymentMoreOpen = true; }
    partial void OnEmploymentSickPayInsurerChanged(string value) { if (Has(value)) EmploymentMoreOpen = true; }
    partial void OnEmploymentAccidentInsurerChanged(string value) { if (Has(value)) EmploymentMoreOpen = true; }
    partial void OnEmploymentNotesChanged(string value) { if (Has(value)) EmploymentMoreOpen = true; }
}
