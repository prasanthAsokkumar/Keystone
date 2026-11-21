using System;
using System.Collections.Generic;
using Runway.Localization;

namespace Runway.Models;

/// <summary>The Swiss retirement provision a savings account belongs to. Stored by name.</summary>
public enum PensionPillar
{
    /// <summary>1st pillar: state pension (AHV/AVS). Its letters project a pension rather than show savings.</summary>
    Ahv,

    /// <summary>2nd pillar: the employer's pension fund (BVG/LPP, "Pensionskasse").</summary>
    PensionFund,

    /// <summary>2nd pillar money parked between jobs ("Freizügigkeitskonto").</summary>
    VestedBenefits,

    /// <summary>Tax-privileged private pension ("Säule 3a").</summary>
    Pillar3a,

    /// <summary>Other private savings for retirement ("Säule 3b"), e.g. life insurance.</summary>
    Pillar3b,
}

/// <summary>A retirement account, e.g. a pension fund or a 3a account, for one household member.</summary>
public class PensionAccount
{
    public int Id { get; set; }

    /// <summary>Usually the provider, e.g. "Pensionskasse Basel-Stadt" or "VIAC 3a".</summary>
    public string Name { get; set; } = "";

    public PensionPillar Pillar { get; set; }

    /// <summary>Whose provision it is; null for a shared account.</summary>
    public int? PersonId { get; set; }

    public string Notes { get; set; } = "";

    public List<PensionStatement> Statements { get; set; } = [];

    /// <summary>The pillar's name in the app's language.</summary>
    public static string PillarName(PensionPillar pillar) => Loc.T(pillar switch
    {
        PensionPillar.Ahv => "AHV (1st pillar)",
        PensionPillar.PensionFund => "Pension fund (2nd pillar)",
        PensionPillar.VestedBenefits => "Vested benefits (2nd pillar)",
        PensionPillar.Pillar3a => "Pillar 3a",
        _ => "Pillar 3b",
    });

    /// <summary>A short label for lists, e.g. "2nd pillar".</summary>
    public static string PillarShortName(PensionPillar pillar) => Loc.T(pillar switch
    {
        PensionPillar.Ahv => "1st pillar",
        PensionPillar.PensionFund or PensionPillar.VestedBenefits => "2nd pillar",
        PensionPillar.Pillar3a => "Pillar 3a",
        _ => "Pillar 3b",
    });

    /// <summary>AHV letters give a projected pension, not money saved up.</summary>
    public static bool HasSavings(PensionPillar pillar) => pillar != PensionPillar.Ahv;
}

/// <summary>What one yearly letter said about an account.</summary>
public class PensionStatement
{
    public int Id { get; set; }

    public int AccountId { get; set; }

    /// <summary>The date the figures are valid on, usually 31 December or 1 January.</summary>
    public DateOnly Date { get; set; }

    /// <summary>Money saved up: the retirement savings ("Altersguthaben") or account balance.</summary>
    public decimal Balance { get; set; }

    /// <summary>Paid in during the year, by you (and your employer for the pension fund).</summary>
    public decimal? PaidIn { get; set; }

    /// <summary>Pension fund: projected savings at retirement age.</summary>
    public decimal? ProjectedCapital { get; set; }

    /// <summary>Pension fund or AHV: projected pension per year from retirement age.</summary>
    public decimal? ProjectedPension { get; set; }

    /// <summary>Pension fund: how much can still be bought in voluntarily ("Einkaufspotenzial").</summary>
    public decimal? BuyInPotential { get; set; }

    /// <summary>The letter, filed in Documents.</summary>
    public int? DocumentId { get; set; }
}
