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
    public string StatusText { get; set; } = "";
    public bool? ContentUnderstandingEnabled { get; set; }
    public string? Reading { get; set; }
    public Organization Organization { get; set; } = null!;
}

public sealed class FilledFormInfo
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Summary { get; set; } = "";
    public string FormAddress { get; set; } = "";
    public string RecordLabel { get; set; } = "";
    public Guid? InstanceId { get; set; }
}

public sealed class FormChangeReport
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid FormId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset? HandledAt { get; set; }
}

public sealed class SettingsMismatch
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset? HandledAt { get; set; }
}

public sealed class GlobalContentUnderstanding
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
}

public sealed class FormDefinition
{
    public Guid Id { get; set; }
    public string Address { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? PublishedModelId { get; set; }
    public List<InterpretationRun> Runs { get; set; } = [];
    public List<FormPublication> Publications { get; set; } = [];
}

public static class RunStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string WaitingForSignIn = "waiting-for-sign-in";
    public const string BuildingModel = "building-model";
    public const string Checking = "checking";
    public const string Finished = "finished";
    public const string Failed = "failed";
    public const string NotConnected = "not-connected";

    public static bool IsActive(string status) =>
        status is Running or WaitingForSignIn or BuildingModel or Checking;
}

public sealed class InterpretationRun
{
    public Guid Id { get; set; }
    public Guid FormId { get; set; }
    public string Description { get; set; } = "";
    public string Status { get; set; } = RunStatus.Queued;
    public string StatusText { get; set; } = "";
    public string? LiveViewUrl { get; set; }
    public bool SignInConfirmed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int ActionCount { get; set; }
    public int StateCount { get; set; }
    public string? ReportJson { get; set; }
    public FormDefinition Form { get; set; } = null!;
}

public static class ModelStatus
{
    public const string Draft = "draft";
    public const string Published = "published";
}

public sealed class FormModelVersion
{
    public Guid Id { get; set; }
    public Guid FormId { get; set; }
    public Guid RunId { get; set; }
    public int Number { get; set; }
    public string Status { get; set; } = ModelStatus.Draft;
    public string ModelJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed class ChoiceList
{
    public Guid Id { get; set; }
    public Guid ModelId { get; set; }
    public string ControlKey { get; set; } = "";
    public bool IsComplete { get; set; }
    public string DependsOnJson { get; set; } = "[]";
    public string ChoicesJson { get; set; } = "[]";
}

public sealed class FormPublication
{
    public Guid FormId { get; set; }
    public Guid OrganizationId { get; set; }
}

public sealed class RunArtifact
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public int Sequence { get; set; }
    public string Kind { get; set; } = "";
    public string Caption { get; set; } = "";
    public string StorageLocation { get; set; } = "";
    public string ContentType { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class FormInstance
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ModelId { get; set; }
    public string TypeName { get; set; } = "";
    public string? SubclassName { get; set; }
    public string Label { get; set; } = "";
    public string ValuesJson { get; set; } = "{}";
    public string SourcesJson { get; set; } = "{}";
    public bool Generated { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
