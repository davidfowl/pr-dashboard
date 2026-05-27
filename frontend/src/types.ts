export type AppInfoResponse = {
  commitSha: string;
  shortCommitSha: string;
  commitUrl?: string;
};

export type AuthStatus = {
  authenticated: boolean;
  configured: boolean;
  canLogin: boolean;
  source?: string;
  login?: string;
  message: string;
};

export type PullState = 'open' | 'closed' | 'all';

export type PullRequestSummary = {
  repository: string;
  number: number;
  title: string;
  state: string;
  draft: boolean;
  author: string;
  htmlUrl: string;
  createdAt: string;
  updatedAt: string;
  labels: string[];
  requestedReviewers: string[];
  milestone?: string;
  linkedIssues: LinkedIssueSummary[];
  commitCount: number;
  additions: number;
  deletions: number;
  changedFiles: number;
  lastCommitAt?: string | null;
  headSha?: string | null;
  review: ReviewStatus;
  checks: ChecksStatus;
};

export type LinkedIssueSummary = {
  repository: string;
  number: number;
  title: string;
  milestone?: string;
  labels: string[];
  htmlUrl: string;
};

export type ReviewStatus = {
  state: 'waiting' | 'reviewed' | 'approved' | 'changes_requested';
  latestState?: string | null;
  reviewerCount: number;
  approvalCount: number;
  changesRequestedCount: number;
  commentedReviewCount: number;
  lastApprovedAt?: string | null;
  lastReviewedAt?: string | null;
};

export type CheckState = 'unknown' | 'success' | 'failure' | 'pending' | 'none';

export type FailingCheck = {
  name: string;
  conclusion?: string | null;
  htmlUrl?: string | null;
};

export type ChecksStatus = {
  state: CheckState;
  totalCount: number;
  successCount: number;
  failureCount: number;
  pendingCount: number;
  neutralCount: number;
  skippedCount: number;
  completedAt?: string | null;
  failingChecks: FailingCheck[];
};

export type PullRequestListResponse = {
  repository: string;
  pullRequests: Omit<PullRequestSummary, 'repository'>[];
};

export type PullRequestChecksRequest = {
  pullRequests: {
    number: number;
    headSha: string;
  }[];
};

export type PullRequestChecksResponse = {
  repository: string;
  pullRequests: {
    number: number;
    headSha: string;
    checks: ChecksStatus;
  }[];
};

export type TimelineItem = {
  id: string;
  event: string;
  actor: string;
  occurredAt: string;
  state?: string;
  summary: string;
  body?: string;
  htmlUrl?: string;
};

export type TimelineStats = {
  commitCount: number;
  humanCommenterCount: number;
  humanCommentCount: number;
  reviewCount: number;
  approvalCount: number;
  firstHumanCommentDelayMs?: number;
  firstReviewDelayMs?: number;
  firstApprovalDelayMs?: number;
  approvalToMergeDelayMs?: number;
  createdToMergeDelayMs?: number;
  averageHumanCommentGapMs?: number;
  longestHumanCommentGapMs?: number;
  mergedAt?: string;
  developers: DeveloperStats[];
};

export type DeveloperStats = {
  actor: string;
  activityCount: number;
  commitCount: number;
  commentCount: number;
  reviewCount: number;
  approvalCount: number;
  changesRequestedCount: number;
  firstActivityAt: string;
  lastActivityAt: string;
};

export type MergeableState =
  | 'clean'
  | 'dirty'
  | 'blocked'
  | 'behind'
  | 'unstable'
  | 'has_hooks'
  | 'unknown'
  | 'draft';

export type TimelineResponse = {
  repository: string;
  number: number;
  stats: TimelineStats;
  checks: ChecksStatus;
  mergeableState?: MergeableState | null;
  items: TimelineItem[];
};

export type DeveloperPullRequestCount = {
  actor: string;
  openPullRequestCount: number;
  repositories: string[];
  latestUpdatedAt?: string;
};

export type AttentionItem = {
  pullRequest: PullRequestSummary;
  reason: string;
};

export type AttentionBucket = {
  label: string;
  summary: string;
  tone: 'success' | 'warning' | 'danger' | 'accent';
  metric: string;
  items: AttentionItem[];
};

export type AttentionSignal = {
  label: string;
  tone?: 'success' | 'warning' | 'danger' | 'accent' | 'muted';
};

export type PickItem = {
  pullRequest: PullRequestSummary;
  action: string;
  reason: string;
  tone: 'success' | 'warning' | 'danger' | 'accent';
  personal: boolean;
};

export type ActivityMarker = {
  id: string;
  left: number;
  label: string;
  title: string;
  tone: 'commit' | 'review' | 'approval' | 'changes' | 'comment' | 'bot' | 'event';
};

export type ActivityGap = {
  id: string;
  left: number;
  width: number;
  label: string;
};

export type ActivityModel = {
  markers: ActivityMarker[];
  gaps: ActivityGap[];
  signals: AttentionSignal[];
  startLabel: string;
  endLabel: string;
};

export type TriageModel = {
  action: AttentionSignal;
  why: string;
  waitingOn: string;
  signals: AttentionSignal[];
  participants: TriageParticipant[];
  milestones: SignalMilestone[];
};

export type TriageParticipant = {
  actor: string;
  role: string;
  summary: string;
};

export type SignalMilestone = {
  id: string;
  occurredAt: string;
  event: string;
  title: string;
  detail?: string;
  tone: 'success' | 'warning' | 'danger' | 'accent' | 'muted';
  url?: string;
};

export type TimelineStoryEntry =
  | {
    kind: 'event';
    id: string;
    occurredAt: string;
    event: string;
    item: TimelineItem;
  }
  | {
    kind: 'summary';
    id: string;
    occurredAt: string;
    event: string;
    summary: string;
    detail: string;
    count: number;
  };
