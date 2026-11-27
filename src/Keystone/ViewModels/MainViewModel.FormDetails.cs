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
    [ObservableProperty] public partial bool PersonHealthOpen { get; set; }
    [ObservableProperty] public partial bool PersonMoreOpen { get; set; }

    private static bool Has(string? text) => !string.IsNullOrWhiteSpace(text);

    // When a form opens, its folds show whatever already has content; filling fields in afterwards unfolds them too.
    partial void OnIsInsurancePanelOpenChanged(bool value)
    {
        if (value)
            InsuranceMoreOpen = Has(PolicyNumber) || Has(PolicyNotes) || PolicyDocument?.Document is not null;
    }

    partial void OnIsPersonPanelOpenChanged(bool value)
    {
        if (!value)
            return;
        PersonHealthOpen = Has(PersonAllergies) || Has(PersonConditions) || PersonBloodTypeIndex > 0 || Has(PersonDoctorName) ||
                           Has(PersonEmergencyName) || Has(PersonEmergencyPhone);
        PersonMoreOpen = Has(PersonAhvNumber) || Has(PersonNotes);
    }

    partial void OnPolicyNumberChanged(string value) { if (Has(value)) InsuranceMoreOpen = true; }
    partial void OnPolicyNotesChanged(string value) { if (Has(value)) InsuranceMoreOpen = true; }
    partial void OnPolicyDocumentChanged(DocumentOption? value) { if (value?.Document is not null) InsuranceMoreOpen = true; }

    partial void OnPersonAllergiesChanged(string value) { if (Has(value)) PersonHealthOpen = true; }
    partial void OnPersonConditionsChanged(string value) { if (Has(value)) PersonHealthOpen = true; }
    partial void OnPersonBloodTypeIndexChanged(int value) { if (value > 0) PersonHealthOpen = true; }
    partial void OnPersonDoctorNameChanged(string value) { if (Has(value)) PersonHealthOpen = true; }
    partial void OnPersonEmergencyNameChanged(string value) { if (Has(value)) PersonHealthOpen = true; }
    partial void OnPersonEmergencyPhoneChanged(string value) { if (Has(value)) PersonHealthOpen = true; }
    partial void OnPersonAhvNumberChanged(string value) { if (Has(value)) PersonMoreOpen = true; }
    partial void OnPersonNotesChanged(string value) { if (Has(value)) PersonMoreOpen = true; }
}
