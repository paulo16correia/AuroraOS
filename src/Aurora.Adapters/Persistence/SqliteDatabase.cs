using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Persistence;

/// <summary>
/// Idempotent schema bootstrap for the Aurora persistence store. Safe to invoke repeatedly;
/// every statement uses <c>IF NOT EXISTS</c> semantics and the seed row is only written when absent.
/// </summary>
public sealed class SqliteDatabase
{
    private const string SchemaDdl = """
        CREATE TABLE IF NOT EXISTS schema_version (
          version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS audit_record (
          sequence INTEGER PRIMARY KEY AUTOINCREMENT,
          record_id TEXT NOT NULL,
          principal_client_id TEXT NOT NULL,
          principal_os_user TEXT NOT NULL,
          action_id TEXT NOT NULL,
          input_hash TEXT NOT NULL,
          outcome TEXT NOT NULL,
          created_at_utc TEXT NOT NULL,
          previous_hash TEXT NOT NULL,
          record_hash TEXT NOT NULL,
          risk TEXT NULL,
          via TEXT NULL,
          decision TEXT NULL,
          policy_ids TEXT NULL,
          reason TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS idempotency (
          principal_client_id TEXT NOT NULL,
          idempotency_key TEXT NOT NULL,
          request_hash TEXT NOT NULL,
          state TEXT NOT NULL,
          result_json TEXT NULL,
          created_at_utc TEXT NOT NULL,
          updated_at_utc TEXT NOT NULL,
          PRIMARY KEY (principal_client_id, idempotency_key)
        );

        CREATE TABLE IF NOT EXISTS approval (
          approval_id TEXT PRIMARY KEY,
          principal_client_id TEXT NOT NULL,
          principal_os_user TEXT NOT NULL,
          action_id TEXT NOT NULL,
          scope_hash TEXT NOT NULL,
          status TEXT NOT NULL,
          created_at_utc TEXT NOT NULL,
          expires_at_utc TEXT NOT NULL,
          decided_at_utc TEXT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS idx_approval_one_live_pending
          ON approval(principal_client_id, action_id, scope_hash)
          WHERE status = 'PENDING';

        CREATE INDEX IF NOT EXISTS idx_approval_scope
          ON approval(principal_client_id, action_id, scope_hash);

        CREATE TABLE IF NOT EXISTS aurora_action (
          id TEXT PRIMARY KEY,
          decision_id TEXT NOT NULL,
          effect_type TEXT NOT NULL,
          target_ref TEXT NOT NULL,
          parameters_hash TEXT NOT NULL,
          reversible INTEGER NOT NULL,
          state TEXT NOT NULL,
          tool_call_id TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_action_state ON aurora_action(state);

        CREATE TABLE IF NOT EXISTS observation (
          id TEXT PRIMARY KEY,
          action_id TEXT NOT NULL,
          observer TEXT NOT NULL,
          observed_at_utc TEXT NOT NULL,
          modality TEXT NOT NULL,
          outcome TEXT NOT NULL,
          payload_ref TEXT NULL,
          integrity TEXT NOT NULL,
          external_ref TEXT NULL,
          state TEXT NOT NULL,
          rejection_reason TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_observation_action ON observation(action_id, state);

        CREATE TABLE IF NOT EXISTS reflection (
          id TEXT PRIMARY KEY,
          observation_id TEXT NOT NULL,
          outcome TEXT NOT NULL,
          evidence_refs TEXT NOT NULL,
          lessons TEXT NOT NULL,
          proposal_refs TEXT NOT NULL,
          state TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS learning_proposal (
          id TEXT PRIMARY KEY,
          reflection_id TEXT NOT NULL,
          type TEXT NOT NULL,
          change_set_json TEXT NOT NULL,
          evaluation_plan TEXT NOT NULL,
          rollback_plan TEXT NOT NULL,
          state TEXT NOT NULL,
          expected_benefit TEXT NOT NULL DEFAULT '',
          risk TEXT NOT NULL DEFAULT 'HIGH',
          evidence_refs TEXT NOT NULL DEFAULT ''
        );

        -- One test of one proposal (RFC 08). Kept rather than overwritten: a proposal evaluated
        -- twice has two answers, and which one was current when it was applied is the question
        -- somebody asks after it goes wrong.
        CREATE TABLE IF NOT EXISTS evaluation_run (
          id TEXT PRIMARY KEY,
          proposal_id TEXT NOT NULL,
          test_scope TEXT NOT NULL,
          dataset_ref TEXT NOT NULL,
          metrics_json TEXT NOT NULL,
          verdict TEXT NOT NULL,
          executed_at_utc TEXT NOT NULL
        );

        -- RFC 09 rule 5. Kept after resolution rather than deleted: a system whose incident log
        -- empties itself is a system that cannot show it has ever been attacked.
        CREATE TABLE IF NOT EXISTS incident (
          id TEXT PRIMARY KEY,
          event_id TEXT NOT NULL,
          severity TEXT NOT NULL,
          type TEXT NOT NULL,
          correlation_id TEXT NOT NULL,
          actor_ref TEXT NOT NULL,
          resource_ref TEXT NOT NULL,
          decision_ref TEXT NULL,
          evidence_ref TEXT NOT NULL,
          detected_at_utc TEXT NOT NULL,
          status TEXT NOT NULL,
          containment_actions TEXT NOT NULL,
          opened_at_utc TEXT NOT NULL,
          contained_at_utc TEXT NULL,
          resolved_at_utc TEXT NULL,
          resolution TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS tool_manifest (
          tool_id TEXT PRIMARY KEY,
          version TEXT NOT NULL,
          provider TEXT NOT NULL,
          capabilities TEXT NOT NULL,
          input_schema TEXT NOT NULL,
          output_schema TEXT NOT NULL,
          effects TEXT NOT NULL,
          data_classes_in TEXT NOT NULL,
          data_classes_out TEXT NOT NULL,
          auth_mode TEXT NOT NULL,
          timeout_seconds INTEGER NOT NULL,
          rate_limit_per_minute INTEGER NOT NULL,
          requires_approval INTEGER NOT NULL,
          secret_reference_id TEXT NULL,
          disabled_reason TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS tool_call (
          id TEXT PRIMARY KEY,
          work_item_id TEXT NOT NULL,
          task_id TEXT NULL,
          tool_id TEXT NOT NULL,
          capability TEXT NOT NULL,
          input_redacted_json TEXT NOT NULL,
          input_hash TEXT NOT NULL,
          idempotency_key TEXT NULL,
          status TEXT NOT NULL,
          policy_decision_ids TEXT NOT NULL,
          approval_id TEXT NULL,
          started_at_utc TEXT NULL,
          ended_at_utc TEXT NULL,
          external_reference TEXT NULL,
          output_ref TEXT NULL,
          error_code TEXT NULL,
          retry_after_utc TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_tool_call_status ON tool_call(status, tool_id);

        CREATE TABLE IF NOT EXISTS capability_definition (
          id TEXT PRIMARY KEY,
          domain TEXT NOT NULL,
          intent_schema TEXT NOT NULL,
          effect_classes TEXT NOT NULL,
          risk_class TEXT NOT NULL,
          required_permissions TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS capability_provider (
          id TEXT PRIMARY KEY,
          capability_id TEXT NOT NULL,
          application_id TEXT NOT NULL,
          tool_ref TEXT NOT NULL,
          priority INTEGER NOT NULL,
          available INTEGER NOT NULL,
          cost_estimate REAL NOT NULL,
          data_classes TEXT NOT NULL,
          constraints TEXT NOT NULL,
          declared_effects TEXT NOT NULL,
          health_ref TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_provider_capability
          ON capability_provider(capability_id, priority);

        CREATE TABLE IF NOT EXISTS capability_request (
          id TEXT PRIMARY KEY,
          decision_ref TEXT NULL,
          capability_id TEXT NOT NULL,
          intent_payload_json TEXT NOT NULL,
          target_constraints TEXT NOT NULL,
          status TEXT NOT NULL,
          pinned_provider_id TEXT NULL,
          preferred_provider_id TEXT NULL,
          resolved_provider_id TEXT NULL,
          blocked_reason TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS resolution_verdict (
          id TEXT PRIMARY KEY,
          request_id TEXT NOT NULL,
          provider_id TEXT NOT NULL,
          eligible INTEGER NOT NULL,
          reason TEXT NOT NULL,
          explanation TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_verdict_request ON resolution_verdict(request_id);

        CREATE TABLE IF NOT EXISTS goal (
          id TEXT PRIMARY KEY,
          title TEXT NOT NULL,
          outcome TEXT NOT NULL,
          owner_id TEXT NOT NULL,
          priority INTEGER NOT NULL,
          status TEXT NOT NULL,
          constraints_json TEXT NOT NULL,
          success_criteria TEXT NOT NULL,
          deadline_at_utc TEXT NULL,
          budget_json TEXT NOT NULL,
          created_from_ref TEXT NULL,
          approval_policy_id TEXT NULL,
          blocked_reason TEXT NULL,
          mission_ref TEXT NULL,
          ad_hoc_review_at_utc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS planned_task (
          id TEXT PRIMARY KEY,
          goal_id TEXT NOT NULL,
          title TEXT NOT NULL,
          description TEXT NOT NULL,
          kind TEXT NOT NULL,
          status TEXT NOT NULL,
          dependencies TEXT NOT NULL,
          inputs_json TEXT NOT NULL,
          expected_output_schema TEXT NULL,
          risk TEXT NOT NULL,
          assigned_to TEXT NOT NULL,
          retry_policy TEXT NOT NULL,
          idempotency_key TEXT NULL,
          acceptance_tests TEXT NOT NULL,
          diagnosis TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_task_goal ON planned_task(goal_id, status);

        CREATE TABLE IF NOT EXISTS plan (
          id TEXT PRIMARY KEY,
          goal_id TEXT NOT NULL,
          revision INTEGER NOT NULL,
          rationale TEXT NOT NULL,
          assumptions TEXT NOT NULL,
          task_ids TEXT NOT NULL,
          status TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_plan_goal ON plan(goal_id, revision);

        CREATE TABLE IF NOT EXISTS task_transition (
          id TEXT PRIMARY KEY,
          task_id TEXT NOT NULL,
          from_state TEXT NOT NULL,
          to_state TEXT NOT NULL,
          evidence_refs TEXT NOT NULL,
          note TEXT NULL,
          at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS cognitive_cycle (
          id TEXT PRIMARY KEY,
          work_item_id TEXT NOT NULL,
          stage TEXT NOT NULL,
          status TEXT NOT NULL,
          ingress_ref TEXT NOT NULL,
          mcp_session_ref TEXT NULL,
          started_at_utc TEXT NOT NULL,
          deadline_at_utc TEXT NULL,
          completed_at_utc TEXT NULL,
          executed INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS cycle_stage_record (
          id TEXT PRIMARY KEY,
          cycle_id TEXT NOT NULL,
          stage TEXT NOT NULL,
          input_refs TEXT NOT NULL,
          output_refs TEXT NOT NULL,
          decision_ref TEXT NULL,
          started_at_utc TEXT NOT NULL,
          ended_at_utc TEXT NULL,
          status TEXT NOT NULL,
          note TEXT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS idx_stage_once ON cycle_stage_record(cycle_id, stage);

        CREATE TABLE IF NOT EXISTS decision (
          id TEXT PRIMARY KEY,
          cycle_id TEXT NOT NULL,
          mode TEXT NOT NULL,
          objective_ref TEXT NULL,
          selected_option_json TEXT NOT NULL,
          alternatives_json TEXT NOT NULL,
          evidence_refs TEXT NOT NULL,
          uncertainty TEXT NOT NULL,
          risk_level TEXT NOT NULL,
          confidence REAL NOT NULL,
          policy_decision_ids TEXT NOT NULL,
          approval_required INTEGER NOT NULL,
          expiry_at_utc TEXT NULL,
          status TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_decision_cycle ON decision(cycle_id, status);

        -- RFC 035 rule 2. Separate from the decision so the assessment can be re-derived from the
        -- decision and compared against what was stored, rather than only trusted.
        CREATE TABLE IF NOT EXISTS constitutional_assessment (
          id TEXT PRIMARY KEY,
          subject_ref TEXT NOT NULL,
          articles_checked TEXT NOT NULL,
          result TEXT NOT NULL,
          conflicts_json TEXT NOT NULL,
          evidence_refs TEXT NOT NULL,
          assessed_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS attention_set (
          id TEXT PRIMARY KEY,
          cycle_id TEXT NOT NULL UNIQUE,
          token_budget INTEGER NOT NULL,
          item_limit INTEGER NOT NULL,
          status TEXT NOT NULL,
          selected_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS attention_item (
          id TEXT PRIMARY KEY,
          set_id TEXT NOT NULL,
          ref TEXT NOT NULL,
          kind TEXT NOT NULL,
          relevance REAL NOT NULL,
          urgency REAL NOT NULL,
          novelty REAL NOT NULL,
          impact REAL NOT NULL,
          confidence REAL NOT NULL,
          recency REAL NOT NULL,
          sensitivity TEXT NOT NULL,
          token_cost INTEGER NOT NULL,
          expires_at_utc TEXT NULL,
          score REAL NOT NULL,
          reason_codes TEXT NOT NULL,
          selected INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_attention_item_set ON attention_item(set_id, selected);

        CREATE TABLE IF NOT EXISTS working_memory (
          id TEXT PRIMARY KEY,
          cycle_id TEXT NOT NULL,
          session_id TEXT NULL,
          status TEXT NOT NULL,
          capacity_tokens INTEGER NOT NULL,
          capacity_items INTEGER NOT NULL,
          sensitivity_ceiling TEXT NOT NULL,
          expires_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS working_item (
          id TEXT PRIMARY KEY,
          working_memory_id TEXT NOT NULL,
          type TEXT NOT NULL,
          payload_json TEXT NULL,
          payload_ref TEXT NULL,
          source_refs TEXT NOT NULL,
          confidence REAL NOT NULL,
          sensitivity TEXT NOT NULL,
          token_cost INTEGER NOT NULL,
          created_at_utc TEXT NOT NULL,
          expires_at_utc TEXT NULL,
          disposition TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_working_item_frame ON working_item(working_memory_id);

        CREATE TABLE IF NOT EXISTS world_version (
          id TEXT PRIMARY KEY,
          mind_id TEXT NOT NULL,
          parent_version_id TEXT NULL,
          status TEXT NOT NULL,
          created_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS world_assertion (
          id TEXT PRIMARY KEY,
          subject_ref TEXT NOT NULL,
          predicate TEXT NOT NULL,
          category TEXT NOT NULL,
          object_ref TEXT NULL,
          literal TEXT NULL,
          evidence_refs TEXT NOT NULL,
          confidence REAL NOT NULL,
          valid_from_utc TEXT NOT NULL,
          valid_to_utc TEXT NULL,
          observed_at_utc TEXT NOT NULL,
          asserted_at_utc TEXT NOT NULL,
          status TEXT NOT NULL,
          version_id TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_world_subject
          ON world_assertion(subject_ref, predicate, status);

        CREATE TABLE IF NOT EXISTS entity_resolution (
          id TEXT PRIMARY KEY,
          candidate_ref TEXT NOT NULL,
          observed_name TEXT NOT NULL,
          match_score REAL NOT NULL,
          evidence_refs TEXT NOT NULL,
          decision TEXT NOT NULL,
          decided_by TEXT NOT NULL,
          decided_at_utc TEXT NOT NULL,
          matched_entity_ref TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS predicate_schema (
          key TEXT PRIMARY KEY,
          display_name TEXT NOT NULL,
          allowed_subject_types TEXT NOT NULL,
          allowed_object_types TEXT NOT NULL,
          cardinality TEXT NOT NULL,
          inverse_key TEXT NULL,
          sensitivity_rule TEXT NULL,
          acyclic INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS knowledge_entity (
          id TEXT PRIMARY KEY,
          type TEXT NOT NULL,
          canonical_name TEXT NOT NULL,
          aliases TEXT NOT NULL,
          attributes_json TEXT NOT NULL,
          status TEXT NOT NULL,
          sensitivity TEXT NOT NULL,
          source_refs TEXT NOT NULL,
          merged_into_id TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_entity_name ON knowledge_entity(type, canonical_name, status);

        CREATE TABLE IF NOT EXISTS knowledge_relation (
          id TEXT PRIMARY KEY,
          subject_id TEXT NOT NULL,
          predicate TEXT NOT NULL,
          object_id TEXT NULL,
          literal_json TEXT NULL,
          qualifier_json TEXT NULL,
          confidence REAL NOT NULL,
          source_memory_ids TEXT NOT NULL,
          status TEXT NOT NULL,
          valid_from_utc TEXT NULL,
          valid_to_utc TEXT NULL,
          asserted_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_relation_subject
          ON knowledge_relation(subject_id, predicate, status);
        CREATE INDEX IF NOT EXISTS idx_relation_object ON knowledge_relation(object_id);

        CREATE TABLE IF NOT EXISTS entity_merge (
          id TEXT PRIMARY KEY,
          survivor_id TEXT NOT NULL,
          merged_id TEXT NOT NULL,
          actor TEXT NOT NULL,
          at_utc TEXT NOT NULL,
          reversed INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS memory (
          id TEXT PRIMARY KEY,
          kind TEXT NOT NULL,
          subject_ref TEXT NOT NULL,
          predicate TEXT NOT NULL,
          object_json TEXT NOT NULL,
          summary TEXT NOT NULL,
          source_refs TEXT NOT NULL,
          evidence_refs TEXT NOT NULL,
          confidence REAL NOT NULL,
          status TEXT NOT NULL,
          sensitivity TEXT NOT NULL,
          access_policy_id TEXT NOT NULL,
          valid_from_utc TEXT NULL,
          valid_to_utc TEXT NULL,
          retention_until_utc TEXT NULL,
          embedding_ref TEXT NULL,
          created_by TEXT NOT NULL,
          content_hash TEXT NOT NULL,
          anchors TEXT NOT NULL DEFAULT '[]'
        );

        CREATE INDEX IF NOT EXISTS idx_memory_subject ON memory(subject_ref, predicate, status);

        CREATE TABLE IF NOT EXISTS memory_revision (
          id TEXT PRIMARY KEY,
          memory_id TEXT NOT NULL,
          operation TEXT NOT NULL,
          actor TEXT NOT NULL,
          reason TEXT NOT NULL,
          prior_hash TEXT NULL,
          new_hash TEXT NOT NULL,
          at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_revision_memory ON memory_revision(memory_id, at_utc);

        CREATE TABLE IF NOT EXISTS mind_state_snapshot (
          id TEXT PRIMARY KEY,
          mind_id TEXT NOT NULL,
          schema_version INTEGER NOT NULL,
          captured_at_utc TEXT NOT NULL,
          consistency_cursor TEXT NOT NULL,
          audit_anchor_hash TEXT NULL,
          encryption_metadata TEXT NOT NULL,
          status TEXT NOT NULL,
          non_consistent_components TEXT NOT NULL,
          nonce BLOB NOT NULL,
          ciphertext BLOB NOT NULL,
          tag BLOB NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_snapshot_mind
          ON mind_state_snapshot(mind_id, captured_at_utc);

        CREATE TABLE IF NOT EXISTS recovery_plan (
          id TEXT PRIMARY KEY,
          snapshot_id TEXT NOT NULL,
          target_environment TEXT NOT NULL,
          steps TEXT NOT NULL,
          unresolved_tool_call_refs TEXT NOT NULL,
          reconciliation_policy TEXT NOT NULL,
          status TEXT NOT NULL,
          created_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS genome (
          id TEXT PRIMARY KEY,
          family TEXT NOT NULL,
          version TEXT NOT NULL,
          parent_genome_ref TEXT NULL,
          status TEXT NOT NULL,
          constitution_version TEXT NOT NULL,
          law_set_version TEXT NOT NULL,
          base_identity_template_ref TEXT NOT NULL,
          personality_baseline_ref TEXT NOT NULL,
          development_profile_ref TEXT NOT NULL,
          mind_schema_version INTEGER NOT NULL,
          allowed_capability_ids TEXT NOT NULL,
          policy_bundle_refs TEXT NOT NULL,
          default_locales TEXT NOT NULL,
          bootstrap_configuration_ref TEXT NOT NULL,
          integrity_hash TEXT NOT NULL,
          signature TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS genome_resolution (
          id TEXT PRIMARY KEY,
          genome_id TEXT NOT NULL,
          installation_id TEXT NOT NULL,
          selected_variants TEXT NOT NULL,
          effective_capability_ids TEXT NOT NULL,
          denied_overrides TEXT NOT NULL,
          effective_hash TEXT NOT NULL,
          resolved_at_utc TEXT NOT NULL,
          resolver TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS instance_lifecycle (
          instance_id TEXT PRIMARY KEY,
          state TEXT NOT NULL,
          entered_at_utc TEXT NOT NULL,
          reason TEXT NULL,
          active_cycle_refs TEXT NOT NULL,
          pending_action_refs TEXT NOT NULL,
          last_verified_snapshot_ref TEXT NULL,
          version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS lifecycle_proposal (
          proposal_id TEXT PRIMARY KEY,
          instance_id TEXT NOT NULL,
          target_state TEXT NOT NULL,
          reason TEXT NOT NULL,
          proposed_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS vault_item (
          id TEXT PRIMARY KEY,
          provider TEXT NOT NULL,
          locator TEXT NOT NULL,
          purpose TEXT NOT NULL,
          allowed_tool_ids TEXT NOT NULL,
          rotation_due_at_utc TEXT NULL,
          status TEXT NOT NULL,
          nonce BLOB NOT NULL,
          ciphertext BLOB NOT NULL,
          tag BLOB NOT NULL,
          created_at_utc TEXT NOT NULL,
          updated_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_vault_status ON vault_item(status);

        CREATE TABLE IF NOT EXISTS domain_event (
          sequence INTEGER PRIMARY KEY AUTOINCREMENT,
          event_id TEXT NOT NULL UNIQUE,
          type TEXT NOT NULL,
          schema_version INTEGER NOT NULL,
          producer TEXT NOT NULL,
          occurred_at_utc TEXT NOT NULL,
          correlation_id TEXT NOT NULL,
          causation_id TEXT NULL,
          aggregate_ref TEXT NULL,
          payload_json TEXT NULL,
          payload_ref TEXT NULL,
          sensitivity TEXT NOT NULL,
          idempotency_key TEXT NULL,
          integrity_hash TEXT NOT NULL,
          tenant_id TEXT NOT NULL DEFAULT 'tenant/local'
        );

        CREATE INDEX IF NOT EXISTS idx_event_type ON domain_event(type, sequence);

        CREATE TABLE IF NOT EXISTS subscription (
          id TEXT PRIMARY KEY,
          consumer TEXT NOT NULL,
          event_types TEXT NOT NULL,
          filter_ref TEXT NULL,
          mode TEXT NOT NULL,
          checkpoint INTEGER NOT NULL,
          status TEXT NOT NULL,
          max_attempts INTEGER NOT NULL,
          max_schema_version INTEGER NOT NULL,
          diagnosis TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS delivery (
          delivery_id TEXT PRIMARY KEY,
          event_id TEXT NOT NULL,
          subscription_id TEXT NOT NULL,
          attempt INTEGER NOT NULL,
          delivered_at_utc TEXT NULL,
          status TEXT NOT NULL,
          last_error TEXT NULL
        );

        -- Makes fan-out re-executable (RFC 050 rule 1): replaying publication after a crash
        -- cannot create a second delivery for the same (event, subscription).
        CREATE UNIQUE INDEX IF NOT EXISTS idx_delivery_unique
          ON delivery(event_id, subscription_id);

        CREATE INDEX IF NOT EXISTS idx_delivery_status ON delivery(status);

        CREATE TABLE IF NOT EXISTS consent_session (
          session_id TEXT PRIMARY KEY,
          principal_client_id TEXT NOT NULL,
          principal_os_user TEXT NOT NULL,
          server_boot_id TEXT NOT NULL,
          policy_version TEXT NOT NULL,
          status TEXT NOT NULL,
          actions_used INTEGER NOT NULL,
          max_actions INTEGER NOT NULL,
          created_at_utc TEXT NOT NULL,
          expires_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_session_live
          ON consent_session(principal_client_id, server_boot_id, policy_version, status);

        CREATE TABLE IF NOT EXISTS schedule (
          id TEXT PRIMARY KEY,
          owner_id TEXT NOT NULL,
          title TEXT NOT NULL,
          trigger_kind TEXT NOT NULL,
          timezone TEXT NOT NULL,
          expression TEXT NOT NULL,
          next_run_at_utc TEXT NULL,
          last_run_at_utc TEXT NULL,
          target TEXT NOT NULL,
          payload_ref TEXT NULL,
          approval_ref TEXT NULL,
          enabled INTEGER NOT NULL,
          quiet_hours_policy TEXT NOT NULL,
          missed_run_policy TEXT NOT NULL,
          status TEXT NOT NULL,
          disabled_reason TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_schedule_due
          ON schedule(status, enabled, next_run_at_utc);

        CREATE TABLE IF NOT EXISTS schedule_run (
          id TEXT PRIMARY KEY,
          schedule_id TEXT NOT NULL,
          due_at_utc TEXT NOT NULL,
          started_at_utc TEXT NULL,
          finished_at_utc TEXT NULL,
          status TEXT NOT NULL,
          cycle_id TEXT NULL,
          result_ref TEXT NULL,
          -- One row per occurrence, not per attempt. This is what makes the hour that repeats at
          -- the end of DST run once instead of twice (RFC 026).
          idempotency_key TEXT NOT NULL UNIQUE,
          FOREIGN KEY (schedule_id) REFERENCES schedule(id)
        );

        CREATE INDEX IF NOT EXISTS idx_schedule_run_schedule
          ON schedule_run(schedule_id, due_at_utc);

        CREATE TABLE IF NOT EXISTS signal (
          id TEXT PRIMARY KEY,
          source_event_ref TEXT NOT NULL,
          kind TEXT NOT NULL,
          severity TEXT NOT NULL,
          urgency REAL NOT NULL,
          relevance REAL NOT NULL,
          confidence REAL NOT NULL,
          target_refs TEXT NOT NULL,
          created_at_utc TEXT NOT NULL,
          expires_at_utc TEXT NOT NULL,
          interruptibility TEXT NOT NULL,
          status TEXT NOT NULL,
          reason_codes TEXT NOT NULL,
          policy_refs TEXT NOT NULL,
          dedupe_key TEXT NOT NULL,
          resolution_ref TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_signal_dedupe ON signal(dedupe_key, created_at_utc);
        CREATE INDEX IF NOT EXISTS idx_signal_open ON signal(status, expires_at_utc);

        CREATE TABLE IF NOT EXISTS need (
          id TEXT PRIMARY KEY,
          kind TEXT NOT NULL,
          subject_ref TEXT NOT NULL,
          intensity REAL NOT NULL,
          priority INTEGER NOT NULL,
          evidence_refs TEXT NOT NULL,
          satisfaction_condition TEXT NOT NULL,
          earliest_action_at_utc TEXT NULL,
          expires_at_utc TEXT NULL,
          recommended_goal_ref TEXT NULL,
          status TEXT NOT NULL,
          policy_constraints TEXT NOT NULL,
          owner TEXT NOT NULL,
          detected_at_utc TEXT NOT NULL,
          satisfied_evidence_ref TEXT NULL
        );

        -- One OPEN need per subject is the invariant, and it is kept by the upsert rather than by
        -- a unique key: the same subject legitimately produces many satisfied needs over time.
        CREATE INDEX IF NOT EXISTS idx_need_subject ON need(subject_ref, status);

        CREATE INDEX IF NOT EXISTS idx_need_open ON need(status, kind, priority);

        CREATE TABLE IF NOT EXISTS mission (
          id TEXT PRIMARY KEY,
          mind_id TEXT NOT NULL,
          title TEXT NOT NULL,
          purpose TEXT NOT NULL,
          success_definition TEXT NOT NULL,
          boundaries TEXT NOT NULL,
          priority_policy TEXT NOT NULL,
          owner TEXT NOT NULL,
          status TEXT NOT NULL,
          review_at_utc TEXT NULL,
          evidence_refs TEXT NOT NULL,
          created_at_utc TEXT NOT NULL,
          approval_ref TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_mission_owner ON mission(owner, status);

        CREATE TABLE IF NOT EXISTS curiosity_proposal (
          id TEXT PRIMARY KEY,
          question TEXT NOT NULL,
          rationale_refs TEXT NOT NULL,
          expected_value REAL NOT NULL,
          scope TEXT NOT NULL,
          allowed_sources TEXT NOT NULL,
          sensitivity_limit TEXT NOT NULL,
          resource_budget REAL NOT NULL,
          status TEXT NOT NULL,
          approval_required INTEGER NOT NULL,
          result_refs TEXT NOT NULL,
          review_at_utc TEXT NULL,
          detected_at_utc TEXT NOT NULL,
          refusal_reasons TEXT NOT NULL,
          goal_ref TEXT NULL,
          subject_ref TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_curiosity_open ON curiosity_proposal(status, review_at_utc);
        CREATE INDEX IF NOT EXISTS idx_curiosity_subject ON curiosity_proposal(subject_ref, status);

        CREATE TABLE IF NOT EXISTS deliberation (
          id TEXT PRIMARY KEY,
          cycle_id TEXT NOT NULL,
          phase TEXT NOT NULL,
          active_question TEXT NOT NULL,
          unresolved_questions TEXT NOT NULL,
          candidate_refs TEXT NOT NULL,
          assertions TEXT NOT NULL,
          uncertainty TEXT NOT NULL,
          next_step TEXT NULL,
          status TEXT NOT NULL,
          trace_ref TEXT NULL,
          retention_until_utc TEXT NOT NULL,
          started_at_utc TEXT NOT NULL,
          deadline_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_deliberation_cycle ON deliberation(cycle_id, status);

        -- Protected technical material (RFC 025 rule 4). Encrypted at rest, kept briefly, and
        -- deliberately unreadable through any interface: nothing returns a trace to a caller.
        CREATE TABLE IF NOT EXISTS deliberation_trace (
          trace_ref TEXT PRIMARY KEY,
          deliberation_id TEXT NOT NULL,
          nonce BLOB NOT NULL,
          ciphertext BLOB NOT NULL,
          tag BLOB NOT NULL,
          written_at_utc TEXT NOT NULL,
          retention_until_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_trace_retention ON deliberation_trace(retention_until_utc);

        CREATE TABLE IF NOT EXISTS thought (
          id TEXT PRIMARY KEY,
          cycle_id TEXT NOT NULL,
          deliberation_id TEXT NOT NULL,
          intent TEXT NOT NULL,
          objective_ref TEXT NULL,
          evidence_refs TEXT NOT NULL,
          assumptions TEXT NOT NULL,
          options TEXT NOT NULL,
          uncertainty TEXT NOT NULL,
          recommended_option TEXT NOT NULL,
          user_explanation TEXT NOT NULL,
          status TEXT NOT NULL,
          created_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_thought_cycle ON thought(cycle_id, created_at_utc);

        CREATE TABLE IF NOT EXISTS belief (
          id TEXT PRIMARY KEY,
          subject_ref TEXT NOT NULL,
          predicate TEXT NOT NULL,
          object_json TEXT NOT NULL,
          scope_json TEXT NOT NULL,
          confidence REAL NOT NULL,
          evidence_for_refs TEXT NOT NULL,
          evidence_against_refs TEXT NOT NULL,
          basis TEXT NOT NULL,
          status TEXT NOT NULL,
          valid_from_utc TEXT NOT NULL,
          review_at_utc TEXT NOT NULL,
          last_evaluated_at_utc TEXT NOT NULL,
          decision_impact TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_belief_subject ON belief(subject_ref, status);
        CREATE INDEX IF NOT EXISTS idx_belief_review ON belief(status, review_at_utc);

        -- Kept rather than folded into the belief: a prediction that failed is not erased, and the
        -- record of having believed something wrong is the useful part (RFC 028).
        CREATE TABLE IF NOT EXISTS belief_update (
          id TEXT PRIMARY KEY,
          belief_id TEXT NOT NULL,
          observation_ref TEXT NOT NULL,
          delta_confidence REAL NOT NULL,
          reason TEXT NOT NULL,
          applied_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_belief_update ON belief_update(belief_id, applied_at_utc);

        CREATE TABLE IF NOT EXISTS relationship_assertion (
          id TEXT PRIMARY KEY,
          subject_ref TEXT NOT NULL,
          relation_type TEXT NOT NULL,
          object_ref TEXT NOT NULL,
          qualifiers_json TEXT NOT NULL,
          authority_scope TEXT NOT NULL,
          confidence REAL NOT NULL,
          evidence_refs TEXT NOT NULL,
          valid_from_utc TEXT NOT NULL,
          valid_to_utc TEXT NULL,
          status TEXT NOT NULL,
          authorization_ref TEXT NULL,
          retention_until_utc TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_relationship_subject
          ON relationship_assertion(subject_ref, status, valid_from_utc);

        CREATE TABLE IF NOT EXISTS preference (
          id TEXT PRIMARY KEY,
          owner_ref TEXT NOT NULL,
          subject_ref TEXT NOT NULL,
          dimension TEXT NOT NULL,
          value_json TEXT NOT NULL,
          strength REAL NOT NULL,
          basis TEXT NOT NULL,
          evidence_refs TEXT NOT NULL,
          scope_json TEXT NOT NULL,
          status TEXT NOT NULL,
          review_at_utc TEXT NOT NULL,
          consent_required INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_preference_owner
          ON preference(owner_ref, dimension, status);

        CREATE TABLE IF NOT EXISTS self_model (
          id TEXT PRIMARY KEY,
          mind_id TEXT NOT NULL,
          version INTEGER NOT NULL,
          identity_ref TEXT NOT NULL,
          personality_ref TEXT NULL,
          capability_snapshot_json TEXT NOT NULL,
          resource_snapshot_json TEXT NOT NULL,
          operational_state TEXT NOT NULL,
          active_cycle_ids TEXT NOT NULL,
          current_focus_ref TEXT NULL,
          health_summary TEXT NOT NULL,
          health_observed_at_utc TEXT NOT NULL,
          recent_activity_refs TEXT NOT NULL,
          observed_at_utc TEXT NOT NULL,
          paused_reason TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_self_version ON self_model(mind_id, version DESC);

        CREATE TABLE IF NOT EXISTS personality_profile (
          id TEXT PRIMARY KEY,
          version INTEGER NOT NULL,
          name TEXT NOT NULL,
          languages TEXT NOT NULL,
          default_locale TEXT NOT NULL,
          voice_json TEXT NOT NULL,
          values_list TEXT NOT NULL,
          prohibited_claims TEXT NOT NULL,
          interaction_rules TEXT NOT NULL,
          disclosure_text TEXT NOT NULL,
          escalation_rules TEXT NOT NULL,
          active_from_utc TEXT NOT NULL,
          active_to_utc TEXT NULL,
          status TEXT NOT NULL,
          approval_ref TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_profile_status ON personality_profile(status, version DESC);

        CREATE TABLE IF NOT EXISTS communication_preference (
          owner_id TEXT NOT NULL,
          channel TEXT NOT NULL,
          language TEXT NOT NULL,
          verbosity REAL NOT NULL,
          quiet_hours TEXT NULL,
          accessibility_json TEXT NOT NULL,
          consent_for_proactivity INTEGER NOT NULL,
          updated_at_utc TEXT NOT NULL,
          PRIMARY KEY (owner_id, channel)
        );

        -- Rule 1: how the identity got to where it is, and who agreed to each step.
        CREATE TABLE IF NOT EXISTS identity_change (
          id TEXT PRIMARY KEY,
          profile_id TEXT NOT NULL,
          old_version INTEGER NOT NULL,
          new_version INTEGER NOT NULL,
          actor TEXT NOT NULL,
          reason TEXT NOT NULL,
          approved_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS development_state (
          mind_id TEXT PRIMARY KEY,
          current_stage_id TEXT NOT NULL,
          evidence_refs TEXT NOT NULL,
          assessment_at_utc TEXT NOT NULL,
          status TEXT NOT NULL,
          restricted_scopes TEXT NOT NULL,
          reason TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS development_proposal (
          id TEXT PRIMARY KEY,
          mind_id TEXT NOT NULL,
          from_stage_id TEXT NOT NULL,
          to_stage_id TEXT NOT NULL,
          evidence_refs TEXT NOT NULL,
          rationale TEXT NOT NULL,
          proposed_at_utc TEXT NOT NULL,
          status TEXT NOT NULL,
          approval_ref TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_development_proposal
          ON development_proposal(mind_id, proposed_at_utc);

        CREATE TABLE IF NOT EXISTS life_episode (
          id TEXT PRIMARY KEY,
          mind_id TEXT NOT NULL,
          kind TEXT NOT NULL,
          occurred_at_utc TEXT NOT NULL,
          occurred_until_utc TEXT NULL,
          title TEXT NOT NULL,
          narrative_summary TEXT NOT NULL,
          evidence_refs TEXT NOT NULL,
          significance TEXT NOT NULL,
          status TEXT NOT NULL,
          sensitivity_class TEXT NOT NULL,
          proposed_at_utc TEXT NOT NULL,
          verified_at_utc TEXT NULL,
          retracted_reason TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_episode_mind
          ON life_episode(mind_id, status, occurred_at_utc);

        -- Rule 3: the text is correctable and the evidence is not, so the trail records only what
        -- was actually allowed to change.
        CREATE TABLE IF NOT EXISTS episode_revision (
          id TEXT PRIMARY KEY,
          episode_id TEXT NOT NULL,
          previous_summary TEXT NOT NULL,
          new_summary TEXT NOT NULL,
          actor TEXT NOT NULL,
          reason TEXT NOT NULL,
          at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_episode_revision ON episode_revision(episode_id, at_utc);

        CREATE TABLE IF NOT EXISTS plugin_installation (
          id TEXT PRIMARY KEY,
          plugin_id TEXT NOT NULL UNIQUE,
          version TEXT NOT NULL,
          publisher TEXT NOT NULL,
          status TEXT NOT NULL,
          granted_permissions TEXT NOT NULL,
          manifest_json TEXT NOT NULL,
          installed_at_utc TEXT NOT NULL,
          updated_at_utc TEXT NOT NULL,
          consecutive_failures INTEGER NOT NULL,
          quarantine_reason TEXT NULL,
          approval_ref TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_plugin_status ON plugin_installation(status);

        CREATE TABLE IF NOT EXISTS remembered_note (
          note_id TEXT PRIMARY KEY,
          principal_client_id TEXT NOT NULL,
          note TEXT NOT NULL,
          created_at_utc TEXT NOT NULL
        );
        """;

    /// <summary>Schema this build expects. Bump it and add a migration in the same commit.</summary>
    public const int TargetSchemaVersion = 16;

    /// <summary>
    /// Migrations from the version keyed here minus one, up to it. Applied in order, only to a
    /// database that predates them; a database created fresh already matches the DDL above and is
    /// stamped at <see cref="TargetSchemaVersion"/> without running any of them.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> Migrations = new Dictionary<int, string>
    {
        // v2 — the principal's OS user is not Windows-specific (docs/adr/0015).
        [2] = """
            ALTER TABLE audit_record RENAME COLUMN principal_windows_user TO principal_os_user;
            ALTER TABLE approval RENAME COLUMN principal_windows_user TO principal_os_user;
            ALTER TABLE consent_session RENAME COLUMN principal_windows_user TO principal_os_user;
            """,

        // v3 — the Scheduler (docs/adr/0032). New tables only, so an existing database gains them
        // without touching a row it already holds.
        [3] = """
            CREATE TABLE IF NOT EXISTS schedule (
              id TEXT PRIMARY KEY,
              owner_id TEXT NOT NULL,
              title TEXT NOT NULL,
              trigger_kind TEXT NOT NULL,
              timezone TEXT NOT NULL,
              expression TEXT NOT NULL,
              next_run_at_utc TEXT NULL,
              last_run_at_utc TEXT NULL,
              target TEXT NOT NULL,
              payload_ref TEXT NULL,
              approval_ref TEXT NULL,
              enabled INTEGER NOT NULL,
              quiet_hours_policy TEXT NOT NULL,
              missed_run_policy TEXT NOT NULL,
              status TEXT NOT NULL,
              disabled_reason TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_schedule_due
              ON schedule(status, enabled, next_run_at_utc);

            CREATE TABLE IF NOT EXISTS schedule_run (
              id TEXT PRIMARY KEY,
              schedule_id TEXT NOT NULL,
              due_at_utc TEXT NOT NULL,
              started_at_utc TEXT NULL,
              finished_at_utc TEXT NULL,
              status TEXT NOT NULL,
              cycle_id TEXT NULL,
              result_ref TEXT NULL,
              idempotency_key TEXT NOT NULL UNIQUE,
              FOREIGN KEY (schedule_id) REFERENCES schedule(id)
            );

            CREATE INDEX IF NOT EXISTS idx_schedule_run_schedule
              ON schedule_run(schedule_id, due_at_utc);
            """,

        // v4 — Signals and Needs (docs/adr/0033). New tables only.
        [4] = """
            CREATE TABLE IF NOT EXISTS signal (
              id TEXT PRIMARY KEY,
              source_event_ref TEXT NOT NULL,
              kind TEXT NOT NULL,
              severity TEXT NOT NULL,
              urgency REAL NOT NULL,
              relevance REAL NOT NULL,
              confidence REAL NOT NULL,
              target_refs TEXT NOT NULL,
              created_at_utc TEXT NOT NULL,
              expires_at_utc TEXT NOT NULL,
              interruptibility TEXT NOT NULL,
              status TEXT NOT NULL,
              reason_codes TEXT NOT NULL,
              policy_refs TEXT NOT NULL,
              dedupe_key TEXT NOT NULL,
              resolution_ref TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_signal_dedupe ON signal(dedupe_key, created_at_utc);
            CREATE INDEX IF NOT EXISTS idx_signal_open ON signal(status, expires_at_utc);

            CREATE TABLE IF NOT EXISTS need (
              id TEXT PRIMARY KEY,
              kind TEXT NOT NULL,
              subject_ref TEXT NOT NULL,
              intensity REAL NOT NULL,
              priority INTEGER NOT NULL,
              evidence_refs TEXT NOT NULL,
              satisfaction_condition TEXT NOT NULL,
              earliest_action_at_utc TEXT NULL,
              expires_at_utc TEXT NULL,
              recommended_goal_ref TEXT NULL,
              status TEXT NOT NULL,
              policy_constraints TEXT NOT NULL,
              owner TEXT NOT NULL,
              detected_at_utc TEXT NOT NULL,
              satisfied_evidence_ref TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_need_subject ON need(subject_ref, status);

            CREATE INDEX IF NOT EXISTS idx_need_open ON need(status, kind, priority);
            """,

        // v5 — Missions, and the two columns on goal that carry RFC 052 rule 2
        // (docs/adr/0035). ALTER ... ADD COLUMN is the one shape SQLite does cheaply and safely.
        [5] = """
            CREATE TABLE IF NOT EXISTS mission (
              id TEXT PRIMARY KEY,
              mind_id TEXT NOT NULL,
              title TEXT NOT NULL,
              purpose TEXT NOT NULL,
              success_definition TEXT NOT NULL,
              boundaries TEXT NOT NULL,
              priority_policy TEXT NOT NULL,
              owner TEXT NOT NULL,
              status TEXT NOT NULL,
              review_at_utc TEXT NULL,
              evidence_refs TEXT NOT NULL,
              created_at_utc TEXT NOT NULL,
              approval_ref TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_mission_owner ON mission(owner, status);
            """,

        // v6 — governed curiosity (docs/adr/0036). New table only.
        [6] = """
            CREATE TABLE IF NOT EXISTS curiosity_proposal (
              id TEXT PRIMARY KEY,
              question TEXT NOT NULL,
              rationale_refs TEXT NOT NULL,
              expected_value REAL NOT NULL,
              scope TEXT NOT NULL,
              allowed_sources TEXT NOT NULL,
              sensitivity_limit TEXT NOT NULL,
              resource_budget REAL NOT NULL,
              status TEXT NOT NULL,
              approval_required INTEGER NOT NULL,
              result_refs TEXT NOT NULL,
              review_at_utc TEXT NULL,
              detected_at_utc TEXT NOT NULL,
              refusal_reasons TEXT NOT NULL,
              goal_ref TEXT NULL,
              subject_ref TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_curiosity_open ON curiosity_proposal(status, review_at_utc);
            CREATE INDEX IF NOT EXISTS idx_curiosity_subject ON curiosity_proposal(subject_ref, status);
            """,

        // v7 — internal deliberation (docs/adr/0040). New tables only.
        [7] = """
            CREATE TABLE IF NOT EXISTS deliberation (
              id TEXT PRIMARY KEY,
              cycle_id TEXT NOT NULL,
              phase TEXT NOT NULL,
              active_question TEXT NOT NULL,
              unresolved_questions TEXT NOT NULL,
              candidate_refs TEXT NOT NULL,
              assertions TEXT NOT NULL,
              uncertainty TEXT NOT NULL,
              next_step TEXT NULL,
              status TEXT NOT NULL,
              trace_ref TEXT NULL,
              retention_until_utc TEXT NOT NULL,
              started_at_utc TEXT NOT NULL,
              deadline_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_deliberation_cycle ON deliberation(cycle_id, status);

            CREATE TABLE IF NOT EXISTS deliberation_trace (
              trace_ref TEXT PRIMARY KEY,
              deliberation_id TEXT NOT NULL,
              nonce BLOB NOT NULL,
              ciphertext BLOB NOT NULL,
              tag BLOB NOT NULL,
              written_at_utc TEXT NOT NULL,
              retention_until_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_trace_retention ON deliberation_trace(retention_until_utc);

            CREATE TABLE IF NOT EXISTS thought (
              id TEXT PRIMARY KEY,
              cycle_id TEXT NOT NULL,
              deliberation_id TEXT NOT NULL,
              intent TEXT NOT NULL,
              objective_ref TEXT NULL,
              evidence_refs TEXT NOT NULL,
              assumptions TEXT NOT NULL,
              options TEXT NOT NULL,
              uncertainty TEXT NOT NULL,
              recommended_option TEXT NOT NULL,
              user_explanation TEXT NOT NULL,
              status TEXT NOT NULL,
              created_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_thought_cycle ON thought(cycle_id, created_at_utc);
            """,

        // v8 — the belief system (docs/adr/0041). New tables only.
        [8] = """
            CREATE TABLE IF NOT EXISTS belief (
              id TEXT PRIMARY KEY,
              subject_ref TEXT NOT NULL,
              predicate TEXT NOT NULL,
              object_json TEXT NOT NULL,
              scope_json TEXT NOT NULL,
              confidence REAL NOT NULL,
              evidence_for_refs TEXT NOT NULL,
              evidence_against_refs TEXT NOT NULL,
              basis TEXT NOT NULL,
              status TEXT NOT NULL,
              valid_from_utc TEXT NOT NULL,
              review_at_utc TEXT NOT NULL,
              last_evaluated_at_utc TEXT NOT NULL,
              decision_impact TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_belief_subject ON belief(subject_ref, status);
            CREATE INDEX IF NOT EXISTS idx_belief_review ON belief(status, review_at_utc);

            CREATE TABLE IF NOT EXISTS belief_update (
              id TEXT PRIMARY KEY,
              belief_id TEXT NOT NULL,
              observation_ref TEXT NOT NULL,
              delta_confidence REAL NOT NULL,
              reason TEXT NOT NULL,
              applied_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_belief_update ON belief_update(belief_id, applied_at_utc);
            """,

        // v9 — relationships and preferences (docs/adr/0042). New tables only.
        [9] = """
            CREATE TABLE IF NOT EXISTS relationship_assertion (
              id TEXT PRIMARY KEY,
              subject_ref TEXT NOT NULL,
              relation_type TEXT NOT NULL,
              object_ref TEXT NOT NULL,
              qualifiers_json TEXT NOT NULL,
              authority_scope TEXT NOT NULL,
              confidence REAL NOT NULL,
              evidence_refs TEXT NOT NULL,
              valid_from_utc TEXT NOT NULL,
              valid_to_utc TEXT NULL,
              status TEXT NOT NULL,
              authorization_ref TEXT NULL,
              retention_until_utc TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_relationship_subject
              ON relationship_assertion(subject_ref, status, valid_from_utc);

            CREATE TABLE IF NOT EXISTS preference (
              id TEXT PRIMARY KEY,
              owner_ref TEXT NOT NULL,
              subject_ref TEXT NOT NULL,
              dimension TEXT NOT NULL,
              value_json TEXT NOT NULL,
              strength REAL NOT NULL,
              basis TEXT NOT NULL,
              evidence_refs TEXT NOT NULL,
              scope_json TEXT NOT NULL,
              status TEXT NOT NULL,
              review_at_utc TEXT NOT NULL,
              consent_required INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_preference_owner
              ON preference(owner_ref, dimension, status);
            """,

        // v10 — the self model (docs/adr/0043). New table only.
        [10] = """
            CREATE TABLE IF NOT EXISTS self_model (
              id TEXT PRIMARY KEY,
              mind_id TEXT NOT NULL,
              version INTEGER NOT NULL,
              identity_ref TEXT NOT NULL,
              personality_ref TEXT NULL,
              capability_snapshot_json TEXT NOT NULL,
              resource_snapshot_json TEXT NOT NULL,
              operational_state TEXT NOT NULL,
              active_cycle_ids TEXT NOT NULL,
              current_focus_ref TEXT NULL,
              health_summary TEXT NOT NULL,
              health_observed_at_utc TEXT NOT NULL,
              recent_activity_refs TEXT NOT NULL,
              observed_at_utc TEXT NOT NULL,
              paused_reason TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_self_version ON self_model(mind_id, version DESC);
            """,

        // v11 — communication identity (docs/adr/0044). New tables only.
        [11] = """
            CREATE TABLE IF NOT EXISTS personality_profile (
              id TEXT PRIMARY KEY,
              version INTEGER NOT NULL,
              name TEXT NOT NULL,
              languages TEXT NOT NULL,
              default_locale TEXT NOT NULL,
              voice_json TEXT NOT NULL,
              values_list TEXT NOT NULL,
              prohibited_claims TEXT NOT NULL,
              interaction_rules TEXT NOT NULL,
              disclosure_text TEXT NOT NULL,
              escalation_rules TEXT NOT NULL,
              active_from_utc TEXT NOT NULL,
              active_to_utc TEXT NULL,
              status TEXT NOT NULL,
              approval_ref TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_profile_status ON personality_profile(status, version DESC);

            CREATE TABLE IF NOT EXISTS communication_preference (
              owner_id TEXT NOT NULL,
              channel TEXT NOT NULL,
              language TEXT NOT NULL,
              verbosity REAL NOT NULL,
              quiet_hours TEXT NULL,
              accessibility_json TEXT NOT NULL,
              consent_for_proactivity INTEGER NOT NULL,
              updated_at_utc TEXT NOT NULL,
              PRIMARY KEY (owner_id, channel)
            );

            CREATE TABLE IF NOT EXISTS identity_change (
              id TEXT PRIMARY KEY,
              profile_id TEXT NOT NULL,
              old_version INTEGER NOT NULL,
              new_version INTEGER NOT NULL,
              actor TEXT NOT NULL,
              reason TEXT NOT NULL,
              approved_at_utc TEXT NOT NULL
            );
            """,

        // v12 — the development model (docs/adr/0046). New tables only.
        [12] = """
            CREATE TABLE IF NOT EXISTS development_state (
              mind_id TEXT PRIMARY KEY,
              current_stage_id TEXT NOT NULL,
              evidence_refs TEXT NOT NULL,
              assessment_at_utc TEXT NOT NULL,
              status TEXT NOT NULL,
              restricted_scopes TEXT NOT NULL,
              reason TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS development_proposal (
              id TEXT PRIMARY KEY,
              mind_id TEXT NOT NULL,
              from_stage_id TEXT NOT NULL,
              to_stage_id TEXT NOT NULL,
              evidence_refs TEXT NOT NULL,
              rationale TEXT NOT NULL,
              proposed_at_utc TEXT NOT NULL,
              status TEXT NOT NULL,
              approval_ref TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_development_proposal
              ON development_proposal(mind_id, proposed_at_utc);
            """,

        // v13 — the life history (docs/adr/0047). New tables only.
        [13] = """
            CREATE TABLE IF NOT EXISTS life_episode (
              id TEXT PRIMARY KEY,
              mind_id TEXT NOT NULL,
              kind TEXT NOT NULL,
              occurred_at_utc TEXT NOT NULL,
              occurred_until_utc TEXT NULL,
              title TEXT NOT NULL,
              narrative_summary TEXT NOT NULL,
              evidence_refs TEXT NOT NULL,
              significance TEXT NOT NULL,
              status TEXT NOT NULL,
              sensitivity_class TEXT NOT NULL,
              proposed_at_utc TEXT NOT NULL,
              verified_at_utc TEXT NULL,
              retracted_reason TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_episode_mind
              ON life_episode(mind_id, status, occurred_at_utc);

            CREATE TABLE IF NOT EXISTS episode_revision (
              id TEXT PRIMARY KEY,
              episode_id TEXT NOT NULL,
              previous_summary TEXT NOT NULL,
              new_summary TEXT NOT NULL,
              actor TEXT NOT NULL,
              reason TEXT NOT NULL,
              at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_episode_revision ON episode_revision(episode_id, at_utc);
            """,

        // v14 — the plugin registry (docs/adr/0048). New table only.
        [14] = """
            CREATE TABLE IF NOT EXISTS plugin_installation (
              id TEXT PRIMARY KEY,
              plugin_id TEXT NOT NULL UNIQUE,
              version TEXT NOT NULL,
              publisher TEXT NOT NULL,
              status TEXT NOT NULL,
              granted_permissions TEXT NOT NULL,
              manifest_json TEXT NOT NULL,
              installed_at_utc TEXT NOT NULL,
              updated_at_utc TEXT NOT NULL,
              consecutive_failures INTEGER NOT NULL,
              quarantine_reason TEXT NULL,
              approval_ref TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_plugin_status ON plugin_installation(status);
            """,
    };

    /// <summary>
    /// Columns added to tables that already existed, as (table, column, declaration).
    /// </summary>
    /// <remarks>
    /// Adding a column cannot live in <see cref="Migrations"/>. The DDL above runs first and its
    /// <c>CREATE TABLE IF NOT EXISTS</c> already produces the target shape, so on a database where
    /// that table did not exist the migration's <c>ALTER ... ADD COLUMN</c> would duplicate a column
    /// the DDL just created. Checked against the live schema instead, which is correct either way
    /// and idempotent by construction — SQLite has no <c>ADD COLUMN IF NOT EXISTS</c>.
    /// </remarks>
    private static readonly (string Table, string Column, string Declaration)[] RequiredColumns =
    [
        // v5 — a goal records the mission it serves, or when it must be looked at again (RFC 052).
        ("goal", "mission_ref", "TEXT NULL"),
        ("goal", "ad_hoc_review_at_utc", "TEXT NULL"),

        // LAW-005 — state crossing a component boundary says who owns it (docs/adr/0050).
        ("domain_event", "tenant_id", "TEXT NOT NULL DEFAULT 'tenant/local'"),

        // v15 — RFC 08 names these on LearningProposal and the table never held them; without
        // risk there is no way to tell a low-risk memory change from anything else (docs/adr/0055).
        ("learning_proposal", "expected_benefit", "TEXT NOT NULL DEFAULT ''"),
        ("learning_proposal", "risk", "TEXT NOT NULL DEFAULT 'HIGH'"),
        ("learning_proposal", "evidence_refs", "TEXT NOT NULL DEFAULT ''"),

        // v16 — RFC 035 rule 2: a high-risk decision names the assessment it was committed
        // against. Null on decisions taken before there was one (docs/adr/0057).
        ("decision", "constitutional_assessment_ref", "TEXT NULL"),
    ];

    private readonly SqliteConnectionFactory _factory;

    public SqliteDatabase(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>Creates the schema if it does not yet exist. Idempotent.</summary>
    public void Initialize()
    {
        using var connection = _factory.Open();

        // journal_mode cannot be changed inside a transaction; set it first. WAL persists on the file.
        using (var walCommand = connection.CreateCommand())
        {
            walCommand.CommandText = "PRAGMA journal_mode=WAL;";
            walCommand.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        using (var ddlCommand = connection.CreateCommand())
        {
            ddlCommand.Transaction = transaction;
            ddlCommand.CommandText = SchemaDdl;
            ddlCommand.ExecuteNonQuery();
        }

        Migrate(connection, transaction);
        EnsureColumns(connection, transaction);

        transaction.Commit();
    }

    /// <summary>
    /// Brings an existing database up to <see cref="TargetSchemaVersion"/>. Runs inside the same
    /// transaction as the DDL, so a half-applied migration cannot survive a crash.
    /// </summary>
    private static void Migrate(SqliteConnection connection, SqliteTransaction transaction)
    {
        int current;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT version FROM schema_version LIMIT 1;";
            var value = read.ExecuteScalar();

            if (value is null)
            {
                // Nothing recorded: the DDL above just created this database at the current shape.
                using var seed = connection.CreateCommand();
                seed.Transaction = transaction;
                seed.CommandText = "INSERT INTO schema_version (version) VALUES (@v);";
                seed.Parameters.AddWithValue("@v", TargetSchemaVersion);
                seed.ExecuteNonQuery();
                return;
            }

            current = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        for (var version = current + 1; version <= TargetSchemaVersion; version++)
        {
            if (!Migrations.TryGetValue(version, out var sql))
            {
                continue;
            }

            using var apply = connection.CreateCommand();
            apply.Transaction = transaction;
            apply.CommandText = sql;
            apply.ExecuteNonQuery();
        }

        if (current < TargetSchemaVersion)
        {
            using var stamp = connection.CreateCommand();
            stamp.Transaction = transaction;
            stamp.CommandText = "UPDATE schema_version SET version = @v;";
            stamp.Parameters.AddWithValue("@v", TargetSchemaVersion);
            stamp.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Adds any column in <see cref="RequiredColumns"/> that this database does not yet have.
    /// </summary>
    private static void EnsureColumns(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach ((var table, var column, var declaration) in RequiredColumns)
        {
            if (HasColumn(connection, transaction, table, column))
            {
                continue;
            }

            using var add = connection.CreateCommand();
            add.Transaction = transaction;
            add.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
            add.ExecuteNonQuery();
        }
    }

    private static bool HasColumn(
        SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @c;";
        read.Parameters.AddWithValue("@c", column);

        return Convert.ToInt64(read.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }
}
