using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Eisenberg.Data;

public sealed class EisenbergDb(DbContextOptions<EisenbergDb> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<FilledFormInfo> FilledForms => Set<FilledFormInfo>();
    public DbSet<SettingsMismatch> SettingsMismatches => Set<SettingsMismatch>();
    public DbSet<FormChangeReport> FormChangeReports => Set<FormChangeReport>();
    public DbSet<GlobalContentUnderstanding> GlobalContentUnderstanding => Set<GlobalContentUnderstanding>();
    public DbSet<FormDefinition> Forms => Set<FormDefinition>();
    public DbSet<InterpretationRun> InterpretationRuns => Set<InterpretationRun>();
    public DbSet<FormModelVersion> ModelVersions => Set<FormModelVersion>();
    public DbSet<ChoiceList> ChoiceLists => Set<ChoiceList>();
    public DbSet<FormPublication> FormPublications => Set<FormPublication>();
    public DbSet<RunArtifact> RunArtifacts => Set<RunArtifact>();
    public DbSet<FormInstance> Instances => Set<FormInstance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Membership>().HasKey(m => new { m.UserId, m.OrganizationId });
        modelBuilder.Entity<Membership>()
            .HasOne(m => m.User)
            .WithMany(u => u.Memberships)
            .HasForeignKey(m => m.UserId);
        modelBuilder.Entity<Membership>()
            .HasOne(m => m.Organization)
            .WithMany(o => o.Memberships)
            .HasForeignKey(m => m.OrganizationId);
        modelBuilder.Entity<StoredFile>()
            .HasOne(f => f.Organization)
            .WithMany()
            .HasForeignKey(f => f.OrganizationId);

        modelBuilder.Entity<InterpretationRun>()
            .HasOne(r => r.Form)
            .WithMany(f => f.Runs)
            .HasForeignKey(r => r.FormId);
        modelBuilder.Entity<InterpretationRun>().Property(r => r.ReportJson).HasColumnType("jsonb");
        modelBuilder.Entity<FormModelVersion>().Property(m => m.ModelJson).HasColumnType("jsonb");
        modelBuilder.Entity<FormModelVersion>().HasIndex(m => new { m.FormId, m.Number }).IsUnique();
        modelBuilder.Entity<FormModelVersion>().HasOne<FormDefinition>().WithMany().HasForeignKey(m => m.FormId);
        modelBuilder.Entity<FormModelVersion>().HasOne<InterpretationRun>().WithMany().HasForeignKey(m => m.RunId);
        modelBuilder.Entity<ChoiceList>().Property(c => c.DependsOnJson).HasColumnType("jsonb");
        modelBuilder.Entity<ChoiceList>().Property(c => c.ChoicesJson).HasColumnType("jsonb");
        modelBuilder.Entity<ChoiceList>().HasOne<FormModelVersion>().WithMany().HasForeignKey(c => c.ModelId);
        modelBuilder.Entity<FormPublication>().HasKey(p => new { p.FormId, p.OrganizationId });
        modelBuilder.Entity<FormPublication>()
            .HasOne<FormDefinition>()
            .WithMany(f => f.Publications)
            .HasForeignKey(p => p.FormId);
        modelBuilder.Entity<FormPublication>().HasOne<Organization>().WithMany().HasForeignKey(p => p.OrganizationId);
        modelBuilder.Entity<RunArtifact>().HasIndex(a => new { a.RunId, a.Sequence });
        modelBuilder.Entity<RunArtifact>().HasOne<InterpretationRun>().WithMany().HasForeignKey(a => a.RunId);
        modelBuilder.Entity<FormInstance>().Property(i => i.ValuesJson).HasColumnType("jsonb");
        modelBuilder.Entity<FormInstance>().Property(i => i.SourcesJson).HasColumnType("jsonb");
        modelBuilder.Entity<FormInstance>().HasIndex(i => new { i.OrganizationId, i.ModelId });
        modelBuilder.Entity<FormChangeReport>().HasIndex(r => r.HandledAt);
    }
}

public static class Schema
{
    public static async Task EnsureAsync(EisenbergDb db, CancellationToken cancellationToken = default)
    {
        if (await db.Database.EnsureCreatedAsync(cancellationToken))
            return;

        // EnsureCreated does nothing when the database exists, so tables added later are created here.
        var script = db.Database.GenerateCreateScript();
        script = Regex.Replace(script, @"\bCREATE TABLE\b", "CREATE TABLE IF NOT EXISTS");
        script = Regex.Replace(script, @"\bCREATE (UNIQUE )?INDEX\b", "CREATE ${1}INDEX IF NOT EXISTS");
        script += """
            ALTER TABLE "Files" ADD COLUMN IF NOT EXISTS "StatusText" text NOT NULL DEFAULT '';
            ALTER TABLE "Instances" ADD COLUMN IF NOT EXISTS "SourcesJson" jsonb NOT NULL DEFAULT '{}';
            ALTER TABLE "FilledForms" ADD COLUMN IF NOT EXISTS "FormAddress" text NOT NULL DEFAULT '';
            ALTER TABLE "FilledForms" ADD COLUMN IF NOT EXISTS "RecordLabel" text NOT NULL DEFAULT '';
            ALTER TABLE "FilledForms" ADD COLUMN IF NOT EXISTS "InstanceId" uuid NULL;
            ALTER TABLE "SettingsMismatches" ADD COLUMN IF NOT EXISTS "HandledAt" timestamp with time zone NULL;
            """;
        await db.Database.ExecuteSqlRawAsync(script, cancellationToken);
    }
}

public static class ConnectionStrings
{
    public static string ToNpgsql(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var uri = new Uri(value);
        var info = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(info[0]);
        var password = info.Length > 1 ? Uri.UnescapeDataString(info[1]) : "";
        var database = uri.AbsolutePath.Trim('/');
        var port = uri.IsDefaultPort ? 5432 : uri.Port;
        var builder = $"Host={uri.Host};Port={port};Database={database};Username={user};Password={password}";
        if (uri.Query.Contains("sslmode=require", StringComparison.OrdinalIgnoreCase))
            builder += ";SSL Mode=Require;Trust Server Certificate=true";
        return builder;
    }
}
