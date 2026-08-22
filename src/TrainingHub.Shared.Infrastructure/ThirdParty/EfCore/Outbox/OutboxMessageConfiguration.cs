using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TrainingHub.Shared.Infrastructure.ThirdParty.EfCore.Outbox;

/// <summary>
/// Maps the outbox envelope onto its table.
/// </summary>
public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    /// <summary>
    /// Maps the envelope columns, and the one index the delivery worker will scan.
    /// </summary>
    /// <remarks>
    /// Every column is declared explicitly, and not out of thoroughness: the envelope's properties
    /// are get-only, and a get-only property EF Core was not told about is silently not a column.
    /// Measured, not assumed — <c>Version</c>, <c>Attempts</c> and <c>Error</c> were missing from
    /// the first draft of this table's migration, with nothing but a smaller file to show for it.
    /// </remarks>
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessage");

        builder.HasKey(message => message.Id);

        // Minted by the publisher — a version-7 GUID, so key order follows insertion order —
        // never by the database: the id is the delivery ledger's deduplication key, and it has to
        // exist before the row does.
        builder.Property(message => message.Id)
            .ValueGeneratedNever();

        builder.Property(message => message.Name)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(message => message.Version)
            .IsRequired();

        builder.Property(message => message.Payload)
            .IsRequired();

        // A W3C traceparent is exactly fifty-five characters — two-digit version, dash,
        // thirty-two-digit trace id, dash, sixteen-digit span id, dash, two-digit flags — and
        // the column says so rather than defaulting to max (ADR 0097).
        builder.Property(message => message.TraceParent)
            .HasMaxLength(55);

        builder.Property(message => message.Attempts)
            .IsRequired();

        builder.Property(message => message.Error);

        builder.Property(message => message.ClaimedBy)
            .HasMaxLength(128);

        builder.Property(message => message.ClaimedUntil)
            .HasPrecision(7);

        // Full precision, declared explicitly rather than left to the default — the same decision
        // the audit columns carry. See ADR 0005.
        builder.Property(message => message.OccurredOnUtc)
            .HasPrecision(7);

        builder.Property(message => message.ProcessedOnUtc)
            .HasPrecision(7);

        builder.Property(message => message.NextAttemptOnUtc)
            .HasPrecision(7);

        builder.Property(message => message.RequeuedOnUtc)
            .HasPrecision(7);

        // Computed from two columns that are mapped, so it is a question rather than a fact and has
        // no column of its own. Said out loud because this configuration declares everything: an
        // unmapped member here is a decision, not a member somebody forgot.
        builder.Ignore(message => message.MayHaveSettledConsumers);

        // The worker's one question — "what is owed, oldest first" — answered by an index that
        // holds only the owed rows: delivered messages fall out of it, so it stays small no matter
        // how much history the table accumulates.
        builder.HasIndex(message => message.OccurredOnUtc)
            .HasFilter("[ProcessedOnUtc] IS NULL")
            .HasDatabaseName("IX_OutboxMessage_Unprocessed");

        // The sweep's mirror question — "what is delivered, oldest first" — so trimming the
        // history past its retention is a range seek that finds nothing almost every poll, which
        // is what makes sweeping on every poll defensible. See ADR 0033.
        builder.HasIndex(message => message.ProcessedOnUtc)
            .HasFilter("[ProcessedOnUtc] IS NOT NULL")
            .HasDatabaseName("IX_OutboxMessage_Delivered");
    }
}
