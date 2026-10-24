-- ============================================================================
-- NightCodeForge Database Data Migration Script
-- Script: 20261024_backfill_stage_queue_tiers.sql
-- Description: Backfill StageDefinitions.QueueTier for pre-existing stages/steps
--              ensuring all existing workflow stages have canonical queue tiers.
--
-- Queue Tiers:
--   Tier 1 (TaskReview = 1): TaskReview, Triage, Ideation
--   Tier 2 (Implementation = 2): PreImplementationBaseline, Implementation,
--                                PostImplementationGates, Merge
--   Tier 3 (CodeReview = 3): CodeReviewAndFix, PostReviewVerification
-- ============================================================================

-- 1. Tier 1: Task Review & Requirements Investigation
UPDATE StageDefinitions 
SET QueueTier = 1 
WHERE StageId IN ('task-review', 'triage', 'ideation') 
  AND QueueTier != 1;

-- 2. Tier 2: Implementation & Deterministic Quality Gates
UPDATE StageDefinitions 
SET QueueTier = 2 
WHERE StageId IN ('deterministic-pre-impl', 'implementation', 'deterministic-post-impl', 'merge') 
  AND QueueTier != 2;

-- 3. Tier 3: Code Review & Final Verification Gates
UPDATE StageDefinitions 
SET QueueTier = 3 
WHERE StageId IN ('code-review-fix', 'deterministic-post-review') 
  AND QueueTier != 3;
