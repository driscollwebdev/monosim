namespace MonoSim.Models;

public sealed record DebtState(long Sequence, string DebtorId, string? CreditorId,
    decimal Amount, decimal Paid, bool IsRent, string Description)
{
    public decimal Remaining => Amount - Paid;
}
