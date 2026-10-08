using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class Attachment
{
    public Guid Id { get; set; }

    public string OwnerService { get; set; } = null!;

    public string OwnerType { get; set; } = null!;

    public Guid OwnerId { get; set; }

    public string FileUrl { get; set; } = null!;

    public string MimeType { get; set; } = null!;

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = null!;

    public string Phase { get; set; } = null!;

    public Guid? UploadedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}
