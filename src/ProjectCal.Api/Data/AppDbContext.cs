using Microsoft.EntityFrameworkCore;
using ProjectCal.Api.Data.Entities;

namespace ProjectCal.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();
    public DbSet<NoteEntity> Notes => Set<NoteEntity>();
    public DbSet<AttachmentEntity> Attachments => Set<AttachmentEntity>();
    public DbSet<TranscriptEntity> Transcripts => Set<TranscriptEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserEntity>(entity =>
        {
            entity.HasIndex(x => x.NormalizedEmail).IsUnique();
            entity.Property(x => x.Email).HasMaxLength(320);
            entity.Property(x => x.NormalizedEmail).HasMaxLength(320);
        });

        modelBuilder.Entity<RefreshTokenEntity>(entity =>
        {
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasOne(x => x.User).WithMany(x => x.RefreshTokens).HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<NoteEntity>(entity =>
        {
            entity.HasIndex(x => new { x.UserId, x.Date, x.StartTime });
            entity.HasIndex(x => new { x.UserId, x.UpdatedAt });
            entity.Property(x => x.Title).HasMaxLength(200);
            entity.HasOne(x => x.User).WithMany(x => x.Notes).HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<AttachmentEntity>(entity =>
        {
            entity.HasIndex(x => new { x.UserId, x.NoteId });
            entity.Property(x => x.FileName).HasMaxLength(260);
            entity.Property(x => x.MimeType).HasMaxLength(160);
            entity.HasOne(x => x.Note).WithMany(x => x.Attachments).HasForeignKey(x => x.NoteId);
        });

        modelBuilder.Entity<TranscriptEntity>(entity =>
        {
            entity.HasIndex(x => new { x.UserId, x.Status });
            entity.HasIndex(x => new { x.UserId, x.UpdatedAt });
            entity.HasIndex(x => x.AttachmentId).IsUnique();
            entity.Property(x => x.Language).HasMaxLength(16);
            entity.HasOne(x => x.Note).WithMany(x => x.Transcripts).HasForeignKey(x => x.NoteId);
            entity.HasOne(x => x.Attachment).WithOne().HasForeignKey<TranscriptEntity>(x => x.AttachmentId);
        });
    }
}
