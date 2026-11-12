using MonoSim.Models;

namespace MonoSim;

public sealed partial class MonoSimEngine
{
    public decimal LiquidPortfolioValue(string playerId)
    {
        var p = players[playerId];
        return p.CashBalance + p.OwnedPropertyIds.Sum(id => BuildingValue(properties[id])) +
            p.OwnedPropertyIds.Where(id => !properties[id].IsMortgaged).Sum(id => properties[id].MortgageValue) +
            p.HeldGetOutOfJailFreeCardIds.Length * 50;
    }

    private void Owe(string debtor, string? creditor, decimal amount, bool rent, string description)
    {
        if (amount <= 0 || players[debtor].IsBankrupt)
        {
            return;
        }
        if (creditor is not null && players[creditor].IsBankrupt)
        {
            creditor = null;
            rent = false;
        }
        debts.Add(new(++debtSequence, debtor, creditor, amount, 0, rent, description));
        Log("DebtCreated", debtor, $"Debt {debtSequence} to {creditor ?? "bank"}: {description}", amount);
    }

    private void DeclareBankruptcy(string id)
    {
        if (Owed(id) <= LiquidPortfolioValue(id))
        {
            throw new InvalidOperationException("Bankruptcy requires debt strictly greater than guaranteed liquid portfolio value.");
        }
        if (!players[id].HasDeclaredBankruptcy)
        {
            players[id] = players[id] with { HasDeclaredBankruptcy = true };
            Log("BankruptcyDeclared", id, "Provisional bankruptcy; liquidation and possible recovery follow.");
        }
    }

    private void SettleDebts()
    {
        if (resolvingDebt)
        {
            return;
        }
        resolvingDebt = true;
        try
        {
            foreach (var id in debts.Where(d => d.Remaining > 0 && !players[d.DebtorId].IsBankrupt).OrderBy(d => d.Sequence).Select(d => d.DebtorId).Distinct().ToArray())
            {
                if (ActiveCount <= 1)
                {
                    break;
                }
                SettlePlayerDebt(id);
            }
        }
        finally
        {
            resolvingDebt = false;
        }
    }

    private void SettlePlayerDebt(string id)
    {
        while (Owed(id) > 0 && !players[id].IsBankrupt)
        {
            var due = Owed(id);
            if (due > LiquidPortfolioValue(id))
            {
                DeclareBankruptcy(id);
            }
            if (players[id].HasDeclaredBankruptcy && players[id].CashBalance >= due)
            {
                players[id] = players[id] with { HasDeclaredBankruptcy = false };
                Log("BankruptcyReversed", id, "Actual cash now covers all remaining debts.");
            }
            PayOldestDebts(id);
            if (Owed(id) == 0)
            {
                if (players[id].HasDeclaredBankruptcy)
                {
                    players[id] = players[id] with { HasDeclaredBankruptcy = false };
                    Log("BankruptcyReversed", id, "All debts settled in cash.");
                }
                return;
            }
            var cashBefore = players[id].CashBalance;
            var previousDecision = decisionPlayer;
            decisionPlayer = id;
            try
            {
                strategies[id].PostRoll(CopyPlayer(players[id]), Snapshot(id))();
            }
            finally
            {
                decisionPlayer = previousDecision;
            }
            if (players[id].CashBalance > cashBefore)
            {
                continue;
            }
            var buildings = players[id].OwnedPropertyIds.Select(p => properties[p]).Where(p => BuildingValue(p) > 0)
                .GroupBy(p => p.ColorGroup).OrderBy(g => g.Sum(BuildingValue)).FirstOrDefault();
            if (buildings is not null)
            {
                SellBuildings(id, buildings.Select(p => p.PropertyId).ToArray());
                continue;
            }
            if (players[id].HeldGetOutOfJailFreeCardIds.Length > 0)
            {
                RedeemCard(id, players[id].HeldGetOutOfJailFreeCardIds[0]);
                continue;
            }
            if (!players[id].HasDeclaredBankruptcy)
            {
                var mortgage = players[id].OwnedPropertyIds.Select(p => properties[p]).Where(p => !p.IsMortgaged).OrderBy(p => p.MortgageValue).FirstOrDefault();
                if (mortgage is not null)
                {
                    Mortgage(id, mortgage.PropertyId);
                    continue;
                }
                DeclareBankruptcy(id);
            }
            if (players[id].OwnedPropertyIds.Length > 0)
            {
                var first = properties[players[id].OwnedPropertyIds[0]];
                var bundle = new AssetBundle { PropertyIds = [first.PropertyId] };
                if (first.ColorGroup is not null && Group(first).All(p => Owner(p.PropertyId) == id))
                {
                    bundle = bundle with { PropertyIds = Group(first).Select(p => p.PropertyId).ToArray() };
                }
                Auction(id, bundle);
                continue;
            }
            if (players[id].CashBalance != 0 || players[id].HeldGetOutOfJailFreeCardIds.Length != 0)
            {
                throw new InvalidOperationException("Final bankruptcy requires no cash and no remaining assets.");
            }
            players[id] = players[id] with { IsBankrupt = true, BankruptcyRound = round, IsInJail = false, FailedJailRolls = 0 };
            Log("BankruptcyFinalized", id, $"No assets or cash remain; unpaid debt {Owed(id)} stays recorded.");
        }
    }

    private void PayOldestDebts(string id)
    {
        for (var index = 0; index < debts.Count && players[id].CashBalance > 0; index++)
        {
            var debt = debts[index];
            if (debt.DebtorId != id || debt.Remaining <= 0)
            {
                continue;
            }
            var amount = Math.Min(players[id].CashBalance, debt.Remaining);
            var creditor = debt.CreditorId;
            if (creditor is not null && players[creditor].IsBankrupt)
            {
                creditor = null;
            }
            TransferCash(id, creditor, amount, $"Settle debt {debt.Sequence}: {debt.Description}");
            debts[index] = debt with { Paid = debt.Paid + amount };
            Log("DebtPayment", id, $"Debt {debt.Sequence}: paid {amount}, remaining {debts[index].Remaining}.", amount);
        }
    }
}
