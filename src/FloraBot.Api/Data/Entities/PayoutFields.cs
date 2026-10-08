namespace FloraBot.Api.Data.Entities;

public partial class Payment
{
    public Guid? PaidBy { get; set; }
}

public partial class WithdrawalRequest
{
    public Guid? PaidBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
}
