using System.Xml.Linq;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.Tests;

public sealed class BankStatementTests
{
    private const string Camt053 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.04">
          <BkToCstmrStmt>
            <Stmt>
              <Acct><Id><IBAN>CH93 0076 2011 6238 5295 7</IBAN></Id></Acct>
              <Bal><Tp><CdOrPrtry><Cd>OPBD</Cd></CdOrPrtry></Tp><Amt Ccy="CHF">5000.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><Dt><Dt>2026-10-31</Dt></Dt></Bal>
              <Bal><Tp><CdOrPrtry><Cd>CLBD</Cd></CdOrPrtry></Tp><Amt Ccy="CHF">8920.15</Amt><CdtDbtInd>CRDT</CdtDbtInd><Dt><Dt>2026-11-30</Dt></Dt></Bal>
              <Ntry>
                <Amt Ccy="CHF">7850.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><Sts>BOOK</Sts>
                <BookgDt><Dt>2026-11-25</Dt></BookgDt><ValDt><Dt>2026-11-25</Dt></ValDt>
                <AcctSvcrRef>20261125-0001</AcctSvcrRef>
                <NtryDtls><TxDtls>
                  <RltdPties><Dbtr><Nm>Novatec AG</Nm></Dbtr></RltdPties>
                  <RmtInf><Ustrd>Lohn November 2026</Ustrd></RmtInf>
                </TxDtls></NtryDtls>
              </Ntry>
              <Ntry>
                <Amt Ccy="CHF">2450.00</Amt><CdtDbtInd>DBIT</CdtDbtInd><Sts><Cd>BOOK</Cd></Sts>
                <BookgDt><Dt>2026-11-01</Dt></BookgDt>
                <AcctSvcrRef>20261101-0007</AcctSvcrRef>
                <NtryDtls><TxDtls>
                  <RltdPties><Cdtr><Pty><Nm>Immobilien Basel AG</Nm></Pty></Cdtr></RltdPties>
                  <RmtInf><Ustrd>Mietzins Dezember</Ustrd></RmtInf>
                </TxDtls></NtryDtls>
              </Ntry>
              <Ntry>
                <Amt Ccy="CHF">479.85</Amt><CdtDbtInd>DBIT</CdtDbtInd><Sts>BOOK</Sts>
                <BookgDt><Dt>2026-11-20</Dt></BookgDt>
                <AcctSvcrRef>20261120-0003</AcctSvcrRef>
                <AddtlNtryInf>Sammelauftrag</AddtlNtryInf>
                <NtryDtls>
                  <TxDtls>
                    <Refs><EndToEndId>E2E-1</EndToEndId></Refs>
                    <AmtDtls><TxAmt><Amt Ccy="CHF">289.85</Amt></TxAmt></AmtDtls>
                    <RltdPties><Cdtr><Nm>IWB</Nm></Cdtr></RltdPties>
                  </TxDtls>
                  <TxDtls>
                    <Refs><EndToEndId>NOTPROVIDED</EndToEndId><AcctSvcrRef>SUB-2</AcctSvcrRef></Refs>
                    <AmtDtls><TxAmt><Amt Ccy="CHF">190.00</Amt></TxAmt></AmtDtls>
                    <RltdPties><Cdtr><Nm>Swisscom</Nm></Cdtr></RltdPties>
                  </TxDtls>
                </NtryDtls>
              </Ntry>
              <Ntry>
                <Amt Ccy="CHF">1000.00</Amt><CdtDbtInd>DBIT</CdtDbtInd><Sts>BOOK</Sts>
                <BookgDt><Dt>2026-11-26</Dt></BookgDt>
                <AcctSvcrRef>20261126-0002</AcctSvcrRef>
                <NtryDtls><TxDtls>
                  <RltdPties><Cdtr><Nm>Lukas Meier</Nm></Cdtr><CdtrAcct><Id><IBAN>CH56 0483 5012 3456 7800 9</IBAN></Id></CdtrAcct></RltdPties>
                  <RmtInf><Ustrd>Sparen</Ustrd></RmtInf>
                </TxDtls></NtryDtls>
              </Ntry>
              <Ntry>
                <Amt Ccy="CHF">55.00</Amt><CdtDbtInd>DBIT</CdtDbtInd><Sts>PDNG</Sts>
                <BookgDt><Dt>2026-11-30</Dt></BookgDt>
              </Ntry>
            </Stmt>
          </BkToCstmrStmt>
        </Document>
        """;

    [Fact]
    public void ReadsCamt053()
    {
        var statement = BankStatementParser.ParseCamt(XDocument.Parse(Camt053));

        Assert.Equal("CH9300762011623852957", statement.Iban);
        Assert.Equal(8920.15m, statement.ClosingBalance);
        Assert.Equal(new DateOnly(2026, 11, 30), statement.ClosingDate);

        // The pending booking is left out; the collective booking becomes two lines.
        Assert.Equal(5, statement.Lines.Count);
        var salary = statement.Lines[0];
        Assert.Equal(7850m, salary.Amount);
        Assert.Equal("Novatec AG: Lohn November 2026", salary.Description);
        Assert.Equal("20261125-0001", salary.Reference);

        var rent = statement.Lines[1];
        Assert.Equal(-2450m, rent.Amount);
        Assert.Equal("Immobilien Basel AG: Mietzins Dezember", rent.Description); // newer Pty structure

        Assert.Equal([-289.85m, -190m], statement.Lines.Skip(2).Take(2).Select(l => l.Amount));
        Assert.Equal(["IWB", "Swisscom"], statement.Lines.Skip(2).Take(2).Select(l => l.Description));
        Assert.Equal(["E2E-1", "SUB-2"], statement.Lines.Skip(2).Take(2).Select(l => l.Reference));

        Assert.Equal("CH5604835012345678009", statement.Lines[4].CounterpartyIban);
    }

    [Fact]
    public void ReadsCsvWithOneAmountColumn()
    {
        // PostFinance-style: a few lines about the account before the header.
        var statement = BankStatementParser.ParseCsv("""
            Datum von:;01.11.2026
            Konto:;CH12 0900 0000 1234 5678 9
            Währung:;CHF

            Buchungsdatum;Avisierungstext;Betrag;Valuta;Saldo
            30.11.2026;"MIGROS BASEL M-PARC, KARTE 1234";-87.40;30.11.2026;4'512.60
            25.11.2026;LOHN NOVATEC AG;7'850.00;25.11.2026;4'600.00
            ;Total;;;
            """.Split('\n'));

        Assert.Equal("CH120900000012345678 9".Replace(" ", ""), statement.Iban);
        Assert.Equal(2, statement.Lines.Count);
        Assert.Equal(-87.40m, statement.Lines[0].Amount);
        Assert.Equal("MIGROS BASEL M-PARC, KARTE 1234", statement.Lines[0].Description);
        Assert.Equal(4512.60m, statement.ClosingBalance); // the newest booking's balance
        Assert.Equal(new DateOnly(2026, 11, 30), statement.ClosingDate);
    }

    [Fact]
    public void ReadsCsvWithDebitAndCreditColumns()
    {
        // UBS/ZKB-style: debits and credits in separate columns, text over several columns.
        var statement = BankStatementParser.ParseCsv("""
            Abschlussdatum,Beschreibung1,Beschreibung2,Belastung,Gutschrift,Saldo
            2026-11-02,Swisscom (Schweiz) AG,Rechnung 10/2026,79.00,,1'921.00
            2026-11-05,Kantonale Steuerverwaltung,Ratenzahlung,"1,150.00",,771.00
            2026-11-25,Praxis Seefeld,Lohn,,"4,200.00","4,971.00"
            """.Split('\n'));

        Assert.Equal([-79m, -1150m, 4200m], statement.Lines.Select(l => l.Amount));
        Assert.Equal("Swisscom (Schweiz) AG Rechnung 10/2026", statement.Lines[0].Description);
        Assert.Equal(4971m, statement.ClosingBalance);
    }

    [Fact]
    public void ReadsEuropeanNumbersAndTabSeparatedFiles()
    {
        var statement = BankStatementParser.ParseCsv(
            ["Date\tDescription\tAmount\tBalance", "21/11/2026\tCOOP-1234 BASEL\t-1.234,50\t10.000,00"]);

        var line = Assert.Single(statement.Lines);
        Assert.Equal(-1234.50m, line.Amount);
        Assert.Equal(10_000m, statement.ClosingBalance);
    }

    [Fact]
    public void RejectsFilesWithoutAStatement() =>
        Assert.Throws<InvalidOperationException>(() => BankStatementParser.ParseCsv(["Name;Phone", "Anna;079 123 45 67"]));

    [Theory]
    [InlineData("MIGROS BASEL M-PARC", false, "Groceries")]
    [InlineData("SBB CFF FFS Mobile", false, "Transport")]
    [InlineData("Helsana Versicherungen AG", false, "Health insurance")]
    [InlineData("Lohn November Novatec", true, "Salary")]
    [InlineData("Kantonale Steuerverwaltung", false, "Taxes")]
    [InlineData("Kita Sonnenschein", false, Categories.Childcare)]
    [InlineData("Some shop", false, "Other")]
    public void GuessesCategories(string description, bool isIncome, string category) =>
        Assert.Equal(category, CategoryGuesser.Guess(description, isIncome, []));

    [Fact]
    public void PrefersHowThePayeeWasCategorisedBefore()
    {
        var history = new[]
        {
            new Transaction { Description = "Bäckerei Kult", Category = "Groceries", Type = TransactionType.Expense, Date = new DateOnly(2026, 10, 1) },
        };
        Assert.Equal("Groceries", CategoryGuesser.Guess("BAECKEREI KULT Basel 4058", false, history));
    }
}
