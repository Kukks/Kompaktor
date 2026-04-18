using Microsoft.EntityFrameworkCore;

namespace Kompaktor.Wallet.Data;

public class WalletDbContext : DbContext
{
    public WalletDbContext(DbContextOptions<WalletDbContext> options) : base(options) { }

    public DbSet<WalletEntity> Wallets => Set<WalletEntity>();
    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();
    public DbSet<AddressEntity> Addresses => Set<AddressEntity>();
    public DbSet<UtxoEntity> Utxos => Set<UtxoEntity>();
    public DbSet<TransactionEntity> Transactions => Set<TransactionEntity>();
    public DbSet<CoinJoinRecordEntity> CoinJoinRecords => Set<CoinJoinRecordEntity>();
    public DbSet<CoinJoinParticipationEntity> CoinJoinParticipations => Set<CoinJoinParticipationEntity>();
    public DbSet<CredentialEventEntity> CredentialEvents => Set<CredentialEventEntity>();
    public DbSet<LabelEntity> Labels => Set<LabelEntity>();
    public DbSet<AddressBookEntry> AddressBook => Set<AddressBookEntry>();
    public DbSet<PendingPaymentEntity> PendingPayments => Set<PendingPaymentEntity>();
    public DbSet<PrivacySnapshotEntity> PrivacySnapshots => Set<PrivacySnapshotEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WalletEntity>(e =>
        {
            e.HasKey(w => w.Id);
            e.HasMany(w => w.Accounts).WithOne(a => a.Wallet).HasForeignKey(a => a.WalletId);
        });

        modelBuilder.Entity<AccountEntity>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasMany(a => a.Addresses).WithOne(addr => addr.Account).HasForeignKey(addr => addr.AccountId);
        });

        modelBuilder.Entity<AddressEntity>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.ScriptPubKey);
            e.HasMany(a => a.Utxos).WithOne(u => u.Address).HasForeignKey(u => u.AddressId);
        });

        modelBuilder.Entity<UtxoEntity>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => new { u.TxId, u.OutputIndex }).IsUnique();
        });

        modelBuilder.Entity<TransactionEntity>(e =>
        {
            e.HasKey(t => t.Id);
        });

        modelBuilder.Entity<CoinJoinRecordEntity>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasOne(c => c.Transaction).WithMany().HasForeignKey(c => c.TransactionId);
            e.HasMany(c => c.Participations).WithOne(p => p.CoinJoinRecord).HasForeignKey(p => p.CoinJoinRecordId);
            e.Property(c => c.OutputValuesSat)
                .HasConversion(
                    v => string.Join(',', v),
                    v => string.IsNullOrEmpty(v) ? Array.Empty<long>() : v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray());
        });

        modelBuilder.Entity<CoinJoinParticipationEntity>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasOne(p => p.Utxo).WithMany().HasForeignKey(p => p.UtxoId);
        });

        modelBuilder.Entity<CredentialEventEntity>(e =>
        {
            e.HasKey(ce => ce.Id);
            e.HasOne(ce => ce.CoinJoinRecord).WithMany().HasForeignKey(ce => ce.CoinJoinRecordId);
            e.HasOne(ce => ce.ParentEvent).WithMany().HasForeignKey(ce => ce.ParentEventId);
            e.HasOne(ce => ce.OutputUtxo).WithMany().HasForeignKey(ce => ce.OutputUtxoId);
            e.HasIndex(ce => ce.CoinJoinRecordId);
            e.HasIndex(ce => ce.CredentialSerial);
        });

        modelBuilder.Entity<LabelEntity>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => new { l.EntityType, l.EntityId });
        });

        modelBuilder.Entity<AddressBookEntry>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasOne(a => a.Wallet).WithMany().HasForeignKey(a => a.WalletId);
        });

        modelBuilder.Entity<PendingPaymentEntity>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => new { p.WalletId, p.Direction, p.Status });
        });

        modelBuilder.Entity<PrivacySnapshotEntity>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.WalletId, s.Timestamp });
        });

        modelBuilder.Entity<FailedRoundInputEntity>(e =>
        {
            e.HasKey(f => f.Id);
            e.HasIndex(f => f.WalletId);
            e.HasIndex(f => f.RoundGroupId);
        });
    }

    public DbSet<FailedRoundInputEntity> FailedRoundInputs => Set<FailedRoundInputEntity>();
}
