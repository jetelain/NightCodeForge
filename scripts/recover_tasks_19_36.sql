-- Step 11.41 Live Instance Recovery Script for Task #19 and Task #36
-- Database: C:\Users\Julien\AppData\Local\NightCodeForge\data\nightcodeforge.db

BEGIN TRANSACTION;

-- 1. Reset Tasks to Open (Status = 2) and set stage tracking to task-review
UPDATE Tasks
SET Status = 2,
    CurrentStageId = 'task-review',
    InterruptedStageId = 'task-review',
    IsWaitingForHuman = 0,
    IsDeferredByPause = 0
WHERE Id IN (19, 36);

-- 2. Transition the latest conversations back to Interrupted (Status = 8) and clear CompletedAt
UPDATE Conversations
SET Status = 8,
    CompletedAt = NULL,
    Summary = 'Awaiting task specification document update (attempt 1/3).'
WHERE Id IN (412, 413);

-- 3. Inject the mandatory conversational remediation prompt as a user turn
-- Task 19 (Conversation #412): TurnIndex 167
INSERT INTO ConversationTurns (ConversationId, TurnIndex, Role, Content, IsError, Timestamp)
VALUES (
    412,
    167,
    0,
    '[System Notice: Mandatory Task Specification Document Update Required]
Your investigation turn concluded without modifying the task specification document on disk in docs/tasks/.

As a Staff Software Architect, you are required to produce an updated task specification before this stage can complete.
1. EDIT THE SPECIFICATION: Use the ''edit'' or ''write'' tool to update the specification document with your approved technical architecture, affected files and classes, implementation plan, and testable acceptance criteria.
2. PRESERVE FRONTMATTER: Keep the top YAML frontmatter block intact (do not modify id or status fields).
3. CLARIFICATION QUESTIONS: If you have blocking questions or product ambiguities for the maintainer, you MUST invoke the ''ask'' tool with your structured questions. Plain text questions in conversational chat are not received by the maintainer and cannot be answered.

Please update the task specification document on disk or invoke ''ask'' now to proceed.',
    0,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);

-- Task 36 (Conversation #413): TurnIndex 161
INSERT INTO ConversationTurns (ConversationId, TurnIndex, Role, Content, IsError, Timestamp)
VALUES (
    413,
    161,
    0,
    '[System Notice: Mandatory Task Specification Document Update Required]
Your investigation turn concluded without modifying the task specification document on disk in docs/tasks/.

As a Staff Software Architect, you are required to produce an updated task specification before this stage can complete.
1. EDIT THE SPECIFICATION: Use the ''edit'' or ''write'' tool to update the specification document with your approved technical architecture, affected files and classes, implementation plan, and testable acceptance criteria.
2. PRESERVE FRONTMATTER: Keep the top YAML frontmatter block intact (do not modify id or status fields).
3. CLARIFICATION QUESTIONS: If you have blocking questions or product ambiguities for the maintainer, you MUST invoke the ''ask'' tool with your structured questions. Plain text questions in conversational chat are not received by the maintainer and cannot be answered.

Please update the task specification document on disk or invoke ''ask'' now to proceed.',
    0,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);

COMMIT;

