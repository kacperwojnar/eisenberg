namespace Eisenberg.Data;

public sealed class Organization
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string SettingsText { get; set; } = "";
    public List<Membership> Memberships { get; set; } = [];
}

public sealed class AppUser
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string Role { get; set; } = "user";
    public bool? ContentUnderstandingEnabled { get; set; }
    public List<Membership> Memberships { get; set; } = [];
}

public sealed class Membership
{
    public Guid UserId { get; set; }
    public Guid OrganizationId { get; set; }
    public AppUser User { get; set; } = null!;
    public Organization Organization { get; set; } = null!;
}

public sealed class StoredFile
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UploadedByUserId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string StorageLocation { get; set; } = "";
    public DateTimeOffset UploadedAt { get; set; }
    public string Status { get; set; } = "stored";
    public bool? ContentUnderstandingEnabled { get; set; }
    public Organization Organization { get; set; } = null!;
}

public sealed class FilledFormInfo
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Summary { get; set; } = "";
}

public sealed class SettingsMismatch
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Text { get; set; } = "";
}

public sealed class GlobalContentUnderstanding
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
}
