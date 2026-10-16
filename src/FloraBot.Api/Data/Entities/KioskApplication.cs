using System;

namespace FloraBot.Api.Data.Entities;

public partial class KioskApplication
{
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }

    public Guid PackageId { get; set; }

    public string ShopName { get; set; } = null!;

    public string Phone { get; set; } = null!;

    public string ContactEmail { get; set; } = null!;

    public string InstallationAddress { get; set; } = null!;

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public string BankName { get; set; } = null!;

    public string BankHolder { get; set; } = null!;

    public string BankAccountNoEnc { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string? PackageName { get; set; }

    public long? AnnualFee { get; set; }

    public int? WalkInSlots { get; set; }

    public int? PreorderSlots { get; set; }

    public int? TotalSlots { get; set; }

    public Guid? PaymentId { get; set; }

    public Guid? AssignedTechnicianId { get; set; }

    public Guid? AssignedKioskId { get; set; }

    public DateOnly? ScheduledDate { get; set; }

    public string? TimeWindow { get; set; }

    public TimeOnly? StartTime { get; set; }

    public TimeOnly? EndTime { get; set; }

    public string? ReviewNote { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public string? TechnicianReport { get; set; }

    public string? ReworkReason { get; set; }

    public DateTime? HandoverAt { get; set; }

    public Guid? HandoverBy { get; set; }

    public Guid? SellerId { get; set; }

    public Guid? RefundApprovedBy { get; set; }

    public Guid? RefundPaidBy { get; set; }

    public string? RefundReference { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual User Customer { get; set; } = null!;

    public virtual SubscriptionPackage Package { get; set; } = null!;

    public virtual Payment? Payment { get; set; }

    public virtual User? AssignedTechnician { get; set; }

    public virtual Kiosk? AssignedKiosk { get; set; }

    public virtual Seller? Seller { get; set; }

    public virtual User? HandoverByUser { get; set; }
}
