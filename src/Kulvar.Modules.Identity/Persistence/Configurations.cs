using Kulvar.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kulvar.Modules.Identity.Persistence;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Email).HasMaxLength(254);
        b.Property(x => x.NormalizedEmail).HasMaxLength(254);
        b.Property(x => x.DisplayName).HasMaxLength(100);
        b.Property(x => x.PasswordHash).HasMaxLength(500);
        b.HasIndex(x => x.NormalizedEmail).IsUnique();
        b.Ignore(x => x.IsEmailVerified);
    }
}

internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> b)
    {
        b.ToTable("sessions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.UserId);
    }
}

internal sealed class UserTokenConfiguration : IEntityTypeConfiguration<UserToken>
{
    public void Configure(EntityTypeBuilder<UserToken> b)
    {
        b.ToTable("user_tokens");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Purpose).HasMaxLength(40);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.UserId);
    }
}

internal sealed class PlatformOperatorConfiguration : IEntityTypeConfiguration<PlatformOperator>
{
    public void Configure(EntityTypeBuilder<PlatformOperator> b)
    {
        b.ToTable("platform_operators");
        b.HasKey(x => x.UserId);
        b.Property(x => x.UserId).ValueGeneratedNever();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
