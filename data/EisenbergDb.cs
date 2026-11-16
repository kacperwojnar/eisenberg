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
    public DbSet<GlobalContentUnderstanding> GlobalContentUnderstanding => Set<GlobalContentUnderstanding>();

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
