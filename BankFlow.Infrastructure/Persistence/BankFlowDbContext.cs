using BankFlow.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BankFlow.Infrastructure.Persistence;

public sealed class BankFlowDbContext : DbContext
{
    public BankFlowDbContext(DbContextOptions<BankFlowDbContext> options) : base(options)
    {
    }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<Beneficiary> Beneficiaries => Set<Beneficiary>();
    public DbSet<ScheduledPayment> ScheduledPayments => Set<ScheduledPayment>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(message => message.Id);
            entity.Property(message => message.RecipientUserId).HasMaxLength(450).IsRequired();
            entity.Property(message => message.Type).HasMaxLength(80).IsRequired();
            entity.Property(message => message.Payload).HasMaxLength(2000).IsRequired();
            entity.Property(message => message.Version).IsConcurrencyToken();
            entity.HasIndex(message => new { message.ProcessedAt, message.RealtimeSentAt, message.LeaseUntil, message.OccurredAt });
            entity.HasIndex(message => message.AggregateId);
        });
        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(notification => notification.Id);
            entity.Property(notification => notification.RecipientUserId).HasMaxLength(450).IsRequired();
            entity.Property(notification => notification.Type).HasMaxLength(80).IsRequired();
            entity.Property(notification => notification.Payload).HasMaxLength(2000).IsRequired();
            entity.HasIndex(notification => notification.OutboxMessageId).IsUnique();
            entity.HasIndex(notification => new { notification.RecipientUserId, notification.CreatedAt, notification.Id });
        });
        var utcInstant = new ValueConverter<DateTimeOffset, long>(instant => instant.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
        var utcNullableInstant = new ValueConverter<DateTimeOffset?, long?>(instant => instant.HasValue ? instant.Value.UtcTicks : null,
            ticks => ticks.HasValue ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null);
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.Property(message => message.OccurredAt).HasConversion(utcInstant);
            entity.Property(message => message.ProcessedAt).HasConversion(utcNullableInstant);
            entity.Property(message => message.RealtimeSentAt).HasConversion(utcNullableInstant);
            entity.Property(message => message.LeaseUntil).HasConversion(utcNullableInstant);
        });
        modelBuilder.Entity<Notification>(entity =>
        {
            entity.Property(notification => notification.CreatedAt).HasConversion(utcInstant);
            entity.Property(notification => notification.ReadAt).HasConversion(utcNullableInstant);
        });
        modelBuilder.Entity<Beneficiary>(entity =>
        {
            entity.HasKey(beneficiary => beneficiary.Id);
            entity.Property(beneficiary => beneficiary.OwnerUserId).HasMaxLength(450).IsRequired();
            entity.Property(beneficiary => beneficiary.Name).HasMaxLength(80).IsRequired();
            entity.Property(beneficiary => beneficiary.AccountNumber).HasMaxLength(25).IsRequired();
            entity.Property(beneficiary => beneficiary.Version).IsConcurrencyToken();
            entity.Property(beneficiary => beneficiary.CreatedAt).HasConversion(utcInstant);
            entity.HasIndex(beneficiary => new { beneficiary.OwnerUserId, beneficiary.AccountNumber }).IsUnique().HasFilter("[Active] = 1");
        });
        modelBuilder.Entity<ScheduledPayment>(entity =>
        {
            entity.HasKey(payment => payment.Id);
            entity.Property(payment => payment.OwnerUserId).HasMaxLength(450).IsRequired();
            entity.Property(payment => payment.CreatedByUserId).HasMaxLength(450).IsRequired();
            entity.Property(payment => payment.BeneficiaryName).HasMaxLength(80).IsRequired();
            entity.Property(payment => payment.DestinationAccountNumber).HasMaxLength(25).IsRequired();
            entity.Property(payment => payment.Description).HasMaxLength(140).IsRequired();
            entity.Property(payment => payment.Amount).HasColumnType("decimal(18,2)");
            entity.Property(payment => payment.FailureCode).HasMaxLength(80);
            entity.Property(payment => payment.Version).IsConcurrencyToken();
            entity.Property(payment => payment.NextRunAt).HasConversion(utcInstant);
            entity.Property(payment => payment.FirstRunAt).HasConversion(utcInstant);
            entity.Property(payment => payment.NextAttemptAt).HasConversion(utcInstant);
            entity.Property(payment => payment.CreatedAt).HasConversion(utcInstant);
            entity.Property(payment => payment.LastRunAt).HasConversion(utcInstant);
            entity.Property(payment => payment.LeaseUntil).HasConversion(utcInstant);
            entity.HasIndex(payment => new { payment.Status, payment.NextAttemptAt });
            entity.HasIndex(payment => new { payment.OwnerUserId, payment.CreatedAt });
            entity.HasIndex(payment => new { payment.CreatedByUserId, payment.CreationKey }).IsUnique();
            entity.HasOne<Account>().WithMany().HasForeignKey(payment => payment.SourceAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CustomerName).IsRequired().HasMaxLength(100);
            entity.Property(x => x.AccountNumber).IsRequired().HasMaxLength(25);
            entity.HasIndex(x => x.AccountNumber).IsUnique();
            entity.Property(x => x.OwnerUserId).HasMaxLength(450).IsConcurrencyToken();
            entity.HasIndex(x => x.OwnerUserId);
            entity.HasIndex(x => new { x.OwnerUserId, x.CreatedAt, x.Id })
                .IncludeProperties(x => new { x.CustomerName, x.AccountNumber, x.Balance });
            entity.HasIndex(x => new { x.CreatedAt, x.Id })
                .IncludeProperties(x => new { x.CustomerName, x.AccountNumber, x.Balance, x.OwnerUserId });
            entity.Property(x => x.Balance).HasColumnType("decimal(18,2)").IsConcurrencyToken();
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).IsRequired().HasMaxLength(50);
            entity.Property(x => x.Description).IsRequired().HasMaxLength(200);
            entity.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            entity.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
            entity.HasOne<Transfer>().WithMany().HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.AccountId, x.CreatedAt, x.Id })
                .IncludeProperties(x => new { x.Amount, x.Type, x.Description, x.TransferId });
        });

        modelBuilder.Entity<Transfer>(entity =>
        {
            entity.HasKey(transfer => transfer.Id);
            entity.Property(transfer => transfer.ActorUserId).IsRequired().HasMaxLength(450);
            entity.Property(transfer => transfer.DestinationAccountNumber).IsRequired().HasMaxLength(25);
            entity.Property(transfer => transfer.Description).IsRequired().HasMaxLength(140);
            entity.Property(transfer => transfer.Amount).HasColumnType("decimal(18,2)");
            entity.HasIndex(transfer => new { transfer.ActorUserId, transfer.IdempotencyKey }).IsUnique();
            entity.HasOne<Account>().WithMany().HasForeignKey(transfer => transfer.SourceAccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Account>().WithMany().HasForeignKey(transfer => transfer.DestinationAccountId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
