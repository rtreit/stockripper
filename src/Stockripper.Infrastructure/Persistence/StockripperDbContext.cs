using Microsoft.EntityFrameworkCore;

namespace Stockripper.Infrastructure.Persistence;

public sealed class StockripperDbContext(DbContextOptions<StockripperDbContext> options)
    : DbContext(options)
{
    internal DbSet<GoalDraftRow> GoalDrafts => Set<GoalDraftRow>();

    internal DbSet<GoalVersionRow> GoalVersions => Set<GoalVersionRow>();

    internal DbSet<GoalActivationRow> GoalActivations => Set<GoalActivationRow>();

    internal DbSet<SessionRow> Sessions => Set<SessionRow>();

    internal DbSet<ExecutableSessionPreflightRow> ExecutableSessionPreflights =>
        Set<ExecutableSessionPreflightRow>();

    internal DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();

    internal DbSet<AuditChainHeadRow> AuditChainHeads => Set<AuditChainHeadRow>();

    internal DbSet<DataSnapshotRow> DataSnapshots => Set<DataSnapshotRow>();

    internal DbSet<ReplayResultRow> ReplayResults => Set<ReplayResultRow>();

    internal DbSet<DecisionRow> Decisions => Set<DecisionRow>();

    internal DbSet<RiskEvaluationRow> RiskEvaluations => Set<RiskEvaluationRow>();

    internal DbSet<ExecutionIntentRow> ExecutionIntents => Set<ExecutionIntentRow>();

    internal DbSet<ReviewApprovalRow> ReviewApprovals => Set<ReviewApprovalRow>();
    internal DbSet<KillSwitchRow> KillSwitches => Set<KillSwitchRow>();
    internal DbSet<ReconciliationRow> Reconciliations => Set<ReconciliationRow>();
    internal DbSet<ExposureReservationRow> ExposureReservations => Set<ExposureReservationRow>();
    internal DbSet<PreSubmitEvaluationRow> PreSubmitEvaluations => Set<PreSubmitEvaluationRow>();
    internal DbSet<OrderRow> Orders => Set<OrderRow>();
    internal DbSet<CancellationRequestRow> CancellationRequests => Set<CancellationRequestRow>();
    internal DbSet<BrokerOrderEventRow> BrokerOrderEvents => Set<BrokerOrderEventRow>();
    internal DbSet<BrokerExecutionEventRow> BrokerExecutionEvents => Set<BrokerExecutionEventRow>();
    internal DbSet<FillRow> Fills => Set<FillRow>();
    internal DbSet<PositionSnapshotBatchRow> PositionSnapshotBatches => Set<PositionSnapshotBatchRow>();
    internal DbSet<PositionSnapshotRow> PositionSnapshots => Set<PositionSnapshotRow>();
    internal DbSet<PostFillEvaluationRow> PostFillEvaluations => Set<PostFillEvaluationRow>();
    internal DbSet<PostFillSafetyResponseRow> PostFillSafetyResponses => Set<PostFillSafetyResponseRow>();

    internal DbSet<ResearchResultRow> ResearchResults => Set<ResearchResultRow>();

    internal DbSet<AccountAllocationLimitRow> AccountAllocationLimits => Set<AccountAllocationLimitRow>();

    internal DbSet<AccountAllocationEntryRow> AccountAllocationEntries => Set<AccountAllocationEntryRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GoalDraftRow>(entity =>
        {
            entity.ToTable("goal_drafts", table =>
            {
                table.HasCheckConstraint("ck_goal_drafts_revision", "revision > 0");
                table.HasCheckConstraint("ck_goal_drafts_version", "version_number > 0");
                table.HasCheckConstraint("ck_goal_drafts_state", "state IN ('DRAFT', 'PUBLISHED')");
                table.HasCheckConstraint(
                    "ck_goal_drafts_publication",
                    "(state = 'DRAFT' AND published_at IS NULL) OR (state = 'PUBLISHED' AND published_at IS NOT NULL)");
            });
            entity.HasKey(row => row.DraftId);
            entity.HasIndex(row => new { row.GoalId, row.VersionNumber }).IsUnique();
            entity.HasIndex(row => row.ProposedGoalVersionId).IsUnique();
            entity.Property(row => row.DraftId).HasColumnName("draft_id");
            entity.Property(row => row.GoalId).HasColumnName("goal_id");
            entity.Property(row => row.ProposedGoalVersionId).HasColumnName("proposed_goal_version_id");
            entity.Property(row => row.VersionNumber).HasColumnName("version_number");
            entity.Property(row => row.Revision).HasColumnName("revision");
            entity.Property(row => row.State).HasColumnName("state").HasMaxLength(16);
            entity.Property(row => row.DefinitionJson).HasColumnName("definition_json").HasColumnType("jsonb");
            entity.Property(row => row.CreatorId).HasColumnName("creator_id");
            entity.Property(row => row.LastEditorId).HasColumnName("last_editor_id");
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.UpdatedAt).HasColumnName("updated_at");
            entity.Property(row => row.PublishedAt).HasColumnName("published_at");
        });

        modelBuilder.Entity<GoalVersionRow>(entity =>
        {
            entity.ToTable("goal_versions", table =>
            {
                table.HasCheckConstraint("ck_goal_versions_version", "version_number > 0");
                table.HasCheckConstraint("ck_goal_versions_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_goal_versions_mode",
                    "mode IN ('REPLAY', 'RESEARCH', 'PAPER_REVIEW', 'PAPER_AUTO', 'LIVE_REVIEW', 'LIVE_AUTO')");
            });
            entity.HasKey(row => row.GoalVersionId);
            entity.HasIndex(row => new { row.GoalId, row.VersionNumber }).IsUnique();
            entity.Property(row => row.GoalVersionId).HasColumnName("goal_version_id");
            entity.Property(row => row.GoalId).HasColumnName("goal_id");
            entity.Property(row => row.VersionNumber).HasColumnName("version_number");
            entity.Property(row => row.Mode).HasColumnName("mode").HasMaxLength(16);
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
            entity.Property(row => row.DefinitionJson).HasColumnName("definition_json").HasColumnType("jsonb");
            entity.Property(row => row.CreatorId).HasColumnName("creator_id");
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<GoalActivationRow>(entity =>
        {
            entity.ToTable("goal_activations", table =>
            {
                table.HasCheckConstraint("ck_goal_activations_revision", "revision > 0");
                table.HasCheckConstraint(
                    "ck_goal_activations_state",
                    "state IN ('ACTIVE', 'PAUSED', 'REVOKED', 'EXPIRED')");
                table.HasCheckConstraint(
                    "ck_goal_activations_expiry",
                    "expiry_time IS NULL OR expiry_time > activation_time");
                table.HasCheckConstraint(
                    "ck_goal_activations_revocation",
                    "(state = 'REVOKED' AND revocation_time IS NOT NULL AND revocation_reason IS NOT NULL) OR (state <> 'REVOKED' AND revocation_time IS NULL AND revocation_reason IS NULL)");
            });
            entity.HasKey(row => row.ActivationId);
            entity.HasIndex(row => row.GoalId)
                .IsUnique()
                .HasFilter("state IN ('ACTIVE', 'PAUSED')");
            entity.HasOne<GoalVersionRow>()
                .WithMany()
                .HasForeignKey(row => row.GoalVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.ActivationId).HasColumnName("activation_id");
            entity.Property(row => row.GoalId).HasColumnName("goal_id");
            entity.Property(row => row.GoalVersionId).HasColumnName("goal_version_id");
            entity.Property(row => row.OperatorId).HasColumnName("operator_id");
            entity.Property(row => row.State).HasColumnName("state").HasMaxLength(16);
            entity.Property(row => row.ActivationEvidence).HasColumnName("activation_evidence").HasColumnType("text");
            entity.Property(row => row.AuthoritySummary).HasColumnName("authority_summary").HasColumnType("text");
            entity.Property(row => row.ActivationTime).HasColumnName("activation_time");
            entity.Property(row => row.ExpiryTime).HasColumnName("expiry_time");
            entity.Property(row => row.RevocationTime).HasColumnName("revocation_time");
            entity.Property(row => row.RevocationReason).HasColumnName("revocation_reason").HasMaxLength(256);
            entity.Property(row => row.Revision).HasColumnName("revision");
        });

        modelBuilder.Entity<AccountAllocationLimitRow>(entity =>
        {
            entity.ToTable("account_allocation_limits", table =>
            {
                table.HasCheckConstraint("ck_account_allocation_limits_environment", "environment = 'PAPER'");
                table.HasCheckConstraint("ck_account_allocation_limits_amount", "maximum_amount > 0");
                table.HasCheckConstraint("ck_account_allocation_limits_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("ck_account_allocation_limits_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_account_allocation_limits_binding",
                    "reference->>'digest' = reference_digest AND length(btrim(endpoint_identity)) > 0 AND length(btrim(account_identifier)) > 0");
            });
            entity.HasKey(row => row.ReferenceDigest);
            entity.Property(row => row.ReferenceDigest).HasColumnName("reference_digest").HasMaxLength(256);
            entity.Property(row => row.Reference).HasColumnName("reference").HasColumnType("jsonb");
            entity.Property(row => row.BrokerConnectionVersion)
                .HasColumnName("broker_connection_version")
                .HasColumnType("jsonb");
            entity.Property(row => row.Environment).HasColumnName("environment").HasMaxLength(16);
            entity.Property(row => row.EndpointIdentity).HasColumnName("endpoint_identity").HasMaxLength(256);
            entity.Property(row => row.AccountIdentifier).HasColumnName("account_identifier").HasMaxLength(256);
            entity.Property(row => row.MaximumAmount).HasColumnName("maximum_amount").HasPrecision(38, 12);
            entity.Property(row => row.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(row => row.CreatorId).HasColumnName("creator_id");
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<AccountAllocationEntryRow>(entity =>
        {
            entity.ToTable("account_allocation_entries", table =>
            {
                table.HasCheckConstraint("ck_account_allocation_entries_environment", "environment = 'PAPER'");
                table.HasCheckConstraint("ck_account_allocation_entries_amount", "allocation_amount > 0");
                table.HasCheckConstraint("ck_account_allocation_entries_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint(
                    "ck_account_allocation_entries_state",
                    "state IN ('ACTIVE', 'RELEASED')");
                table.HasCheckConstraint(
                    "ck_account_allocation_entries_end",
                    "(state = 'ACTIVE' AND effective_end IS NULL) OR (state = 'RELEASED' AND effective_end IS NOT NULL AND effective_end >= effective_start)");
                table.HasCheckConstraint("ck_account_allocation_entries_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_account_allocation_entries_observation",
                    "observed_environment = 'PAPER' AND observed_environment = environment AND observed_endpoint_identity = endpoint_identity AND observed_account_identifier = account_identifier");
                table.HasCheckConstraint(
                    "ck_account_allocation_entries_binding",
                    "account_limit_version->>'digest' = account_limit_digest AND broker_connection_version->>'digest' = broker_connection_digest AND length(btrim(exposure_method_identifier)) > 0");
            });
            entity.HasKey(row => row.AllocationEntryId);
            entity.HasIndex(row => row.ActivationId).IsUnique();
            entity.HasIndex(row => new
            {
                row.BrokerConnectionDigest,
                row.AccountIdentifier,
                row.State
            });
            entity.HasOne<GoalVersionRow>()
                .WithMany()
                .HasForeignKey(row => row.GoalVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GoalActivationRow>()
                .WithMany()
                .HasForeignKey(row => row.ActivationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AccountAllocationLimitRow>()
                .WithMany()
                .HasForeignKey(row => row.AccountLimitDigest)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.AllocationEntryId).HasColumnName("allocation_entry_id");
            entity.Property(row => row.ActivationId).HasColumnName("activation_id");
            entity.Property(row => row.GoalVersionId).HasColumnName("goal_version_id");
            entity.Property(row => row.AccountLimitDigest).HasColumnName("account_limit_digest").HasMaxLength(256);
            entity.Property(row => row.AccountLimitVersion).HasColumnName("account_limit_version").HasColumnType("jsonb");
            entity.Property(row => row.BrokerConnectionDigest).HasColumnName("broker_connection_digest").HasMaxLength(256);
            entity.Property(row => row.BrokerConnectionVersion)
                .HasColumnName("broker_connection_version")
                .HasColumnType("jsonb");
            entity.Property(row => row.Environment).HasColumnName("environment").HasMaxLength(16);
            entity.Property(row => row.EndpointIdentity).HasColumnName("endpoint_identity").HasMaxLength(256);
            entity.Property(row => row.AccountIdentifier).HasColumnName("account_identifier").HasMaxLength(256);
            entity.Property(row => row.AllocationAmount).HasColumnName("allocation_amount").HasPrecision(38, 12);
            entity.Property(row => row.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(row => row.ExposureMethodIdentifier)
                .HasColumnName("exposure_method_identifier")
                .HasMaxLength(128);
            entity.Property(row => row.EffectiveStart).HasColumnName("effective_start");
            entity.Property(row => row.EffectiveEnd).HasColumnName("effective_end");
            entity.Property(row => row.State).HasColumnName("state").HasMaxLength(16);
            entity.Property(row => row.ObservedEnvironment).HasColumnName("observed_environment").HasMaxLength(16);
            entity.Property(row => row.ObservedEndpointIdentity)
                .HasColumnName("observed_endpoint_identity")
                .HasMaxLength(256);
            entity.Property(row => row.ObservedAccountIdentifier)
                .HasColumnName("observed_account_identifier")
                .HasMaxLength(256);
            entity.Property(row => row.ObservedCapabilities)
                .HasColumnName("observed_capabilities")
                .HasColumnType("jsonb");
            entity.Property(row => row.ObservedAt).HasColumnName("observed_at");
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<SessionRow>(entity =>
        {
            entity.ToTable("sessions", table =>
            {
                table.HasCheckConstraint("ck_sessions_revision", "revision > 0");
                table.HasCheckConstraint(
                    "ck_sessions_mode",
                    "mode IN ('REPLAY', 'RESEARCH', 'PAPER_REVIEW', 'PAPER_AUTO', 'LIVE_REVIEW', 'LIVE_AUTO')");
                table.HasCheckConstraint(
                    "ck_sessions_state",
                    "state IN ('CREATED', 'PREFLIGHT', 'READY', 'RUNNING', 'STOPPING', 'RECONCILING', 'COMPLETED', 'FAILED', 'LOCKED')");
                table.HasCheckConstraint(
                    "ck_sessions_terminal",
                    "(state IN ('COMPLETED', 'FAILED') AND end_time IS NOT NULL) OR (state NOT IN ('COMPLETED', 'FAILED') AND end_time IS NULL)");
                table.HasCheckConstraint(
                    "ck_sessions_failure",
                    "(state = 'FAILED' AND failure_reason IS NOT NULL) OR (state <> 'FAILED' AND failure_reason IS NULL)");
                table.HasCheckConstraint(
                    "ck_sessions_replay_input",
                    "(mode = 'REPLAY' AND input_data_snapshot_id IS NOT NULL AND replay_seed IS NOT NULL) OR (mode <> 'REPLAY' AND input_data_snapshot_id IS NOT NULL AND replay_seed IS NULL)");
                table.HasCheckConstraint(
                    "ck_sessions_broker_observation",
                    "(mode IN ('REPLAY', 'RESEARCH') AND observed_environment IS NULL AND observed_endpoint_identity IS NULL AND observed_account_identifier IS NULL) OR (mode IN ('PAPER_REVIEW', 'PAPER_AUTO', 'LIVE_REVIEW', 'LIVE_AUTO') AND ((observed_environment IS NULL AND observed_endpoint_identity IS NULL AND observed_account_identifier IS NULL) OR (observed_environment IN ('PAPER', 'LIVE') AND observed_endpoint_identity IS NOT NULL AND observed_account_identifier IS NOT NULL)) AND (state NOT IN ('READY', 'RUNNING', 'STOPPING', 'RECONCILING', 'COMPLETED') OR observed_environment IS NOT NULL))");
            });
            entity.HasKey(row => row.SessionId);
            entity.HasOne<GoalVersionRow>()
                .WithMany()
                .HasForeignKey(row => row.GoalVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GoalActivationRow>()
                .WithMany()
                .HasForeignKey(row => row.ActivationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DataSnapshotRow>()
                .WithMany()
                .HasForeignKey(row => row.InputDataSnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.SessionId).HasColumnName("session_id");
            entity.Property(row => row.GoalVersionId).HasColumnName("goal_version_id");
            entity.Property(row => row.ActivationId).HasColumnName("activation_id");
            entity.Property(row => row.Mode).HasColumnName("mode").HasMaxLength(16);
            entity.Property(row => row.StartTime).HasColumnName("start_time");
            entity.Property(row => row.State).HasColumnName("state").HasMaxLength(16);
            entity.Property(row => row.EndTime).HasColumnName("end_time");
            entity.Property(row => row.FailureReason).HasColumnName("failure_reason").HasMaxLength(256);
            entity.Property(row => row.InputDataSnapshotId).HasColumnName("input_data_snapshot_id");
            entity.Property(row => row.ReplaySeed).HasColumnName("replay_seed");
            entity.Property(row => row.ObservedEnvironment)
                .HasColumnName("observed_environment")
                .HasMaxLength(16);
            entity.Property(row => row.ObservedEndpointIdentity)
                .HasColumnName("observed_endpoint_identity")
                .HasMaxLength(256);
            entity.Property(row => row.ObservedAccountIdentifier)
                .HasColumnName("observed_account_identifier")
                .HasMaxLength(256);
            entity.Property(row => row.Revision).HasColumnName("revision");
        });

        modelBuilder.Entity<ExecutableSessionPreflightRow>(entity =>
        {
            entity.ToTable("executable_session_preflights", table =>
            {
                table.HasCheckConstraint(
                    "ck_executable_session_preflights_result",
                    "result IN ('PASSED', 'FAILED')");
                table.HasCheckConstraint(
                    "ck_executable_session_preflights_environment",
                    "observed_environment IS NULL OR observed_environment IN ('PAPER', 'LIVE')");
                table.HasCheckConstraint(
                    "ck_executable_session_preflights_money",
                    "(position_exposure IS NULL OR position_exposure >= 0) AND (open_order_exposure IS NULL OR open_order_exposure >= 0) AND (buying_power IS NULL OR buying_power >= 0)");
                table.HasCheckConstraint(
                    "ck_executable_session_preflights_currency",
                    "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint(
                    "ck_executable_session_preflights_checks",
                    "jsonb_typeof(checks) = 'array' AND jsonb_array_length(checks) = 17");
                table.HasCheckConstraint(
                    "ck_executable_session_preflights_capabilities",
                    "jsonb_typeof(observed_capabilities) = 'array'");
                table.HasCheckConstraint(
                    "ck_executable_session_preflights_digest",
                    "content_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_executable_session_preflights_reconciliation",
                    "result <> 'PASSED' OR reconciliation_id IS NOT NULL");
            });
            entity.HasKey(row => row.PreflightId);
            entity.HasIndex(row => row.SessionId).IsUnique();
            entity.HasOne<ReconciliationRow>()
                .WithMany()
                .HasForeignKey(row => row.ReconciliationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SessionRow>()
                .WithMany()
                .HasForeignKey(row => row.SessionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GoalVersionRow>()
                .WithMany()
                .HasForeignKey(row => row.GoalVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GoalActivationRow>()
                .WithMany()
                .HasForeignKey(row => row.ActivationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.PreflightId).HasColumnName("preflight_id");
            entity.Property(row => row.SessionId).HasColumnName("session_id");
            entity.Property(row => row.GoalVersionId).HasColumnName("goal_version_id");
            entity.Property(row => row.ActivationId).HasColumnName("activation_id");
            entity.Property(row => row.BrokerConnectionVersion)
                .HasColumnName("broker_connection_version")
                .HasColumnType("jsonb");
            entity.Property(row => row.ReconciliationId).HasColumnName("reconciliation_id");
            entity.Property(row => row.ObservedEnvironment)
                .HasColumnName("observed_environment")
                .HasMaxLength(16);
            entity.Property(row => row.ObservedEndpointIdentity)
                .HasColumnName("observed_endpoint_identity")
                .HasMaxLength(256);
            entity.Property(row => row.ObservedAccountIdentifier)
                .HasColumnName("observed_account_identifier")
                .HasMaxLength(256);
            entity.Property(row => row.ObservedCapabilities)
                .HasColumnName("observed_capabilities")
                .HasColumnType("jsonb");
            entity.Property(row => row.BrokerObservedAt).HasColumnName("broker_observed_at");
            entity.Property(row => row.BrokerTime).HasColumnName("broker_time");
            entity.Property(row => row.CapitalObservedAt).HasColumnName("capital_observed_at");
            entity.Property(row => row.ReconciliationObservedAt)
                .HasColumnName("reconciliation_observed_at");
            entity.Property(row => row.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(row => row.PositionExposure)
                .HasColumnName("position_exposure")
                .HasPrecision(38, 12);
            entity.Property(row => row.OpenOrderExposure)
                .HasColumnName("open_order_exposure")
                .HasPrecision(38, 12);
            entity.Property(row => row.BuyingPower)
                .HasColumnName("buying_power")
                .HasPrecision(38, 12);
            entity.Property(row => row.Checks).HasColumnName("checks").HasColumnType("jsonb");
            entity.Property(row => row.Result).HasColumnName("result").HasMaxLength(16);
            entity.Property(row => row.CompletedAt).HasColumnName("completed_at");
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<AuditChainHeadRow>(entity =>
        {
            entity.ToTable("audit_chain_heads", table =>
            {
                table.HasCheckConstraint("ck_audit_chain_heads_sequence", "last_sequence >= 0");
                table.HasCheckConstraint("ck_audit_chain_heads_hash", "last_hash ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.ChainId);
            entity.Property(row => row.ChainId).HasColumnName("chain_id").HasMaxLength(32);
            entity.Property(row => row.LastSequence).HasColumnName("last_sequence");
            entity.Property(row => row.LastHash).HasColumnName("last_hash").HasMaxLength(64);
        });

        modelBuilder.Entity<AuditEventRow>(entity =>
        {
            entity.ToTable("audit_events", table =>
            {
                table.HasCheckConstraint("ck_audit_events_sequence", "sequence > 0");
                table.HasCheckConstraint("ck_audit_events_previous_hash", "previous_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_audit_events_event_hash", "event_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_audit_events_type",
                    "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'ACCOUNT_ALLOCATION_LIMIT_CONFIGURED', 'BROKER_CONNECTION_VERIFIED', 'ACCOUNT_ALLOCATION_RESERVED', 'ACCOUNT_ALLOCATION_RELEASED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'SESSION_PREFLIGHT_COMPLETED', 'DECISION_RECORDED', 'RISK_EVALUATED', 'EXECUTION_INTENT_CREATED', 'EXECUTION_INTENT_STATE_CHANGED', 'REVIEW_RECORDED', 'KILL_SWITCH_CONFIGURED', 'KILL_SWITCH_STATE_CHANGED', 'RECONCILIATION_STARTED', 'RECONCILIATION_COMPLETED', 'EXPOSURE_RESERVED', 'EXPOSURE_RELEASED', 'EXPOSURE_ADJUSTED', 'PRE_SUBMIT_EVALUATED', 'ORDER_PREPARED', 'ORDER_STATE_CHANGED', 'CANCELLATION_PREPARED', 'CANCELLATION_STATE_CHANGED', 'BROKER_ORDER_EVENT_RECORDED', 'BROKER_EXECUTION_EVENT_RECORDED', 'FILL_RECORDED', 'POSITION_SNAPSHOT_RECORDED', 'POST_FILL_EVALUATED', 'POST_FILL_SAFETY_RESPONSE_CREATED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");
            });
            entity.HasKey(row => row.EventId);
            entity.HasIndex(row => row.Sequence).IsUnique();
            entity.HasOne<GoalVersionRow>()
                .WithMany()
                .HasForeignKey(row => row.GoalVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SessionRow>()
                .WithMany()
                .HasForeignKey(row => row.SessionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.EventId).HasColumnName("event_id");
            entity.Property(row => row.Sequence).HasColumnName("sequence").ValueGeneratedNever();
            entity.Property(row => row.EventType).HasColumnName("event_type").HasMaxLength(64);
            entity.Property(row => row.EventTime).HasColumnName("event_time");
            entity.Property(row => row.ActorId).HasColumnName("actor_id");
            entity.Property(row => row.GoalVersionId).HasColumnName("goal_version_id");
            entity.Property(row => row.SessionId).HasColumnName("session_id");
            entity.Property(row => row.RelatedRecordIdentifiersJson)
                .HasColumnName("related_record_identifiers")
                .HasColumnType("jsonb");
            entity.Property(row => row.PreviousState).HasColumnName("previous_state").HasMaxLength(64);
            entity.Property(row => row.NewState).HasColumnName("new_state").HasMaxLength(64);
            entity.Property(row => row.Reason).HasColumnName("reason").HasMaxLength(256);
            entity.Property(row => row.SourceReference).HasColumnName("source_reference").HasMaxLength(128);
            entity.Property(row => row.CanonicalPayload).HasColumnName("canonical_payload").HasColumnType("text");
            entity.Property(row => row.PreviousHash).HasColumnName("previous_hash").HasMaxLength(64);
            entity.Property(row => row.EventHash).HasColumnName("event_hash").HasMaxLength(64);
        });

        modelBuilder.Entity<DataSnapshotRow>(entity =>
        {
            entity.ToTable("data_snapshots", table =>
            {
                table.HasCheckConstraint("ck_data_snapshots_range", "range_end >= range_start");
                table.HasCheckConstraint("ck_data_snapshots_capture", "captured_at >= range_end");
                table.HasCheckConstraint("ck_data_snapshots_payload", "octet_length(payload) > 0");
                table.HasCheckConstraint("ck_data_snapshots_payload_digest", "payload_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_data_snapshots_content_digest", "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.DataSnapshotId);
            entity.Property(row => row.DataSnapshotId).HasColumnName("data_snapshot_id");
            entity.Property(row => row.DataSetVersion).HasColumnName("data_set_version").HasColumnType("jsonb");
            entity.Property(row => row.DataPolicyVersion).HasColumnName("data_policy_version").HasColumnType("jsonb");
            entity.Property(row => row.SchemaVersion).HasColumnName("schema_version").HasMaxLength(128);
            entity.Property(row => row.RangeStart).HasColumnName("range_start");
            entity.Property(row => row.RangeEnd).HasColumnName("range_end");
            entity.Property(row => row.CapturedAt).HasColumnName("captured_at");
            entity.Property(row => row.SourceObservations).HasColumnName("source_observations").HasColumnType("jsonb");
            entity.Property(row => row.Payload).HasColumnName("payload").HasColumnType("bytea");
            entity.Property(row => row.PayloadDigest).HasColumnName("payload_digest").HasMaxLength(64);
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<ReplayResultRow>(entity =>
        {
            entity.ToTable("replay_results", table =>
            {
                table.HasCheckConstraint("ck_replay_results_range", "range_end >= range_start");
                table.HasCheckConstraint("ck_replay_results_time", "completed_at >= started_at");
                table.HasCheckConstraint("ck_replay_results_snapshot_digest", "data_snapshot_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_replay_results_output_digest", "output_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.ReplayResultId);
            entity.HasIndex(row => row.SessionId).IsUnique();
            entity.HasOne<GoalVersionRow>()
                .WithMany()
                .HasForeignKey(row => row.GoalVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DataSnapshotRow>()
                .WithMany()
                .HasForeignKey(row => row.DataSnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GoalActivationRow>()
                .WithMany()
                .HasForeignKey(row => row.ActivationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SessionRow>()
                .WithMany()
                .HasForeignKey(row => row.SessionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.ReplayResultId).HasColumnName("replay_result_id");
            entity.Property(row => row.SessionId).HasColumnName("session_id");
            entity.Property(row => row.ActivationId).HasColumnName("activation_id");
            entity.Property(row => row.GoalVersionId).HasColumnName("goal_version_id");
            entity.Property(row => row.DataSnapshotId).HasColumnName("data_snapshot_id");
            entity.Property(row => row.DataSnapshotDigest).HasColumnName("data_snapshot_digest").HasMaxLength(64);
            entity.Property(row => row.RangeStart).HasColumnName("range_start");
            entity.Property(row => row.RangeEnd).HasColumnName("range_end");
            entity.Property(row => row.StrategyVersion).HasColumnName("strategy_version").HasColumnType("jsonb");
            entity.Property(row => row.DataPolicyVersion).HasColumnName("data_policy_version").HasColumnType("jsonb");
            entity.Property(row => row.RiskPolicyVersion).HasColumnName("risk_policy_version").HasColumnType("jsonb");
            entity.Property(row => row.Seed).HasColumnName("seed");
            entity.Property(row => row.OutputMetrics).HasColumnName("output_metrics").HasColumnType("jsonb");
            entity.Property(row => row.Evidence).HasColumnName("evidence").HasColumnType("text");
            entity.Property(row => row.StartedAt).HasColumnName("started_at");
            entity.Property(row => row.CompletedAt).HasColumnName("completed_at");
            entity.Property(row => row.OutputDigest).HasColumnName("output_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<DecisionRow>(entity =>
        {
            entity.ToTable("decisions", table =>
            {
                table.HasCheckConstraint(
                    "ck_decisions_action",
                    "proposed_action IN ('UNKNOWN', 'NO_ACTION', 'BUY', 'SELL')");
                table.HasCheckConstraint(
                    "ck_decisions_state",
                    "state IN ('PROPOSED', 'GUARD_REJECTED', 'INTENT_CREATED', 'NO_ACTION', 'FAILED')");
                table.HasCheckConstraint(
                    "ck_decisions_confidence",
                    "confidence IS NULL OR (confidence >= 0 AND confidence <= 1)");
                table.HasCheckConstraint("ck_decisions_digest", "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.DecisionId);
            entity.HasOne<SessionRow>()
                .WithMany()
                .HasForeignKey(row => row.SessionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GoalVersionRow>()
                .WithMany()
                .HasForeignKey(row => row.GoalVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DataSnapshotRow>()
                .WithMany()
                .HasForeignKey(row => row.InputSnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.DecisionId).HasColumnName("decision_id");
            entity.Property(row => row.SessionId).HasColumnName("session_id");
            entity.Property(row => row.GoalVersionId).HasColumnName("goal_version_id");
            entity.Property(row => row.StrategyVersion).HasColumnName("strategy_version").HasColumnType("jsonb");
            entity.Property(row => row.InputSnapshotId).HasColumnName("input_snapshot_id");
            entity.Property(row => row.DecisionTime).HasColumnName("decision_time");
            entity.Property(row => row.ProposedAction).HasColumnName("proposed_action").HasMaxLength(16);
            entity.Property(row => row.InstrumentIdentifier).HasColumnName("instrument_identifier").HasMaxLength(128);
            entity.Property(row => row.Quantity).HasColumnName("quantity").HasPrecision(38, 12);
            entity.Property(row => row.LimitPrice).HasColumnName("limit_price").HasPrecision(38, 12);
            entity.Property(row => row.Explanation).HasColumnName("explanation").HasColumnType("text");
            entity.Property(row => row.Confidence).HasColumnName("confidence").HasPrecision(18, 12);
            entity.Property(row => row.GuardResults).HasColumnName("guard_results").HasColumnType("jsonb");
            entity.Property(row => row.State).HasColumnName("state").HasMaxLength(24);
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<RiskEvaluationRow>(entity =>
        {
            entity.ToTable("risk_evaluations", table =>
            {
                table.HasCheckConstraint(
                    "ck_risk_evaluations_money",
                    "position_exposure >= 0 AND open_order_exposure >= 0 AND requested_exposure > 0 AND buying_power >= 0");
                table.HasCheckConstraint("ck_risk_evaluations_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint(
                    "ck_risk_evaluations_checks",
                    "jsonb_typeof(checks) = 'array' AND jsonb_array_length(checks) = 13");
                table.HasCheckConstraint("ck_risk_evaluations_result", "result IN ('ACCEPTED', 'REJECTED')");
                table.HasCheckConstraint("ck_risk_evaluations_digest", "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.RiskEvaluationId);
            entity.HasIndex(row => row.IntentId).IsUnique();
            entity.HasOne<DecisionRow>()
                .WithMany()
                .HasForeignKey(row => row.DecisionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GoalVersionRow>()
                .WithMany()
                .HasForeignKey(row => row.GoalVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SessionRow>()
                .WithMany()
                .HasForeignKey(row => row.SessionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AccountAllocationEntryRow>()
                .WithMany()
                .HasForeignKey(row => row.AllocationEntryId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ReconciliationRow>()
                .WithMany()
                .HasForeignKey(row => row.ReconciliationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.RiskEvaluationId).HasColumnName("risk_evaluation_id");
            entity.Property(row => row.IntentId).HasColumnName("intent_id");
            entity.Property(row => row.DecisionId).HasColumnName("decision_id");
            entity.Property(row => row.GoalVersionId).HasColumnName("goal_version_id");
            entity.Property(row => row.SessionId).HasColumnName("session_id");
            entity.Property(row => row.RiskPolicyVersion).HasColumnName("risk_policy_version").HasColumnType("jsonb");
            entity.Property(row => row.AllocationEntryId).HasColumnName("allocation_entry_id");
            entity.Property(row => row.ReconciliationId).HasColumnName("reconciliation_id");
            entity.Property(row => row.PositionExposure).HasColumnName("position_exposure").HasPrecision(38, 12);
            entity.Property(row => row.OpenOrderExposure).HasColumnName("open_order_exposure").HasPrecision(38, 12);
            entity.Property(row => row.RequestedExposure).HasColumnName("requested_exposure").HasPrecision(38, 12);
            entity.Property(row => row.BuyingPower).HasColumnName("buying_power").HasPrecision(38, 12);
            entity.Property(row => row.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(row => row.Checks).HasColumnName("checks").HasColumnType("jsonb");
            entity.Property(row => row.Result).HasColumnName("result").HasMaxLength(16);
            entity.Property(row => row.EvaluatedAt).HasColumnName("evaluated_at");
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<ExecutionIntentRow>(entity =>
        {
            entity.ToTable("execution_intents", table =>
            {
                table.HasCheckConstraint(
                    "ck_execution_intents_mode",
                    "mode IN ('PAPER_REVIEW', 'PAPER_AUTO')");
                table.HasCheckConstraint("ck_execution_intents_environment", "environment = 'PAPER'");
                table.HasCheckConstraint("ck_execution_intents_side", "side IN ('BUY', 'SELL')");
                table.HasCheckConstraint("ck_execution_intents_order", "order_type = 'LIMIT' AND time_in_force = 'DAY'");
                table.HasCheckConstraint("ck_execution_intents_money", "quantity > 0 AND limit_price > 0");
                table.HasCheckConstraint("ck_execution_intents_expiry", "expires_at > created_at");
                table.HasCheckConstraint(
                    "ck_execution_intents_state",
                    "state IN ('CREATED', 'RISK_ACCEPTED', 'RISK_REJECTED', 'REVIEW_PENDING', 'APPROVED', 'REJECTED', 'EXPIRED', 'SUBMISSION_PENDING', 'SUBMITTED', 'SUBMISSION_UNKNOWN', 'CANCELED', 'FAILED')");
                table.HasCheckConstraint("ck_execution_intents_digest", "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.IntentId);
            entity.HasIndex(row => row.RiskEvaluationId).IsUnique();
            entity.HasIndex(row => row.IdempotencyKey).IsUnique();
            entity.HasOne<DecisionRow>()
                .WithMany()
                .HasForeignKey(row => row.DecisionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GoalVersionRow>()
                .WithMany()
                .HasForeignKey(row => row.GoalVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SessionRow>()
                .WithMany()
                .HasForeignKey(row => row.SessionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<RiskEvaluationRow>()
                .WithMany()
                .HasForeignKey(row => row.RiskEvaluationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.IntentId).HasColumnName("intent_id");
            entity.Property(row => row.DecisionId).HasColumnName("decision_id");
            entity.Property(row => row.GoalVersionId).HasColumnName("goal_version_id");
            entity.Property(row => row.SessionId).HasColumnName("session_id");
            entity.Property(row => row.Mode).HasColumnName("mode").HasMaxLength(16);
            entity.Property(row => row.BrokerConnectionVersion).HasColumnName("broker_connection_version").HasColumnType("jsonb");
            entity.Property(row => row.Environment).HasColumnName("environment").HasMaxLength(8);
            entity.Property(row => row.EndpointIdentity).HasColumnName("endpoint_identity").HasMaxLength(256);
            entity.Property(row => row.AccountIdentifier).HasColumnName("account_identifier").HasMaxLength(256);
            entity.Property(row => row.InstrumentIdentifier).HasColumnName("instrument_identifier").HasMaxLength(128);
            entity.Property(row => row.Side).HasColumnName("side").HasMaxLength(8);
            entity.Property(row => row.OrderType).HasColumnName("order_type").HasMaxLength(16);
            entity.Property(row => row.Quantity).HasColumnName("quantity").HasPrecision(38, 12);
            entity.Property(row => row.LimitPrice).HasColumnName("limit_price").HasPrecision(38, 12);
            entity.Property(row => row.TimeInForce).HasColumnName("time_in_force").HasMaxLength(16);
            entity.Property(row => row.ExpiresAt).HasColumnName("expires_at");
            entity.Property(row => row.RiskEvaluationId).HasColumnName("risk_evaluation_id");
            entity.Property(row => row.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.State).HasColumnName("state").HasMaxLength(24);
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<ReviewApprovalRow>(entity =>
        {
            entity.ToTable("review_approvals", table =>
            {
                table.HasCheckConstraint("ck_review_approvals_result", "result IN ('APPROVED', 'REJECTED')");
                table.HasCheckConstraint("ck_review_approvals_expiry", "expires_at > action_time");
                table.HasCheckConstraint("ck_review_approvals_intent_digest", "intent_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_review_approvals_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_review_approvals_safety_evidence",
                    "(result = 'APPROVED' AND reconciliation_id IS NOT NULL AND reservation_id IS NOT NULL) OR (result = 'REJECTED' AND reconciliation_id IS NULL AND reservation_id IS NULL)");
            });
            entity.HasKey(row => row.ApprovalId);
            entity.HasIndex(row => row.IntentId).IsUnique();
            entity.HasOne<ExecutionIntentRow>()
                .WithMany()
                .HasForeignKey(row => row.IntentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ReconciliationRow>()
                .WithMany()
                .HasForeignKey(row => row.ReconciliationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ExposureReservationRow>()
                .WithMany()
                .HasForeignKey(row => row.ReservationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.ApprovalId).HasColumnName("approval_id");
            entity.Property(row => row.IntentId).HasColumnName("intent_id");
            entity.Property(row => row.ReconciliationId).HasColumnName("reconciliation_id");
            entity.Property(row => row.ReservationId).HasColumnName("reservation_id");
            entity.Property(row => row.OperatorId).HasColumnName("operator_id");
            entity.Property(row => row.Result).HasColumnName("result").HasMaxLength(16);
            entity.Property(row => row.ActionTime).HasColumnName("action_time");
            entity.Property(row => row.IntentDigest).HasColumnName("intent_digest").HasMaxLength(64);
            entity.Property(row => row.ExpiresAt).HasColumnName("expires_at");
            entity.Property(row => row.OperatorNote).HasColumnName("operator_note").HasColumnType("text");
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<KillSwitchRow>(entity =>
        {
            entity.ToTable("kill_switches", table =>
            {
                table.HasCheckConstraint(
                    "ck_kill_switches_scope",
                    "scope_type IN ('SYSTEM', 'CONNECTION', 'ACCOUNT', 'GOAL')");
                table.HasCheckConstraint(
                    "ck_kill_switches_state",
                    "state IN ('CLEAR', 'TRIGGERED', 'RECOVERY_REQUIRED')");
                table.HasCheckConstraint("ck_kill_switches_revision", "revision >= 0");
                table.HasCheckConstraint(
                    "ck_kill_switches_lifecycle",
                    "(state = 'CLEAR' AND ((revision = 0 AND triggered_at IS NULL AND trigger_reason IS NULL AND recovery_reconciliation_id IS NULL AND recovery_required_at IS NULL AND cleared_at IS NULL) OR (revision > 0 AND triggered_at IS NOT NULL AND trigger_reason IS NOT NULL AND recovery_reconciliation_id IS NOT NULL AND recovery_required_at IS NOT NULL AND cleared_at IS NOT NULL))) OR (state = 'TRIGGERED' AND triggered_at IS NOT NULL AND trigger_reason IS NOT NULL AND recovery_reconciliation_id IS NULL AND recovery_required_at IS NULL AND cleared_at IS NULL) OR (state = 'RECOVERY_REQUIRED' AND triggered_at IS NOT NULL AND trigger_reason IS NOT NULL AND recovery_reconciliation_id IS NOT NULL AND recovery_required_at IS NOT NULL AND cleared_at IS NULL)");
            });
            entity.HasKey(row => row.KillSwitchId);
            entity.HasIndex(row => new { row.ScopeType, row.ScopeIdentifier }).IsUnique();
            entity.Property(row => row.KillSwitchId).HasColumnName("kill_switch_id");
            entity.Property(row => row.ScopeType).HasColumnName("scope_type").HasMaxLength(16);
            entity.Property(row => row.ScopeIdentifier).HasColumnName("scope_identifier").HasMaxLength(256);
            entity.Property(row => row.State).HasColumnName("state").HasMaxLength(24);
            entity.Property(row => row.Revision).HasColumnName("revision");
            entity.Property(row => row.ConfiguredAt).HasColumnName("configured_at");
            entity.Property(row => row.TriggeredAt).HasColumnName("triggered_at");
            entity.Property(row => row.TriggerReason).HasColumnName("trigger_reason").HasColumnType("text");
            entity.Property(row => row.RecoveryReconciliationId).HasColumnName("recovery_reconciliation_id");
            entity.Property(row => row.RecoveryRequiredAt).HasColumnName("recovery_required_at");
            entity.Property(row => row.ClearedAt).HasColumnName("cleared_at");
        });

        modelBuilder.Entity<ReconciliationRow>(entity =>
        {
            entity.ToTable("reconciliations", table =>
            {
                table.HasCheckConstraint("ck_reconciliations_environment", "environment = 'PAPER'");
                table.HasCheckConstraint(
                    "ck_reconciliations_result",
                    "result IN ('CURRENT', 'DIFFERENCE_LOCKED', 'FAILED')");
                table.HasCheckConstraint(
                    "ck_reconciliations_money",
                    "position_exposure >= 0 AND open_order_exposure >= 0 AND buying_power >= 0");
                table.HasCheckConstraint("ck_reconciliations_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("ck_reconciliations_time", "completed_at >= started_at AND observed_at <= completed_at");
                table.HasCheckConstraint("ck_reconciliations_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_reconciliations_evidence",
                    "compared_order_range ~ '^orders:sha256:[0-9a-f]{64}$' AND compared_fill_range ~ '^fills:sha256:[0-9a-f]{64}$' AND compared_position_snapshot ~ '^positions:sha256:[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_reconciliations_details",
                    "jsonb_typeof(differences) = 'array' AND jsonb_typeof(corrections) = 'array' AND ((result = 'CURRENT' AND jsonb_array_length(differences) = 0 AND operator_action IS NULL) OR (result = 'DIFFERENCE_LOCKED' AND jsonb_array_length(differences) > 0 AND operator_action IS NOT NULL) OR (result = 'FAILED' AND operator_action IS NOT NULL))");
            });
            entity.HasKey(row => row.ReconciliationId);
            entity.HasIndex(row => row.Sequence).IsUnique();
            entity.HasIndex(row => new { row.GoalVersionId, row.CompletedAt });
            entity.HasOne<GoalVersionRow>()
                .WithMany()
                .HasForeignKey(row => row.GoalVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.ReconciliationId).HasColumnName("reconciliation_id");
            entity.Property(row => row.Sequence).HasColumnName("sequence")
                .UseIdentityAlwaysColumn();
            entity.Property(row => row.GoalVersionId).HasColumnName("goal_version_id");
            entity.Property(row => row.BrokerConnectionVersion).HasColumnName("broker_connection_version").HasColumnType("jsonb");
            entity.Property(row => row.Environment).HasColumnName("environment").HasMaxLength(8);
            entity.Property(row => row.EndpointIdentity).HasColumnName("endpoint_identity").HasMaxLength(256);
            entity.Property(row => row.AccountIdentifier).HasColumnName("account_identifier").HasMaxLength(256);
            entity.Property(row => row.ReconciliationPolicyVersion).HasColumnName("reconciliation_policy_version").HasColumnType("jsonb");
            entity.Property(row => row.StartedAt).HasColumnName("started_at");
            entity.Property(row => row.CompletedAt).HasColumnName("completed_at");
            entity.Property(row => row.ObservedAt).HasColumnName("observed_at");
            entity.Property(row => row.PositionExposure).HasColumnName("position_exposure").HasPrecision(38, 12);
            entity.Property(row => row.OpenOrderExposure).HasColumnName("open_order_exposure").HasPrecision(38, 12);
            entity.Property(row => row.BuyingPower).HasColumnName("buying_power").HasPrecision(38, 12);
            entity.Property(row => row.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(row => row.ComparedOrderRange).HasColumnName("compared_order_range").HasColumnType("text");
            entity.Property(row => row.ComparedFillRange).HasColumnName("compared_fill_range").HasColumnType("text");
            entity.Property(row => row.ComparedPositionSnapshot).HasColumnName("compared_position_snapshot").HasColumnType("text");
            entity.Property(row => row.Differences).HasColumnName("differences").HasColumnType("jsonb");
            entity.Property(row => row.Corrections).HasColumnName("corrections").HasColumnType("jsonb");
            entity.Property(row => row.OperatorAction).HasColumnName("operator_action").HasColumnType("text");
            entity.Property(row => row.Result).HasColumnName("result").HasMaxLength(24);
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<ExposureReservationRow>(entity =>
        {
            entity.ToTable("exposure_reservations", table =>
            {
                table.HasCheckConstraint("ck_exposure_reservations_environment", "environment = 'PAPER'");
                table.HasCheckConstraint("ck_exposure_reservations_amount", "amount > 0 AND remaining_amount >= 0 AND remaining_amount <= amount");
                table.HasCheckConstraint("ck_exposure_reservations_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("ck_exposure_reservations_state", "state IN ('RESERVED', 'RELEASED')");
                table.HasCheckConstraint("ck_exposure_reservations_revision", "revision >= 0");
                table.HasCheckConstraint("ck_exposure_reservations_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_exposure_reservations_lifecycle",
                    "(state = 'RESERVED' AND remaining_amount > 0 AND released_at IS NULL AND release_reason IS NULL) OR (state = 'RELEASED' AND remaining_amount = 0 AND revision > 0 AND released_at IS NOT NULL AND release_reason IS NOT NULL)");
            });
            entity.HasKey(row => row.ReservationId);
            entity.HasIndex(row => row.IntentId).IsUnique();
            entity.HasIndex(row => new { row.AllocationEntryId, row.State });
            entity.HasOne<ExecutionIntentRow>()
                .WithMany()
                .HasForeignKey(row => row.IntentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GoalVersionRow>()
                .WithMany()
                .HasForeignKey(row => row.GoalVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AccountAllocationEntryRow>()
                .WithMany()
                .HasForeignKey(row => row.AllocationEntryId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.ReservationId).HasColumnName("reservation_id");
            entity.Property(row => row.IntentId).HasColumnName("intent_id");
            entity.Property(row => row.GoalVersionId).HasColumnName("goal_version_id");
            entity.Property(row => row.AllocationEntryId).HasColumnName("allocation_entry_id");
            entity.Property(row => row.BrokerConnectionVersion).HasColumnName("broker_connection_version").HasColumnType("jsonb");
            entity.Property(row => row.Environment).HasColumnName("environment").HasMaxLength(8);
            entity.Property(row => row.EndpointIdentity).HasColumnName("endpoint_identity").HasMaxLength(256);
            entity.Property(row => row.AccountIdentifier).HasColumnName("account_identifier").HasMaxLength(256);
            entity.Property(row => row.Amount).HasColumnName("amount").HasPrecision(38, 12);
            entity.Property(row => row.RemainingAmount).HasColumnName("remaining_amount").HasPrecision(38, 12);
            entity.Property(row => row.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.State).HasColumnName("state").HasMaxLength(16);
            entity.Property(row => row.ReleasedAt).HasColumnName("released_at");
            entity.Property(row => row.ReleaseReason).HasColumnName("release_reason").HasColumnType("text");
            entity.Property(row => row.Revision).HasColumnName("revision");
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<PreSubmitEvaluationRow>(entity =>
        {
            entity.ToTable("pre_submit_evaluations", table =>
            {
                table.HasCheckConstraint("ck_pre_submit_evaluations_money",
                    "position_exposure >= 0 AND open_order_exposure >= 0 AND buying_power >= 0");
                table.HasCheckConstraint("ck_pre_submit_evaluations_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("ck_pre_submit_evaluations_result", "result IN ('PASSED', 'FAILED')");
                table.HasCheckConstraint("ck_pre_submit_evaluations_digest", "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.EvaluationId);
            entity.HasIndex(row => row.IntentId).IsUnique();
            entity.HasOne<ExecutionIntentRow>().WithMany().HasForeignKey(row => row.IntentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ReviewApprovalRow>().WithMany().HasForeignKey(row => row.ApprovalId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ExposureReservationRow>().WithMany().HasForeignKey(row => row.ReservationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ReconciliationRow>().WithMany().HasForeignKey(row => row.ReconciliationId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.EvaluationId).HasColumnName("evaluation_id");
            entity.Property(row => row.IntentId).HasColumnName("intent_id");
            entity.Property(row => row.IntentDigest).HasColumnName("intent_digest").HasMaxLength(64);
            entity.Property(row => row.ApprovalId).HasColumnName("approval_id");
            entity.Property(row => row.ReservationId).HasColumnName("reservation_id");
            entity.Property(row => row.ReconciliationId).HasColumnName("reconciliation_id");
            entity.Property(row => row.PositionExposure).HasColumnName("position_exposure").HasPrecision(38, 12);
            entity.Property(row => row.OpenOrderExposure).HasColumnName("open_order_exposure").HasPrecision(38, 12);
            entity.Property(row => row.BuyingPower).HasColumnName("buying_power").HasPrecision(38, 12);
            entity.Property(row => row.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(row => row.Checks).HasColumnName("checks").HasColumnType("jsonb");
            entity.Property(row => row.Result).HasColumnName("result").HasMaxLength(16);
            entity.Property(row => row.EvaluatedAt).HasColumnName("evaluated_at");
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<OrderRow>(entity =>
        {
            entity.ToTable("orders", table =>
            {
                table.HasCheckConstraint("ck_orders_environment", "environment = 'PAPER'");
                table.HasCheckConstraint("ck_orders_request", "order_type = 'LIMIT' AND time_in_force = 'DAY' AND quantity > 0 AND quantity = trunc(quantity) AND limit_price > 0");
                table.HasCheckConstraint("ck_orders_filled_quantity", "filled_quantity >= 0 AND filled_quantity <= quantity");
                table.HasCheckConstraint("ck_orders_state", "state IN ('CREATED', 'SUBMITTING', 'ACKNOWLEDGED', 'PARTIALLY_FILLED', 'FILLED', 'CANCEL_PENDING', 'CANCELED', 'REJECTED', 'EXPIRED', 'SUBMISSION_UNKNOWN', 'RECONCILIATION_REQUIRED')");
                table.HasCheckConstraint("ck_orders_terminal", "(state IN ('FILLED', 'CANCELED', 'REJECTED', 'EXPIRED', 'RECONCILIATION_REQUIRED') AND terminal_at IS NOT NULL) OR (state NOT IN ('FILLED', 'CANCELED', 'REJECTED', 'EXPIRED', 'RECONCILIATION_REQUIRED') AND terminal_at IS NULL)");
            });
            entity.HasKey(row => row.OrderId);
            entity.HasIndex(row => row.IntentId).IsUnique();
            entity.HasIndex(row => row.IdempotencyKey).IsUnique();
            entity.HasIndex(row => row.BrokerClientOrderId).IsUnique();
            entity.HasOne<ExecutionIntentRow>().WithMany().HasForeignKey(row => row.IntentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PreSubmitEvaluationRow>().WithMany().HasForeignKey(row => row.EvaluationId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.OrderId).HasColumnName("order_id");
            entity.Property(row => row.IntentId).HasColumnName("intent_id");
            entity.Property(row => row.IntentDigest).HasColumnName("intent_digest").HasMaxLength(64);
            entity.Property(row => row.EvaluationId).HasColumnName("evaluation_id");
            entity.Property(row => row.BrokerConnectionVersion).HasColumnName("broker_connection_version").HasColumnType("jsonb");
            entity.Property(row => row.Environment).HasColumnName("environment").HasMaxLength(8);
            entity.Property(row => row.EndpointIdentity).HasColumnName("endpoint_identity").HasMaxLength(256);
            entity.Property(row => row.AccountIdentifier).HasColumnName("account_identifier").HasMaxLength(256);
            entity.Property(row => row.InstrumentIdentifier).HasColumnName("instrument_identifier").HasMaxLength(128);
            entity.Property(row => row.Side).HasColumnName("side").HasMaxLength(8);
            entity.Property(row => row.OrderType).HasColumnName("order_type").HasMaxLength(16);
            entity.Property(row => row.Quantity).HasColumnName("quantity").HasPrecision(38, 12);
            entity.Property(row => row.LimitPrice).HasColumnName("limit_price").HasPrecision(38, 12);
            entity.Property(row => row.TimeInForce).HasColumnName("time_in_force").HasMaxLength(16);
            entity.Property(row => row.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            entity.Property(row => row.BrokerClientOrderId).HasColumnName("broker_client_order_id").HasMaxLength(64);
            entity.Property(row => row.State).HasColumnName("state").HasMaxLength(32);
            entity.Property(row => row.BrokerOrderId).HasColumnName("broker_order_id").HasMaxLength(256);
            entity.Property(row => row.BrokerEventReference).HasColumnName("broker_event_reference").HasMaxLength(256);
            entity.Property(row => row.FilledQuantity).HasColumnName("filled_quantity").HasPrecision(38, 12);
            entity.Property(row => row.FirstSubmissionTime).HasColumnName("first_submission_time");
            entity.Property(row => row.LastUpdatedAt).HasColumnName("last_updated_at");
            entity.Property(row => row.TerminalAt).HasColumnName("terminal_at");
        });

        modelBuilder.Entity<CancellationRequestRow>(entity =>
        {
            entity.ToTable("cancellation_requests", table =>
            {
                table.HasCheckConstraint("ck_cancellation_requests_environment", "environment = 'PAPER'");
                table.HasCheckConstraint("ck_cancellation_requests_state",
                    "state IN ('PREPARED', 'REQUESTED', 'RECONCILIATION_REQUIRED', 'CONFIRMED', 'SUPERSEDED')");
                table.HasCheckConstraint("ck_cancellation_requests_lifecycle",
                    "(state IN ('PREPARED', 'RECONCILIATION_REQUIRED') AND broker_request_reference IS NULL AND resolution_event_reference IS NULL) OR (state = 'REQUESTED' AND broker_request_reference IS NOT NULL AND resolution_event_reference IS NULL) OR (state IN ('CONFIRMED', 'SUPERSEDED') AND resolution_event_reference IS NOT NULL)");
                table.HasCheckConstraint("ck_cancellation_requests_digest", "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.CancellationRequestId);
            entity.HasIndex(row => row.OrderId).IsUnique();
            entity.HasIndex(row => row.IdempotencyKey).IsUnique();
            entity.HasOne<OrderRow>().WithMany().HasForeignKey(row => row.OrderId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.CancellationRequestId).HasColumnName("cancellation_request_id");
            entity.Property(row => row.OrderId).HasColumnName("order_id");
            entity.Property(row => row.BrokerConnectionVersion).HasColumnName("broker_connection_version").HasColumnType("jsonb");
            entity.Property(row => row.Environment).HasColumnName("environment").HasMaxLength(8);
            entity.Property(row => row.EndpointIdentity).HasColumnName("endpoint_identity").HasMaxLength(256);
            entity.Property(row => row.AccountIdentifier).HasColumnName("account_identifier").HasMaxLength(256);
            entity.Property(row => row.BrokerClientOrderId).HasColumnName("broker_client_order_id").HasMaxLength(64);
            entity.Property(row => row.BrokerOrderId).HasColumnName("broker_order_id").HasMaxLength(256);
            entity.Property(row => row.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            entity.Property(row => row.Reason).HasColumnName("reason").HasColumnType("text");
            entity.Property(row => row.State).HasColumnName("state").HasMaxLength(32);
            entity.Property(row => row.BrokerRequestReference).HasColumnName("broker_request_reference").HasMaxLength(256);
            entity.Property(row => row.ResolutionEventReference).HasColumnName("resolution_event_reference").HasMaxLength(256);
            entity.Property(row => row.PreparedAt).HasColumnName("prepared_at");
            entity.Property(row => row.LastUpdatedAt).HasColumnName("last_updated_at");
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<BrokerOrderEventRow>(entity =>
        {
            entity.ToTable("broker_order_events", table =>
            {
                table.HasCheckConstraint("ck_broker_order_events_result", "result IN ('ACKNOWLEDGED', 'REJECTED')");
                table.HasCheckConstraint("ck_broker_order_events_digest", "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.EventId);
            entity.HasIndex(row => row.EventReference).IsUnique();
            entity.HasOne<OrderRow>().WithMany().HasForeignKey(row => row.OrderId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.EventId).HasColumnName("event_id");
            entity.Property(row => row.OrderId).HasColumnName("order_id");
            entity.Property(row => row.BrokerClientOrderId).HasColumnName("broker_client_order_id").HasMaxLength(64);
            entity.Property(row => row.BrokerOrderId).HasColumnName("broker_order_id").HasMaxLength(256);
            entity.Property(row => row.EventReference).HasColumnName("event_reference").HasMaxLength(256);
            entity.Property(row => row.Result).HasColumnName("result").HasMaxLength(16);
            entity.Property(row => row.ObservedAt).HasColumnName("observed_at");
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<BrokerExecutionEventRow>(entity =>
        {
            entity.ToTable("broker_execution_events", table =>
            {
                table.HasCheckConstraint("ck_broker_execution_events_environment", "environment = 'PAPER'");
                table.HasCheckConstraint("ck_broker_execution_events_kind", "kind IN ('ACKNOWLEDGED', 'PARTIALLY_FILLED', 'FILLED', 'CANCELED', 'REJECTED', 'EXPIRED')");
                table.HasCheckConstraint("ck_broker_execution_events_quantity", "cumulative_filled_quantity >= 0");
                table.HasCheckConstraint("ck_broker_execution_events_digest", "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.EventId);
            entity.HasIndex(row => row.EventReference).IsUnique();
            entity.HasOne<OrderRow>().WithMany().HasForeignKey(row => row.OrderId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.EventId).HasColumnName("event_id");
            entity.Property(row => row.OrderId).HasColumnName("order_id");
            entity.Property(row => row.BrokerConnectionVersion).HasColumnName("broker_connection_version").HasColumnType("jsonb");
            entity.Property(row => row.Environment).HasColumnName("environment").HasMaxLength(8);
            entity.Property(row => row.EndpointIdentity).HasColumnName("endpoint_identity").HasMaxLength(256);
            entity.Property(row => row.AccountIdentifier).HasColumnName("account_identifier").HasMaxLength(256);
            entity.Property(row => row.BrokerClientOrderId).HasColumnName("broker_client_order_id").HasMaxLength(64);
            entity.Property(row => row.BrokerOrderId).HasColumnName("broker_order_id").HasMaxLength(256);
            entity.Property(row => row.EventReference).HasColumnName("event_reference").HasMaxLength(256);
            entity.Property(row => row.Kind).HasColumnName("kind").HasMaxLength(24);
            entity.Property(row => row.CumulativeFilledQuantity).HasColumnName("cumulative_filled_quantity").HasPrecision(38, 12);
            entity.Property(row => row.BrokerEventTime).HasColumnName("broker_event_time");
            entity.Property(row => row.ReceivedAt).HasColumnName("received_at");
            entity.Property(row => row.SourcePayloadReference).HasColumnName("source_payload_reference").HasMaxLength(512);
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<FillRow>(entity =>
        {
            entity.ToTable("fills", table =>
            {
                table.HasCheckConstraint("ck_fills_values", "quantity > 0 AND quantity = trunc(quantity) AND price > 0 AND (fee_amount IS NULL OR fee_amount >= 0)");
                table.HasCheckConstraint("ck_fills_fee", "(fee_amount IS NULL AND fee_currency IS NULL) OR (fee_amount IS NOT NULL AND fee_currency ~ '^[A-Z]{3}$')");
                table.HasCheckConstraint("ck_fills_digest", "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.FillId);
            entity.HasIndex(row => row.EventId).IsUnique();
            entity.HasIndex(row => row.BrokerFillId).IsUnique().HasFilter("broker_fill_id IS NOT NULL");
            entity.HasOne<OrderRow>().WithMany().HasForeignKey(row => row.OrderId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<BrokerExecutionEventRow>().WithMany().HasForeignKey(row => row.EventId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.FillId).HasColumnName("fill_id");
            entity.Property(row => row.BrokerFillId).HasColumnName("broker_fill_id").HasMaxLength(256);
            entity.Property(row => row.OrderId).HasColumnName("order_id");
            entity.Property(row => row.EventId).HasColumnName("event_id");
            entity.Property(row => row.BrokerOrderId).HasColumnName("broker_order_id").HasMaxLength(256);
            entity.Property(row => row.InstrumentIdentifier).HasColumnName("instrument_identifier").HasMaxLength(128);
            entity.Property(row => row.Quantity).HasColumnName("quantity").HasPrecision(38, 12);
            entity.Property(row => row.Price).HasColumnName("price").HasPrecision(38, 12);
            entity.Property(row => row.FeeAmount).HasColumnName("fee_amount").HasPrecision(38, 12);
            entity.Property(row => row.FeeCurrency).HasColumnName("fee_currency").HasMaxLength(3);
            entity.Property(row => row.BrokerExecutionTime).HasColumnName("broker_execution_time");
            entity.Property(row => row.ReceivedAt).HasColumnName("received_at");
            entity.Property(row => row.SourcePayloadReference).HasColumnName("source_payload_reference").HasMaxLength(512);
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<PositionSnapshotBatchRow>(entity =>
        {
            entity.ToTable("position_snapshot_batches", table =>
            {
                table.HasCheckConstraint("ck_position_snapshot_batches_environment", "environment = 'PAPER'");
                table.HasCheckConstraint("ck_position_snapshot_batches_count", "position_count >= 0");
                table.HasCheckConstraint("ck_position_snapshot_batches_digest", "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.BatchId);
            entity.HasIndex(row => row.ObservationReference).IsUnique();
            entity.Property(row => row.BatchId).HasColumnName("batch_id");
            entity.Property(row => row.BrokerConnectionVersion).HasColumnName("broker_connection_version").HasColumnType("jsonb");
            entity.Property(row => row.Environment).HasColumnName("environment").HasMaxLength(8);
            entity.Property(row => row.EndpointIdentity).HasColumnName("endpoint_identity").HasMaxLength(256);
            entity.Property(row => row.AccountIdentifier).HasColumnName("account_identifier").HasMaxLength(256);
            entity.Property(row => row.ObservationReference).HasColumnName("observation_reference").HasMaxLength(256);
            entity.Property(row => row.PositionCount).HasColumnName("position_count");
            entity.Property(row => row.BrokerObservationTime).HasColumnName("broker_observation_time");
            entity.Property(row => row.ReceivedAt).HasColumnName("received_at");
            entity.Property(row => row.SourcePayloadReference).HasColumnName("source_payload_reference").HasMaxLength(512);
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<PositionSnapshotRow>(entity =>
        {
            entity.ToTable("position_snapshots", table =>
            {
                table.HasCheckConstraint("ck_position_snapshots_environment", "environment = 'PAPER'");
                table.HasCheckConstraint("ck_position_snapshots_values", "quantity >= 0 AND quantity = trunc(quantity) AND market_value >= 0 AND ((quantity = 0 AND average_entry_price IS NULL AND market_value = 0) OR (quantity > 0 AND average_entry_price > 0))");
                table.HasCheckConstraint("ck_position_snapshots_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("ck_position_snapshots_digest", "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.SnapshotId);
            entity.HasIndex(row => new { row.BatchId, row.InstrumentIdentifier }).IsUnique();
            entity.HasOne<PositionSnapshotBatchRow>().WithMany().HasForeignKey(row => row.BatchId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(row => row.BatchId).HasColumnName("batch_id");
            entity.Property(row => row.BrokerConnectionVersion).HasColumnName("broker_connection_version").HasColumnType("jsonb");
            entity.Property(row => row.Environment).HasColumnName("environment").HasMaxLength(8);
            entity.Property(row => row.EndpointIdentity).HasColumnName("endpoint_identity").HasMaxLength(256);
            entity.Property(row => row.AccountIdentifier).HasColumnName("account_identifier").HasMaxLength(256);
            entity.Property(row => row.InstrumentIdentifier).HasColumnName("instrument_identifier").HasMaxLength(128);
            entity.Property(row => row.Quantity).HasColumnName("quantity").HasPrecision(38, 12);
            entity.Property(row => row.AverageEntryPrice).HasColumnName("average_entry_price").HasPrecision(38, 12);
            entity.Property(row => row.MarketValue).HasColumnName("market_value").HasPrecision(38, 12);
            entity.Property(row => row.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<PostFillEvaluationRow>(entity =>
        {
            entity.ToTable("post_fill_evaluations", table =>
            {
                table.HasCheckConstraint("ck_post_fill_evaluations_values", "position_exposure >= 0 AND open_order_exposure >= 0 AND allocation_amount > 0 AND largest_instrument_exposure >= 0 AND largest_instrument_exposure <= position_exposure + open_order_exposure AND maximum_position_exposure > 0 AND maximum_portfolio_exposure >= maximum_position_exposure AND maximum_concentration > 0 AND maximum_concentration <= 1");
                table.HasCheckConstraint("ck_post_fill_evaluations_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("ck_post_fill_evaluations_result", "result IN ('PASSED', 'VIOLATION_LOCKED')");
                table.HasCheckConstraint("ck_post_fill_evaluations_result_consistency", "(result = 'PASSED' AND position_evidence_current AND position_exposure + open_order_exposure <= allocation_amount AND largest_instrument_exposure <= maximum_position_exposure AND position_exposure + open_order_exposure <= maximum_portfolio_exposure AND (position_exposure + open_order_exposure = 0 OR largest_instrument_exposure / (position_exposure + open_order_exposure) <= maximum_concentration)) OR (result = 'VIOLATION_LOCKED' AND (NOT position_evidence_current OR position_exposure + open_order_exposure > allocation_amount OR largest_instrument_exposure > maximum_position_exposure OR position_exposure + open_order_exposure > maximum_portfolio_exposure OR (position_exposure + open_order_exposure > 0 AND largest_instrument_exposure / (position_exposure + open_order_exposure) > maximum_concentration)))");
                table.HasCheckConstraint("ck_post_fill_evaluations_digest", "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.EvaluationId);
            entity.HasIndex(row => row.FillId).IsUnique();
            entity.HasIndex(row => row.PositionBatchId).IsUnique();
            entity.HasOne<FillRow>().WithMany().HasForeignKey(row => row.FillId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OrderRow>().WithMany().HasForeignKey(row => row.OrderId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GoalVersionRow>().WithMany().HasForeignKey(row => row.GoalVersionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SessionRow>().WithMany().HasForeignKey(row => row.SessionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AccountAllocationEntryRow>().WithMany().HasForeignKey(row => row.AllocationEntryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PositionSnapshotBatchRow>().WithMany().HasForeignKey(row => row.PositionBatchId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.EvaluationId).HasColumnName("evaluation_id");
            entity.Property(row => row.FillId).HasColumnName("fill_id");
            entity.Property(row => row.OrderId).HasColumnName("order_id");
            entity.Property(row => row.GoalVersionId).HasColumnName("goal_version_id");
            entity.Property(row => row.SessionId).HasColumnName("session_id");
            entity.Property(row => row.RiskPolicyVersion).HasColumnName("risk_policy_version").HasColumnType("jsonb");
            entity.Property(row => row.AllocationEntryId).HasColumnName("allocation_entry_id");
            entity.Property(row => row.PositionBatchId).HasColumnName("position_batch_id");
            entity.Property(row => row.PositionExposure).HasColumnName("position_exposure").HasPrecision(38, 12);
            entity.Property(row => row.OpenOrderExposure).HasColumnName("open_order_exposure").HasPrecision(38, 12);
            entity.Property(row => row.AllocationAmount).HasColumnName("allocation_amount").HasPrecision(38, 12);
            entity.Property(row => row.LargestInstrumentExposure).HasColumnName("largest_instrument_exposure").HasPrecision(38, 12);
            entity.Property(row => row.MaximumPositionExposure).HasColumnName("maximum_position_exposure").HasPrecision(38, 12);
            entity.Property(row => row.MaximumPortfolioExposure).HasColumnName("maximum_portfolio_exposure").HasPrecision(38, 12);
            entity.Property(row => row.MaximumConcentration).HasColumnName("maximum_concentration").HasPrecision(18, 12);
            entity.Property(row => row.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(row => row.PositionEvidenceCurrent).HasColumnName("position_evidence_current");
            entity.Property(row => row.Result).HasColumnName("result").HasMaxLength(24);
            entity.Property(row => row.EvaluatedAt).HasColumnName("evaluated_at");
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<PostFillSafetyResponseRow>(entity =>
        {
            entity.ToTable("post_fill_safety_responses", table =>
            {
                table.HasCheckConstraint("ck_post_fill_safety_responses_environment", "environment = 'PAPER'");
                table.HasCheckConstraint("ck_post_fill_safety_responses_states", "reconciliation_state = 'REQUESTED' AND notification_state = 'PENDING'");
                table.HasCheckConstraint("ck_post_fill_safety_responses_digest", "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.ResponseId);
            entity.HasIndex(row => row.EvaluationId).IsUnique();
            entity.HasOne<PostFillEvaluationRow>().WithMany().HasForeignKey(row => row.EvaluationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GoalVersionRow>().WithMany().HasForeignKey(row => row.GoalVersionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SessionRow>().WithMany().HasForeignKey(row => row.SessionId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.ResponseId).HasColumnName("response_id");
            entity.Property(row => row.EvaluationId).HasColumnName("evaluation_id");
            entity.Property(row => row.GoalVersionId).HasColumnName("goal_version_id");
            entity.Property(row => row.SessionId).HasColumnName("session_id");
            entity.Property(row => row.BrokerConnectionVersion).HasColumnName("broker_connection_version").HasColumnType("jsonb");
            entity.Property(row => row.Environment).HasColumnName("environment").HasMaxLength(8);
            entity.Property(row => row.EndpointIdentity).HasColumnName("endpoint_identity").HasMaxLength(256);
            entity.Property(row => row.AccountIdentifier).HasColumnName("account_identifier").HasMaxLength(256);
            entity.Property(row => row.ReconciliationState).HasColumnName("reconciliation_state").HasMaxLength(16);
            entity.Property(row => row.NotificationState).HasColumnName("notification_state").HasMaxLength(16);
            entity.Property(row => row.Reason).HasColumnName("reason").HasColumnType("text");
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });

        modelBuilder.Entity<ResearchResultRow>(entity =>
        {
            entity.ToTable("research_results", table =>
            {
                table.HasCheckConstraint("ck_research_results_time", "completed_at >= started_at");
                table.HasCheckConstraint("ck_research_results_snapshot_digest", "data_snapshot_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_research_results_content_digest", "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.ResearchResultId);
            entity.HasIndex(row => row.SessionId).IsUnique();
            entity.HasOne<SessionRow>()
                .WithMany()
                .HasForeignKey(row => row.SessionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GoalActivationRow>()
                .WithMany()
                .HasForeignKey(row => row.ActivationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GoalVersionRow>()
                .WithMany()
                .HasForeignKey(row => row.GoalVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DataSnapshotRow>()
                .WithMany()
                .HasForeignKey(row => row.DataSnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.ResearchResultId).HasColumnName("research_result_id");
            entity.Property(row => row.SessionId).HasColumnName("session_id");
            entity.Property(row => row.ActivationId).HasColumnName("activation_id");
            entity.Property(row => row.GoalVersionId).HasColumnName("goal_version_id");
            entity.Property(row => row.DataSnapshotId).HasColumnName("data_snapshot_id");
            entity.Property(row => row.DataSnapshotDigest).HasColumnName("data_snapshot_digest").HasMaxLength(64);
            entity.Property(row => row.StrategyVersion).HasColumnName("strategy_version").HasColumnType("jsonb");
            entity.Property(row => row.DataPolicyVersion).HasColumnName("data_policy_version").HasColumnType("jsonb");
            entity.Property(row => row.SourceObservations).HasColumnName("source_observations").HasColumnType("jsonb");
            entity.Property(row => row.OutputMetrics).HasColumnName("output_metrics").HasColumnType("jsonb");
            entity.Property(row => row.Evidence).HasColumnName("evidence").HasColumnType("text");
            entity.Property(row => row.StartedAt).HasColumnName("started_at");
            entity.Property(row => row.CompletedAt).HasColumnName("completed_at");
            entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
        });
    }
}

internal sealed class GoalDraftRow
{
    public Guid DraftId { get; set; }
    public Guid GoalId { get; set; }
    public Guid ProposedGoalVersionId { get; set; }
    public int VersionNumber { get; set; }
    public int Revision { get; set; }
    public string State { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = string.Empty;
    public Guid CreatorId { get; set; }
    public Guid LastEditorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}

internal sealed class GoalVersionRow
{
    public Guid GoalVersionId { get; set; }
    public Guid GoalId { get; set; }
    public int VersionNumber { get; set; }
    public string Mode { get; set; } = string.Empty;
    public string ContentDigest { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = string.Empty;
    public Guid CreatorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class GoalActivationRow
{
    public Guid ActivationId { get; set; }
    public Guid GoalId { get; set; }
    public Guid GoalVersionId { get; set; }
    public Guid OperatorId { get; set; }
    public string State { get; set; } = string.Empty;
    public string ActivationEvidence { get; set; } = string.Empty;
    public string AuthoritySummary { get; set; } = string.Empty;
    public DateTimeOffset ActivationTime { get; set; }
    public DateTimeOffset? ExpiryTime { get; set; }
    public DateTimeOffset? RevocationTime { get; set; }
    public string? RevocationReason { get; set; }
    public int Revision { get; set; }
}

internal sealed class SessionRow
{
    public Guid SessionId { get; set; }
    public Guid GoalVersionId { get; set; }
    public Guid ActivationId { get; set; }
    public string Mode { get; set; } = string.Empty;
    public DateTimeOffset StartTime { get; set; }
    public string State { get; set; } = string.Empty;
    public DateTimeOffset? EndTime { get; set; }
    public string? FailureReason { get; set; }
    public Guid? InputDataSnapshotId { get; set; }
    public long? ReplaySeed { get; set; }
    public string? ObservedEnvironment { get; set; }
    public string? ObservedEndpointIdentity { get; set; }
    public string? ObservedAccountIdentifier { get; set; }
    public int Revision { get; set; }
}

internal sealed class ExecutableSessionPreflightRow
{
    public Guid PreflightId { get; set; }
    public Guid SessionId { get; set; }
    public Guid GoalVersionId { get; set; }
    public Guid ActivationId { get; set; }
    public string BrokerConnectionVersion { get; set; } = string.Empty;
    public Guid? ReconciliationId { get; set; }
    public string? ObservedEnvironment { get; set; }
    public string? ObservedEndpointIdentity { get; set; }
    public string? ObservedAccountIdentifier { get; set; }
    public string ObservedCapabilities { get; set; } = string.Empty;
    public DateTimeOffset? BrokerObservedAt { get; set; }
    public DateTimeOffset? BrokerTime { get; set; }
    public DateTimeOffset? CapitalObservedAt { get; set; }
    public DateTimeOffset? ReconciliationObservedAt { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal? PositionExposure { get; set; }
    public decimal? OpenOrderExposure { get; set; }
    public decimal? BuyingPower { get; set; }
    public string Checks { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public DateTimeOffset CompletedAt { get; set; }
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class AccountAllocationLimitRow
{
    public string ReferenceDigest { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string BrokerConnectionVersion { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string EndpointIdentity { get; set; } = string.Empty;
    public string AccountIdentifier { get; set; } = string.Empty;
    public decimal MaximumAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public Guid CreatorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class AccountAllocationEntryRow
{
    public Guid AllocationEntryId { get; set; }
    public Guid ActivationId { get; set; }
    public Guid GoalVersionId { get; set; }
    public string AccountLimitDigest { get; set; } = string.Empty;
    public string AccountLimitVersion { get; set; } = string.Empty;
    public string BrokerConnectionDigest { get; set; } = string.Empty;
    public string BrokerConnectionVersion { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string EndpointIdentity { get; set; } = string.Empty;
    public string AccountIdentifier { get; set; } = string.Empty;
    public decimal AllocationAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string ExposureMethodIdentifier { get; set; } = string.Empty;
    public DateTimeOffset EffectiveStart { get; set; }
    public DateTimeOffset? EffectiveEnd { get; set; }
    public string State { get; set; } = string.Empty;
    public string ObservedEnvironment { get; set; } = string.Empty;
    public string ObservedEndpointIdentity { get; set; } = string.Empty;
    public string ObservedAccountIdentifier { get; set; } = string.Empty;
    public string ObservedCapabilities { get; set; } = string.Empty;
    public DateTimeOffset ObservedAt { get; set; }
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class AuditChainHeadRow
{
    public string ChainId { get; set; } = string.Empty;
    public long LastSequence { get; set; }
    public string LastHash { get; set; } = string.Empty;
}

internal sealed class AuditEventRow
{
    public Guid EventId { get; set; }
    public long Sequence { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTimeOffset EventTime { get; set; }
    public Guid ActorId { get; set; }
    public Guid? GoalVersionId { get; set; }
    public Guid? SessionId { get; set; }
    public string RelatedRecordIdentifiersJson { get; set; } = string.Empty;
    public string? PreviousState { get; set; }
    public string NewState { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string SourceReference { get; set; } = string.Empty;
    public string CanonicalPayload { get; set; } = string.Empty;
    public string PreviousHash { get; set; } = string.Empty;
    public string EventHash { get; set; } = string.Empty;
}

internal sealed class DataSnapshotRow
{
    public Guid DataSnapshotId { get; set; }
    public string DataSetVersion { get; set; } = string.Empty;
    public string DataPolicyVersion { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public DateTimeOffset RangeStart { get; set; }
    public DateTimeOffset RangeEnd { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public string SourceObservations { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];
    public string PayloadDigest { get; set; } = string.Empty;
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class ReplayResultRow
{
    public Guid ReplayResultId { get; set; }
    public Guid SessionId { get; set; }
    public Guid ActivationId { get; set; }
    public Guid GoalVersionId { get; set; }
    public Guid DataSnapshotId { get; set; }
    public string DataSnapshotDigest { get; set; } = string.Empty;
    public DateTimeOffset RangeStart { get; set; }
    public DateTimeOffset RangeEnd { get; set; }
    public string StrategyVersion { get; set; } = string.Empty;
    public string DataPolicyVersion { get; set; } = string.Empty;
    public string RiskPolicyVersion { get; set; } = string.Empty;
    public long Seed { get; set; }
    public string OutputMetrics { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string OutputDigest { get; set; } = string.Empty;
}

internal sealed class DecisionRow
{
    public Guid DecisionId { get; set; }
    public Guid SessionId { get; set; }
    public Guid GoalVersionId { get; set; }
    public string StrategyVersion { get; set; } = string.Empty;
    public Guid InputSnapshotId { get; set; }
    public DateTimeOffset DecisionTime { get; set; }
    public string ProposedAction { get; set; } = string.Empty;
    public string? InstrumentIdentifier { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? LimitPrice { get; set; }
    public string Explanation { get; set; } = string.Empty;
    public decimal? Confidence { get; set; }
    public string GuardResults { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class RiskEvaluationRow
{
    public Guid RiskEvaluationId { get; set; }
    public Guid IntentId { get; set; }
    public Guid DecisionId { get; set; }
    public Guid GoalVersionId { get; set; }
    public Guid SessionId { get; set; }
    public string RiskPolicyVersion { get; set; } = string.Empty;
    public Guid AllocationEntryId { get; set; }
    public Guid ReconciliationId { get; set; }
    public decimal PositionExposure { get; set; }
    public decimal OpenOrderExposure { get; set; }
    public decimal RequestedExposure { get; set; }
    public decimal BuyingPower { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Checks { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public DateTimeOffset EvaluatedAt { get; set; }
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class ExecutionIntentRow
{
    public Guid IntentId { get; set; }
    public Guid DecisionId { get; set; }
    public Guid GoalVersionId { get; set; }
    public Guid SessionId { get; set; }
    public string Mode { get; set; } = string.Empty;
    public string BrokerConnectionVersion { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string EndpointIdentity { get; set; } = string.Empty;
    public string AccountIdentifier { get; set; } = string.Empty;
    public string InstrumentIdentifier { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal LimitPrice { get; set; }
    public string TimeInForce { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid RiskEvaluationId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string State { get; set; } = string.Empty;
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class ReviewApprovalRow
{
    public Guid ApprovalId { get; set; }
    public Guid IntentId { get; set; }
    public Guid? ReconciliationId { get; set; }
    public Guid? ReservationId { get; set; }
    public Guid OperatorId { get; set; }
    public string Result { get; set; } = string.Empty;
    public DateTimeOffset ActionTime { get; set; }
    public string IntentDigest { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public string? OperatorNote { get; set; }
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class KillSwitchRow
{
    public Guid KillSwitchId { get; set; }
    public string ScopeType { get; set; } = string.Empty;
    public string ScopeIdentifier { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public int Revision { get; set; }
    public DateTimeOffset ConfiguredAt { get; set; }
    public DateTimeOffset? TriggeredAt { get; set; }
    public string? TriggerReason { get; set; }
    public Guid? RecoveryReconciliationId { get; set; }
    public DateTimeOffset? RecoveryRequiredAt { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
}

internal sealed class ReconciliationRow
{
    public Guid ReconciliationId { get; set; }
    public long Sequence { get; set; }
    public Guid GoalVersionId { get; set; }
    public string BrokerConnectionVersion { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string EndpointIdentity { get; set; } = string.Empty;
    public string AccountIdentifier { get; set; } = string.Empty;
    public string ReconciliationPolicyVersion { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public decimal PositionExposure { get; set; }
    public decimal OpenOrderExposure { get; set; }
    public decimal BuyingPower { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string ComparedOrderRange { get; set; } = string.Empty;
    public string ComparedFillRange { get; set; } = string.Empty;
    public string ComparedPositionSnapshot { get; set; } = string.Empty;
    public string Differences { get; set; } = string.Empty;
    public string Corrections { get; set; } = string.Empty;
    public string? OperatorAction { get; set; }
    public string Result { get; set; } = string.Empty;
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class ExposureReservationRow
{
    public Guid ReservationId { get; set; }
    public Guid IntentId { get; set; }
    public Guid GoalVersionId { get; set; }
    public Guid AllocationEntryId { get; set; }
    public string BrokerConnectionVersion { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string EndpointIdentity { get; set; } = string.Empty;
    public string AccountIdentifier { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal RemainingAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string State { get; set; } = string.Empty;
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
    public int Revision { get; set; }
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class PreSubmitEvaluationRow
{
    public Guid EvaluationId { get; set; }
    public Guid IntentId { get; set; }
    public string IntentDigest { get; set; } = string.Empty;
    public Guid? ApprovalId { get; set; }
    public Guid ReservationId { get; set; }
    public Guid ReconciliationId { get; set; }
    public decimal PositionExposure { get; set; }
    public decimal OpenOrderExposure { get; set; }
    public decimal BuyingPower { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Checks { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public DateTimeOffset EvaluatedAt { get; set; }
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class OrderRow
{
    public Guid OrderId { get; set; }
    public Guid IntentId { get; set; }
    public string IntentDigest { get; set; } = string.Empty;
    public Guid EvaluationId { get; set; }
    public string BrokerConnectionVersion { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string EndpointIdentity { get; set; } = string.Empty;
    public string AccountIdentifier { get; set; } = string.Empty;
    public string InstrumentIdentifier { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal LimitPrice { get; set; }
    public string TimeInForce { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string BrokerClientOrderId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? BrokerOrderId { get; set; }
    public string? BrokerEventReference { get; set; }
    public decimal FilledQuantity { get; set; }
    public DateTimeOffset FirstSubmissionTime { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }
    public DateTimeOffset? TerminalAt { get; set; }
}

internal sealed class CancellationRequestRow
{
    public Guid CancellationRequestId { get; set; }
    public Guid OrderId { get; set; }
    public string BrokerConnectionVersion { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string EndpointIdentity { get; set; } = string.Empty;
    public string AccountIdentifier { get; set; } = string.Empty;
    public string BrokerClientOrderId { get; set; } = string.Empty;
    public string BrokerOrderId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? BrokerRequestReference { get; set; }
    public string? ResolutionEventReference { get; set; }
    public DateTimeOffset PreparedAt { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class BrokerOrderEventRow
{
    public Guid EventId { get; set; }
    public Guid OrderId { get; set; }
    public string BrokerClientOrderId { get; set; } = string.Empty;
    public string? BrokerOrderId { get; set; }
    public string EventReference { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public DateTimeOffset ObservedAt { get; set; }
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class BrokerExecutionEventRow
{
    public Guid EventId { get; set; }
    public Guid OrderId { get; set; }
    public string BrokerConnectionVersion { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string EndpointIdentity { get; set; } = string.Empty;
    public string AccountIdentifier { get; set; } = string.Empty;
    public string BrokerClientOrderId { get; set; } = string.Empty;
    public string BrokerOrderId { get; set; } = string.Empty;
    public string EventReference { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public decimal CumulativeFilledQuantity { get; set; }
    public DateTimeOffset BrokerEventTime { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string SourcePayloadReference { get; set; } = string.Empty;
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class FillRow
{
    public Guid FillId { get; set; }
    public string? BrokerFillId { get; set; }
    public Guid OrderId { get; set; }
    public Guid EventId { get; set; }
    public string BrokerOrderId { get; set; } = string.Empty;
    public string InstrumentIdentifier { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal? FeeAmount { get; set; }
    public string? FeeCurrency { get; set; }
    public DateTimeOffset BrokerExecutionTime { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string SourcePayloadReference { get; set; } = string.Empty;
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class PositionSnapshotBatchRow
{
    public Guid BatchId { get; set; }
    public string BrokerConnectionVersion { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string EndpointIdentity { get; set; } = string.Empty;
    public string AccountIdentifier { get; set; } = string.Empty;
    public string ObservationReference { get; set; } = string.Empty;
    public int PositionCount { get; set; }
    public DateTimeOffset BrokerObservationTime { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string SourcePayloadReference { get; set; } = string.Empty;
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class PositionSnapshotRow
{
    public Guid SnapshotId { get; set; }
    public Guid BatchId { get; set; }
    public string BrokerConnectionVersion { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string EndpointIdentity { get; set; } = string.Empty;
    public string AccountIdentifier { get; set; } = string.Empty;
    public string InstrumentIdentifier { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal? AverageEntryPrice { get; set; }
    public decimal MarketValue { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class PostFillEvaluationRow
{
    public Guid EvaluationId { get; set; }
    public Guid FillId { get; set; }
    public Guid OrderId { get; set; }
    public Guid GoalVersionId { get; set; }
    public Guid SessionId { get; set; }
    public string RiskPolicyVersion { get; set; } = string.Empty;
    public Guid AllocationEntryId { get; set; }
    public Guid PositionBatchId { get; set; }
    public decimal PositionExposure { get; set; }
    public decimal OpenOrderExposure { get; set; }
    public decimal AllocationAmount { get; set; }
    public decimal LargestInstrumentExposure { get; set; }
    public decimal MaximumPositionExposure { get; set; }
    public decimal MaximumPortfolioExposure { get; set; }
    public decimal MaximumConcentration { get; set; }
    public string Currency { get; set; } = string.Empty;
    public bool PositionEvidenceCurrent { get; set; }
    public string Result { get; set; } = string.Empty;
    public DateTimeOffset EvaluatedAt { get; set; }
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class PostFillSafetyResponseRow
{
    public Guid ResponseId { get; set; }
    public Guid EvaluationId { get; set; }
    public Guid GoalVersionId { get; set; }
    public Guid SessionId { get; set; }
    public string BrokerConnectionVersion { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string EndpointIdentity { get; set; } = string.Empty;
    public string AccountIdentifier { get; set; } = string.Empty;
    public string ReconciliationState { get; set; } = string.Empty;
    public string NotificationState { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string ContentDigest { get; set; } = string.Empty;
}

internal sealed class ResearchResultRow
{
    public Guid ResearchResultId { get; set; }
    public Guid SessionId { get; set; }
    public Guid ActivationId { get; set; }
    public Guid GoalVersionId { get; set; }
    public Guid DataSnapshotId { get; set; }
    public string DataSnapshotDigest { get; set; } = string.Empty;
    public string StrategyVersion { get; set; } = string.Empty;
    public string DataPolicyVersion { get; set; } = string.Empty;
    public string SourceObservations { get; set; } = string.Empty;
    public string OutputMetrics { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string ContentDigest { get; set; } = string.Empty;
}
