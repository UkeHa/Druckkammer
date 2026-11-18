using Druckkammer.Web.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Druckkammer.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole, string>(options)
{
    public DbSet<InviteToken> InviteTokens => Set<InviteToken>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<NotificationWebhook> NotificationWebhooks => Set<NotificationWebhook>();

    public DbSet<ModelItem> ModelItems => Set<ModelItem>();
    public DbSet<ModelFile> ModelFiles => Set<ModelFile>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ModelCollectionMap> ModelCollectionMaps => Set<ModelCollectionMap>();
    public DbSet<ModelTagMap> ModelTagMaps => Set<ModelTagMap>();
    public DbSet<UserFavoriteModel> UserFavoriteModels => Set<UserFavoriteModel>();
    public DbSet<ModelMake> ModelMakes => Set<ModelMake>();

    public DbSet<Printer> Printers => Set<Printer>();
    public DbSet<Material> Materials => Set<Material>();
    public DbSet<ExposureProfile> ExposureProfiles => Set<ExposureProfile>();
    public DbSet<PrintOrder> PrintOrders => Set<PrintOrder>();
    public DbSet<PrintBatch> PrintBatches => Set<PrintBatch>();
    public DbSet<PrintRequest> PrintRequests => Set<PrintRequest>();
    public DbSet<PrintRequestComment> PrintRequestComments => Set<PrintRequestComment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<InviteToken>()
            .HasIndex(x => x.Code)
            .IsUnique();

        builder.Entity<ModelFile>()
            .HasIndex(x => x.Sha256Hash);

        builder.Entity<Collection>()
            .HasOne(c => c.ParentCollection)
            .WithMany(c => c.ChildCollections)
            .HasForeignKey(c => c.ParentCollectionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<ModelCollectionMap>()
            .HasKey(mc => new { mc.ModelItemId, mc.CollectionId });

        builder.Entity<ModelCollectionMap>()
            .HasOne(mc => mc.ModelItem)
            .WithMany(m => m.Collections)
            .HasForeignKey(mc => mc.ModelItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ModelCollectionMap>()
            .HasOne(mc => mc.Collection)
            .WithMany(c => c.Models)
            .HasForeignKey(mc => mc.CollectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ModelTagMap>()
            .HasKey(mt => new { mt.ModelItemId, mt.TagId });

        builder.Entity<ModelTagMap>()
            .HasOne(mt => mt.ModelItem)
            .WithMany(m => m.Tags)
            .HasForeignKey(mt => mt.ModelItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ModelTagMap>()
            .HasOne(mt => mt.Tag)
            .WithMany(t => t.Models)
            .HasForeignKey(mt => mt.TagId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<UserFavoriteModel>()
            .HasKey(uf => new { uf.UserId, uf.ModelItemId });

        builder.Entity<UserFavoriteModel>()
            .HasOne(uf => uf.ModelItem)
            .WithMany(m => m.Favorites)
            .HasForeignKey(uf => uf.ModelItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<PrintRequest>()
            .HasOne(pr => pr.ModelItem)
            .WithMany()
            .HasForeignKey(pr => pr.ModelItemId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<PrintRequest>()
            .HasOne(pr => pr.SelectedModelFile)
            .WithMany()
            .HasForeignKey(pr => pr.SelectedModelFileId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<PrintRequest>()
            .HasOne(pr => pr.PrintOrder)
            .WithMany(po => po.Requests)
            .HasForeignKey(pr => pr.PrintOrderId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<PrintRequest>()
            .HasOne(pr => pr.PrintBatch)
            .WithMany(pb => pb.Requests)
            .HasForeignKey(pr => pr.PrintBatchId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<PrintRequest>()
            .HasOne(pr => pr.PreferredMaterial)
            .WithMany()
            .HasForeignKey(pr => pr.PreferredMaterialId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
