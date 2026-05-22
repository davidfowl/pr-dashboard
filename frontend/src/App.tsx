import { useEffect, useMemo, useState } from 'react';
import type { CSSProperties, FormEvent } from 'react';
import './App.css';

type AuthStatus = {
  authenticated: boolean;
  configured: boolean;
  canLogin: boolean;
  source?: string;
  login?: string;
  message: string;
};

type DeviceLoginResponse = {
  status: 'idle' | 'pending' | 'slow_down' | 'authorized' | 'expired' | 'access_denied' | 'error';
  userCode?: string;
  verificationUri?: string;
  verificationUriComplete?: string;
  intervalSeconds: number;
  expiresAt?: string;
  message: string;
};

type PullRequestSummary = {
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
  review: ReviewStatus;
};

type ReviewStatus = {
  state: 'waiting' | 'reviewed' | 'approved' | 'changes_requested';
  latestState?: string;
  reviewerCount: number;
  approvalCount: number;
  changesRequestedCount: number;
  commentedReviewCount: number;
  lastReviewedAt?: string;
};

type PullRequestListResponse = {
  repository: string;
  pullRequests: Omit<PullRequestSummary, 'repository'>[];
};

type TimelineItem = {
  id: string;
  event: string;
  actor: string;
  occurredAt: string;
  state?: string;
  summary: string;
  body?: string;
  htmlUrl?: string;
};

type TimelineStats = {
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

type DeveloperStats = {
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

type TimelineResponse = {
  repository: string;
  number: number;
  stats: TimelineStats;
  items: TimelineItem[];
};

type AttentionItem = {
  pullRequest: PullRequestSummary;
  reason: string;
};

type AttentionBucket = {
  label: string;
  summary: string;
  tone: 'success' | 'warning' | 'danger' | 'accent';
  metric: string;
  items: AttentionItem[];
};

type AttentionSignal = {
  label: string;
  tone?: 'success' | 'warning' | 'danger' | 'accent' | 'muted';
};

type PickItem = {
  pullRequest: PullRequestSummary;
  action: string;
  reason: string;
  tone: 'success' | 'warning' | 'danger' | 'accent';
  personal: boolean;
};

type ActivityMarker = {
  id: string;
  left: number;
  label: string;
  title: string;
  tone: 'commit' | 'review' | 'approval' | 'changes' | 'comment' | 'bot' | 'event';
};

type ActivityGap = {
  id: string;
  left: number;
  width: number;
  label: string;
};

type ActivityModel = {
  markers: ActivityMarker[];
  gaps: ActivityGap[];
  signals: AttentionSignal[];
  startLabel: string;
  endLabel: string;
};

type TriageModel = {
  action: AttentionSignal;
  why: string;
  waitingOn: string;
  signals: AttentionSignal[];
  participants: TriageParticipant[];
  milestones: SignalMilestone[];
};

type TriageParticipant = {
  actor: string;
  role: string;
  summary: string;
};

type SignalMilestone = {
  id: string;
  occurredAt: string;
  event: string;
  title: string;
  detail?: string;
  tone: 'success' | 'warning' | 'danger' | 'accent' | 'muted';
  url?: string;
};

type TimelineStoryEntry =
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

const defaultRepos = ['microsoft/aspire', 'microsoft/aspire.dev', 'microsoft/dcp'];
const defaultRepoInput = defaultRepos.join(', ');
const hourMs = 1000 * 60 * 60;
const dayMs = hourMs * 24;

function App() {
  const [repo, setRepo] = useState(defaultRepoInput);
  const [activeRepo, setActiveRepo] = useState(defaultRepos[0]);
  const [state, setState] = useState<'open' | 'closed' | 'all'>('open');
  const [authStatus, setAuthStatus] = useState<AuthStatus | null>(null);
  const [pullRequests, setPullRequests] = useState<PullRequestSummary[]>([]);
  const [selectedPullRequest, setSelectedPullRequest] = useState<PullRequestSummary | null>(null);
  const [timelineItems, setTimelineItems] = useState<TimelineItem[]>([]);
  const [timelineStats, setTimelineStats] = useState<TimelineStats | null>(null);
  const [pullsLoading, setPullsLoading] = useState(false);
  const [timelineLoading, setTimelineLoading] = useState(false);
  const [loginFlow, setLoginFlow] = useState<DeviceLoginResponse | null>(null);
  const [loginLoading, setLoginLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [viewMode, setViewMode] = useState<'dashboard' | 'details'>('dashboard');
  const [locationHash, setLocationHash] = useState(window.location.hash);

  const selectedTitle = selectedPullRequest
    ? `#${selectedPullRequest.number} ${selectedPullRequest.title}`
    : 'Select a pull request';

  const groupedTimeline = useMemo(() => {
    return createTimelineStory(timelineItems).reduce<Record<string, TimelineStoryEntry[]>>((groups, entry) => {
      const day = new Date(entry.occurredAt).toLocaleDateString(undefined, {
        weekday: 'short',
        month: 'short',
        day: 'numeric',
        year: 'numeric',
      });
      groups[day] ??= [];
      groups[day].push(entry);
      return groups;
    }, {});
  }, [timelineItems]);

  const repoAccent = useMemo(() => colorForText(activeRepo), [activeRepo]);
  const teamMetrics = useMemo(() => createTeamMetrics(pullRequests), [pullRequests]);
  const attentionBuckets = useMemo(() => createAttentionBuckets(pullRequests), [pullRequests]);
  const forMeItems = useMemo(
    () => createForMeItems(pullRequests, authStatus?.login),
    [authStatus?.login, pullRequests],
  );
  const activityModel = useMemo(() => createActivityModel(timelineItems), [timelineItems]);
  const triageModel = useMemo(
    () =>
      selectedPullRequest && timelineStats
        ? createTriageModel(selectedPullRequest, timelineStats, timelineItems)
        : null,
    [selectedPullRequest, timelineItems, timelineStats],
  );
  useEffect(() => {
    void loadAuthStatus();
    void loadPullRequests(defaultRepoInput, state);
  }, []);

  useEffect(() => {
    function onPopState() {
      setLocationHash(window.location.hash);
      if (!window.location.hash.startsWith('#pr/')) {
        showDashboard(false);
      }
    }

    window.addEventListener('popstate', onPopState);
    window.addEventListener('hashchange', onPopState);
    return () => {
      window.removeEventListener('popstate', onPopState);
      window.removeEventListener('hashchange', onPopState);
    };
  }, []);

  useEffect(() => {
    const detail = parseDetailHash(locationHash);
    if (!detail || pullRequests.length === 0) {
      return;
    }

    if (
      selectedPullRequest?.repository === detail.repository
      && selectedPullRequest.number === detail.number
    ) {
      setViewMode('details');
      return;
    }

    const pullRequest = pullRequests.find(
      (item) => item.repository === detail.repository && item.number === detail.number,
    );

    if (pullRequest) {
      void loadTimeline(detail.repository, pullRequest, false);
    }
  }, [locationHash, pullRequests, selectedPullRequest]);

  async function loadAuthStatus() {
    try {
      const response = await fetch('/api/github/auth-status');
      setAuthStatus(await readJson<AuthStatus>(response));
    } catch (err) {
      setAuthStatus({
        authenticated: false,
        configured: false,
        canLogin: false,
        message: err instanceof Error ? err.message : 'Unable to check GitHub authentication.',
      });
    }
  }

  async function startGitHubLogin() {
    setLoginLoading(true);
    setError(null);

    try {
      const response = await fetch('/api/github/login/start', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: '{}',
      });
      const flow = await readJson<DeviceLoginResponse>(response);
      setLoginFlow(flow);

      if (flow.status === 'pending' || flow.status === 'slow_down') {
        void pollGitHubLogin(flow.intervalSeconds);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to start GitHub login.');
    } finally {
      setLoginLoading(false);
    }
  }

  async function pollGitHubLogin(intervalSeconds: number) {
    await delay(intervalSeconds * 1000);

    try {
      const response = await fetch('/api/github/login/poll', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: '{}',
      });
      const flow = await readJson<DeviceLoginResponse>(response);
      setLoginFlow(flow);

      if (flow.status === 'authorized') {
        setLoginFlow(null);
        await loadAuthStatus();
        await loadPullRequests(repo.trim(), state);
        return;
      }

      if (flow.status === 'pending' || flow.status === 'slow_down') {
        void pollGitHubLogin(flow.intervalSeconds);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to complete GitHub login.');
    }
  }

  async function logoutGitHub() {
    setError(null);
    setLoginFlow(null);

    try {
      const response = await fetch('/api/github/logout', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: '{}',
      });
      await readJson(response);
      await loadAuthStatus();
      setPullRequests([]);
      setSelectedPullRequest(null);
      setTimelineItems([]);
      setTimelineStats(null);
      setViewMode('dashboard');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to sign out.');
    }
  }

  async function loadPullRequests(repositoryInput: string, pullState: typeof state) {
    setPullsLoading(true);
    setError(null);
    setSelectedPullRequest(null);
    setTimelineItems([]);
    setTimelineStats(null);
    setViewMode('dashboard');

    try {
      const repositories = parseRepositories(repositoryInput);
      const responses = await Promise.all(
        repositories.map(async (repository) => {
          const query = new URLSearchParams({ repo: repository, state: pullState });
          const response = await fetch(`/api/github/pulls?${query}`);
          return await readJson<PullRequestListResponse>(response);
        }),
      );

      const pullRequests = responses
        .flatMap((data) =>
          data.pullRequests.map((pullRequest) => ({
            ...pullRequest,
            repository: data.repository,
          })),
        )
        .sort((first, second) => new Date(second.updatedAt).getTime() - new Date(first.updatedAt).getTime());

      setActiveRepo(repositories.length === 1 ? responses[0]?.repository ?? repositories[0] : `${repositories.length} repos`);
      setPullRequests(pullRequests);

    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to load pull requests.');
      setPullRequests([]);
    } finally {
      setPullsLoading(false);
    }
  }

  async function loadTimeline(repository: string, pullRequest: PullRequestSummary, updateHistory = true) {
    setTimelineLoading(true);
    setError(null);
    setSelectedPullRequest(pullRequest);
    setActiveRepo(repository);
    setViewMode('details');
    if (updateHistory) {
      pushDetailHistory(repository, pullRequest.number);
      setLocationHash(window.location.hash);
    }

    try {
      const query = new URLSearchParams({ repo: repository });
      const response = await fetch(`/api/github/pulls/${pullRequest.number}/timeline?${query}`);
      const data = await readJson<TimelineResponse>(response);
      setTimelineStats(data.stats);
      setTimelineItems(data.items);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to load pull request timeline.');
      setTimelineItems([]);
      setTimelineStats(null);
    } finally {
      setTimelineLoading(false);
    }
  }

  function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    void loadPullRequests(repo.trim(), state);
  }

  function showDashboard(updateHistory = true) {
    setViewMode('dashboard');
    if (updateHistory && window.location.hash) {
      window.history.replaceState({ view: 'dashboard' }, '', window.location.pathname + window.location.search);
      setLocationHash('');
    }
  }

  return (
    <div className="app-shell" style={{ '--repo-accent': repoAccent } as CSSProperties}>
      <header className="hero">
        <div>
          <p className="eyebrow">GitHub PR Dashboard</p>
          <h1>PR dashboard</h1>
          <p className="hero-copy">
            Find the pull requests that need attention and keep reviews moving.
          </p>
        </div>

        <div className={`auth-card ${authStatus?.authenticated ? 'ok' : 'warning'}`}>
          <span>{authStatus?.authenticated ? 'Authenticated' : 'Auth needed'}</span>
          <strong>{authStatus?.login ?? authStatus?.source ?? 'GitHub login'}</strong>
          <p>{authStatus?.message ?? 'Checking local GitHub auth...'}</p>
          {loginFlow?.userCode && (
            <div className="login-flow">
              <b>{loginFlow.userCode}</b>
              <a href={loginFlow.verificationUriComplete ?? loginFlow.verificationUri} target="_blank" rel="noreferrer">
                Continue on GitHub
              </a>
              <small>{loginFlow.message}</small>
            </div>
          )}
          <div className="auth-actions">
            {!authStatus?.authenticated && (
              <button
                type="button"
                onClick={() => void startGitHubLogin()}
                disabled={!authStatus?.canLogin || loginLoading}
              >
                {loginLoading ? 'Starting...' : 'Login with GitHub'}
              </button>
            )}
            {authStatus?.authenticated && (
              <button type="button" onClick={() => void logoutGitHub()}>
                Sign out
              </button>
            )}
          </div>
        </div>
      </header>

      <main className={`workspace ${viewMode}`}>
        {viewMode === 'dashboard' && (
          <>
            {(pullRequests.length > 0 || attentionBuckets.length > 0) && (
              <section className="panel queue-panel" aria-label="Review queue">
                {pullRequests.length > 0 && (
              <section className="pace-strip" aria-label="Team flow metrics">
                <div>
                  <span>Readiness ↑</span>
                  <strong>{teamMetrics.averageReadiness}%</strong>
                </div>
                <div>
                  <span>Review coverage ↑</span>
                  <strong>{teamMetrics.reviewCoverage}%</strong>
                </div>
                <div>
                  <span>Waiting ↓</span>
                  <strong>{teamMetrics.waiting}</strong>
                </div>
                <div>
                  <span>Idle ↓</span>
                  <strong>{teamMetrics.idle}</strong>
                </div>
              </section>
                )}

                {pullRequests.length > 0 && (
                  <section className="pick-panel" aria-label="For me">
                    <div className="attention-card-header">
                      <span>For me</span>
                      <strong>{forMeItems.length}</strong>
                    </div>
                    <p>
                      {authStatus?.login
                        ? `Clear these before picking up more work. Oldest unresolved reviews stay on top.`
                        : 'Sign in with gh auth to personalize this bucket.'}
                    </p>
                    <div className="pick-list">
                      {forMeItems.length === 0 && (
                        <p className="empty-for-me">
                          Nothing directly needs you right now.
                        </p>
                      )}
                      {forMeItems.map((item, index) => (
                        <button
                          key={`${item.pullRequest.repository}-${item.pullRequest.number}`}
                          type="button"
                          className={`pick-card ${item.tone}`}
                          onClick={() => void loadTimeline(item.pullRequest.repository, item.pullRequest)}
                        >
                          <span className="pick-rank">#{index + 1}</span>
                          <span className="pick-action">{item.action}</span>
                          <strong>{item.pullRequest.title}</strong>
                          <span className="pick-meta">
                            {shortRepoName(item.pullRequest.repository)} #{item.pullRequest.number} · {item.pullRequest.author}
                          </span>
                          <span className="pick-signals">
                            <span className={`attention-signal ${item.tone}`}>{item.action}</span>
                            {createForMeSignals(item).map((signal) => (
                              <span key={signal.label} className={`attention-signal ${signal.tone ?? 'muted'}`}>
                                {signal.label}
                              </span>
                            ))}
                          </span>
                        </button>
                      ))}
                    </div>
                  </section>
                )}

                {attentionBuckets.length > 0 && (
              <section className="attention-board" aria-label="What needs attention">
                <div className="section-title-row">
                  <p className="eyebrow">Team review board</p>
                  <h3>Current PR state</h3>
                  <p className="board-guidance">Oldest unresolved PRs are ranked first so new work does not bury review debt.</p>
                </div>
                <div className="attention-grid">
                  {attentionBuckets.map((bucket) => (
                    <article key={bucket.label} className={`attention-card ${bucket.tone}`}>
                      <div className="attention-card-header">
                        <span>{bucket.label}</span>
                        <strong>{bucket.items.length}</strong>
                      </div>
                      <p>{bucket.summary} <em>{bucket.metric}</em></p>
                      <div className="attention-list">
                        {bucket.items.map((item) => (
                          <button
                            key={`${item.pullRequest.repository}-${item.pullRequest.number}`}
                            type="button"
                            onClick={() => void loadTimeline(item.pullRequest.repository, item.pullRequest)}
                          >
                            <span className="attention-pr-number">#{item.pullRequest.number}</span>
                            <span className="attention-pr-repo">{shortRepoName(item.pullRequest.repository)}</span>
                            <strong>{item.pullRequest.title}</strong>
                            <span className="attention-pr-meta">
                              {item.pullRequest.author} · updated {formatRelative(item.pullRequest.updatedAt)}
                            </span>
                            <span className="attention-pr-signals">
                              {createAttentionSignals(item).map((signal) => (
                                <span key={signal.label} className={`attention-signal ${signal.tone ?? 'muted'}`}>
                                  {signal.label}
                                </span>
                              ))}
                            </span>
                          </button>
                        ))}
                      </div>
                    </article>
                  ))}
                </div>
              </section>
                )}
              </section>
            )}

            <section className="panel controls-panel" aria-labelledby="repo-heading">
          <div>
            <p className="eyebrow">Repository</p>
            <h2 id="repo-heading">Dashboard filters</h2>
          </div>

          <form className="repo-form" onSubmit={onSubmit}>
            <label>
              <span>Repositories</span>
              <input
                value={repo}
                onChange={(event) => setRepo(event.target.value)}
                placeholder={defaultRepoInput}
                autoComplete="off"
              />
              <small>Separate multiple repositories with commas.</small>
            </label>

            <label>
              <span>State</span>
              <select value={state} onChange={(event) => setState(event.target.value as typeof state)}>
                <option value="open">Open</option>
                <option value="closed">Closed</option>
                <option value="all">All</option>
              </select>
            </label>

            <button type="submit" disabled={pullsLoading}>
              {pullsLoading ? 'Loading...' : 'Load PRs'}
            </button>
          </form>

          {error && (
            <div className="error" role="alert">
              {error}
            </div>
          )}

          {pullsLoading && pullRequests.length === 0 && (
            <p className="empty-state">Loading pull requests...</p>
          )}
          {!pullsLoading && pullRequests.length === 0 && (
            <p className="empty-state">No pull requests loaded yet.</p>
          )}
            </section>
          </>
        )}

        {viewMode === 'details' && (
          <section className="panel timeline-panel details-panel" aria-labelledby="timeline-heading">
          <div className="timeline-header">
            <div>
              <p className="eyebrow">{activeRepo}</p>
              <h2 id="timeline-heading">{selectedTitle}</h2>
            </div>
            <div className="details-actions">
              <button type="button" onClick={() => showDashboard()}>
                Back to dashboard
              </button>
              {selectedPullRequest && (
                <a href={selectedPullRequest.htmlUrl} target="_blank" rel="noreferrer">
                  Open on GitHub
                </a>
              )}
            </div>
          </div>

          {timelineLoading ? (
            <p className="empty-state">Loading timeline...</p>
          ) : timelineItems.length === 0 ? (
            <p className="empty-state">Select a pull request to load its timeline.</p>
          ) : (
            <div>
              {triageModel && (
                <section className="triage-panel" aria-label="Pull request triage">
                  <article className={`triage-action ${triageModel.action.tone ?? 'muted'}`}>
                    <span>Next action</span>
                    <strong>{triageModel.action.label}</strong>
                    <p>{triageModel.why}</p>
                  </article>
                  <article className="triage-card">
                    <span>Waiting on</span>
                    <strong>{triageModel.waitingOn}</strong>
                    <div className="triage-signals">
                      {triageModel.signals.map((signal) => (
                        <span key={signal.label} className={`attention-signal ${signal.tone ?? 'muted'}`}>
                          {signal.label}
                        </span>
                      ))}
                    </div>
                  </article>
                </section>
              )}

              {activityModel && (
                <section className="activity-panel" aria-label="Pull request activity">
                  <div className="section-title-row">
                    <p className="eyebrow">Activity</p>
                    <h3>Where time went</h3>
                  </div>
                  <div className="activity-signals">
                    {activityModel.signals.map((signal) => (
                      <span key={signal.label} className={`attention-signal ${signal.tone ?? 'muted'}`}>
                        {signal.label}
                      </span>
                    ))}
                  </div>
                  <div className="activity-legend" aria-hidden="true">
                    <span className="commit">Commit</span>
                    <span className="review">Review</span>
                    <span className="approval">Approval</span>
                    <span className="changes">Changes</span>
                    <span className="comment">Comment</span>
                    <span className="bot">Bot</span>
                  </div>
                  <div className="activity-strip">
                    <div className="activity-rail" />
                    {activityModel.gaps.map((gap) => (
                      <span
                        key={gap.id}
                        className="activity-gap"
                        style={{ '--gap-left': `${gap.left}%`, '--gap-width': `${gap.width}%` } as CSSProperties}
                      >
                        {gap.label}
                      </span>
                    ))}
                    {activityModel.markers.map((marker) => (
                      <span
                        key={marker.id}
                        className={`activity-marker ${marker.tone}`}
                        style={{ '--marker-left': `${marker.left}%` } as CSSProperties}
                        title={marker.title}
                        aria-label={marker.title}
                      />
                    ))}
                  </div>
                  <div className="activity-range">
                    <span>{activityModel.startLabel}</span>
                    <span>{activityModel.endLabel}</span>
                  </div>
                </section>
              )}

              {triageModel && triageModel.participants.length > 0 && (
                <section className="developer-panel" aria-label="Per-developer timeline stats">
                  <div className="section-title-row">
                    <p className="eyebrow">People</p>
                    <h3>Who is involved</h3>
                  </div>
                  <div className="developer-grid">
                    {triageModel.participants.map((participant) => (
                      <article
                        key={participant.actor}
                        className="developer-card"
                        style={{ '--developer-accent': colorForText(participant.actor) } as CSSProperties}
                      >
                        <div className="developer-card-header">
                          <span className="avatar-dot">{initials(participant.actor)}</span>
                          <span>
                            <strong>{participant.actor}</strong>
                            <em>{participant.role}</em>
                          </span>
                        </div>
                        <p>{participant.summary}</p>
                      </article>
                    ))}
                  </div>
                </section>
              )}

              {triageModel && (
                <section className="milestone-panel" aria-label="Pull request signal milestones">
                  <div className="section-title-row">
                    <p className="eyebrow">Signals</p>
                    <h3>What matters</h3>
                  </div>
                  <div className="milestone-list">
                    {triageModel.milestones.map((milestone) => (
                      <article key={milestone.id} className={`milestone-card ${milestone.tone}`}>
                        <div>
                          <span className="event-pill">{milestone.event}</span>
                          <time dateTime={milestone.occurredAt}>{formatRelative(milestone.occurredAt)}</time>
                        </div>
                        <strong>{milestone.title}</strong>
                        {milestone.detail && <p>{milestone.detail}</p>}
                        {milestone.url && (
                          <a href={milestone.url} target="_blank" rel="noreferrer">
                            View on GitHub
                          </a>
                        )}
                      </article>
                    ))}
                  </div>
                </section>
              )}

              <details className="raw-activity">
                <summary>Raw activity</summary>
                <div className="timeline">
                  {Object.entries(groupedTimeline).map(([day, items]) => (
                    <section key={day} className="timeline-day" aria-label={day}>
                      <h3>{day}</h3>
                      {items.map((entry) => (
                        <article key={entry.id} className={`timeline-item ${entry.kind}`}>
                          <div className="timeline-dot" aria-hidden="true" />
                          <div className="timeline-content">
                            <div className="timeline-row">
                              <span className="event-pill">{entry.event.replace(/_/g, ' ')}</span>
                              <time dateTime={entry.occurredAt}>{formatTime(entry.occurredAt)}</time>
                            </div>
                            <strong>{entry.kind === 'event' ? storyHeadline(entry.item) : entry.summary}</strong>
                            {entry.kind === 'event' && entry.item.body && <p>{truncate(entry.item.body, 420)}</p>}
                            {entry.kind === 'summary' && <p>{entry.detail}</p>}
                            {entry.kind === 'event' && entry.item.htmlUrl && (
                              <a href={entry.item.htmlUrl} target="_blank" rel="noreferrer">
                                View event
                              </a>
                            )}
                          </div>
                        </article>
                      ))}
                    </section>
                  ))}
                </div>
              </details>
            </div>
          )}
          </section>
        )}
      </main>
    </div>
  );
}

function parseRepositories(value: string) {
  const repositories = value
    .split(/[,\s]+/)
    .map((repository) => repository.trim())
    .filter(Boolean);

  return [...new Set(repositories)].length > 0 ? [...new Set(repositories)] : defaultRepos;
}

function pushDetailHistory(repository: string, number: number) {
  const hash = `#pr/${encodeURIComponent(repository)}/${number}`;
  if (window.location.hash !== hash) {
    window.history.pushState({ view: 'details', repository, number }, '', hash);
  }
}

function parseDetailHash(hash: string) {
  const match = /^#pr\/([^/]+)\/(\d+)$/.exec(hash);
  if (!match) {
    return null;
  }

  return {
    repository: decodeURIComponent(match[1]),
    number: Number.parseInt(match[2], 10),
  };
}

function shortRepoName(repository: string) {
  const parts = repository.split('/');
  return parts[parts.length - 1] ?? repository;
}

function readinessForPullRequest(pullRequest: PullRequestSummary) {
  const updatedAgeMs = Date.now() - new Date(pullRequest.updatedAt).getTime();
  let readiness = 50;

  if (pullRequest.review.state === 'approved') {
    readiness += 25;
  }

  if (pullRequest.review.state === 'reviewed') {
    readiness += 10;
  }

  if (pullRequest.review.state === 'changes_requested') {
    readiness -= 20;
  }

  if (pullRequest.review.state === 'waiting') {
    readiness -= 10;
  }

  if (pullRequest.draft) {
    readiness -= 15;
  }

  if (updatedAgeMs <= 12 * hourMs) {
    readiness += 10;
  } else if (updatedAgeMs >= 2 * dayMs) {
    readiness -= 15;
  }

  if (pullRequest.review.reviewerCount >= 3) {
    readiness += 10;
  }

  if (pullRequest.review.approvalCount >= 2) {
    readiness += 10;
  }

  return Math.max(0, Math.min(100, readiness));
}

function createTeamMetrics(pullRequests: PullRequestSummary[]) {
  const readiness = pullRequests.map(readinessForPullRequest);
  const reviewed = pullRequests.filter((pullRequest) => pullRequest.review.state !== 'waiting').length;
  return {
    averageReadiness:
      readiness.length === 0 ? 0 : Math.round(readiness.reduce((total, value) => total + value, 0) / readiness.length),
    reviewCoverage: pullRequests.length === 0 ? 0 : Math.round((reviewed / pullRequests.length) * 100),
    waiting: pullRequests.filter((pullRequest) => pullRequest.review.state === 'waiting').length,
    idle: pullRequests.filter(
      (pullRequest) =>
        pullRequest.state === 'open'
        && Date.now() - new Date(pullRequest.updatedAt).getTime() >= 2 * dayMs,
    ).length,
  };
}

function createForMeItems(pullRequests: PullRequestSummary[], login?: string): PickItem[] {
  if (!login) {
    return [];
  }

  return pullRequests
    .filter((pullRequest) => pullRequest.state === 'open' && !pullRequest.draft)
    .map((pullRequest) => createPersonalPick(pullRequest, login))
    .filter((item): item is PickItem => item !== null)
    .sort((first, second) => pickScore(second) - pickScore(first))
    .slice(0, 5);
}

function createPersonalPick(pullRequest: PullRequestSummary, login: string): PickItem | null {
  if (pullRequest.requestedReviewers.some((reviewer) => sameLogin(reviewer, login))) {
    return {
      pullRequest,
      action: 'Review this',
      reason: `Review requested from you · ${pickReason(pullRequest)}`,
      tone: 'warning',
      personal: true,
    };
  }

  if (sameLogin(pullRequest.author, login) && pullRequest.review.state === 'changes_requested') {
    return {
      pullRequest,
      action: 'Respond here',
      reason: `Your PR has changes requested · ${pickReason(pullRequest)}`,
      tone: 'danger',
      personal: true,
    };
  }

  if (sameLogin(pullRequest.author, login) && pullRequest.review.state === 'approved') {
    return {
      pullRequest,
      action: 'Finish this',
      reason: `Your PR is approved and still open · ${pickReason(pullRequest)}`,
      tone: 'success',
      personal: true,
    };
  }

  return null;
}

function createForMeSignals(item: PickItem): AttentionSignal[] {
  const firstReason = item.reason.split(' · ')[0];
  const signals = createAttentionSignals({ pullRequest: item.pullRequest, reason: item.reason })
    .filter((signal) => signal.label !== item.action && signal.label !== actionSignal(item.pullRequest).label);

  return dedupeSignals([
    { label: firstReason, tone: 'accent' },
    ...signals,
  ]).slice(0, 7);
}

function pickScore(item: PickItem) {
  let score = item.personal ? 1000 : 0;
  if (item.action === 'Review this') score += 90;
  if (item.action === 'Merge this' || item.action === 'Finish this') score += 80;
  if (item.action === 'Respond here') score += 75;
  if (item.action === 'Finish review') score += 65;
  if (item.action === 'Unstick this') score += 45;
  if (item.pullRequest.review.state === 'changes_requested') score += 30;
  if (item.pullRequest.review.state === 'waiting') score += 45;
  if (item.pullRequest.review.state === 'reviewed') score += 25;
  if (item.pullRequest.review.state === 'approved') score += 5;
  if (isIdle(item.pullRequest)) score += 15;
  if (isBotAuthor(item.pullRequest.author)) score -= 120;
  return score
    + Math.min(80, Math.floor(ageMs(item.pullRequest.createdAt) / dayMs) * 4)
    + Math.min(20, Math.floor(updatedAgeMs(item.pullRequest) / dayMs));
}

function pickReason(pullRequest: PullRequestSummary) {
  const signals = [`open ${formatAge(pullRequest.createdAt)}`];
  if (pullRequest.review.approvalCount > 0) {
    signals.push(formatCount(pullRequest.review.approvalCount, 'approval'));
  } else if (pullRequest.review.reviewerCount > 0) {
    signals.push(`${formatCount(pullRequest.review.reviewerCount, 'reviewer')} · 0 approvals`);
  } else {
    signals.push('no reviews');
  }

  if (isIdle(pullRequest)) {
    signals.push(`idle ${formatAge(pullRequest.updatedAt)}`);
  }

  return signals.join(' · ');
}

function sameLogin(first: string, second: string) {
  return actorIdentityKey(first) === actorIdentityKey(second);
}

function createAttentionBuckets(pullRequests: PullRequestSummary[]): AttentionBucket[] {
  const buckets: AttentionBucket[] = [
    {
      label: 'Ready to merge',
      summary: 'Approved and waiting on a maintainer or author to finish.',
      tone: 'success',
      metric: 'merge rate ↑',
      items: [],
    },
    {
      label: 'Needs review',
      summary: 'Ready PRs that have not had a human review yet.',
      tone: 'warning',
      metric: 'coverage ↑',
      items: [],
    },
    {
      label: 'Review started',
      summary: 'Someone engaged, but the PR is not approved yet.',
      tone: 'accent',
      metric: 'finish reviews',
      items: [],
    },
    {
      label: 'Stalled',
      summary: 'Reviewed or discussed, then went quiet.',
      tone: 'warning',
      metric: 'idle ↓',
      items: [],
    },
    {
      label: 'Author response',
      summary: 'Review is blocked on changes from the PR author.',
      tone: 'danger',
      metric: 'blockers ↓',
      items: [],
    },
    {
      label: 'Draft',
      summary: 'Not ready for the shared review queue yet.',
      tone: 'accent',
      metric: 'readiness ↑',
      items: [],
    },
  ];
  const bucketsByLabel = new Map(buckets.map((bucket) => [bucket.label, bucket]));

  for (const pullRequest of pullRequests.filter((item) => item.state === 'open')) {
    bucketsByLabel.get(reviewBucketLabel(pullRequest))?.items.push({
      pullRequest,
      reason: reviewSignal(pullRequest),
    });
  }

  for (const bucket of buckets) {
    bucket.items.sort((first, second) => compareAttentionItems(bucket.label, first, second));
  }

  return buckets.filter((bucket) => bucket.items.length > 0);
}

function compareAttentionItems(bucket: string, first: AttentionItem, second: AttentionItem) {
  const priorityDiff = queuePriority(bucket, second.pullRequest) - queuePriority(bucket, first.pullRequest);
  if (priorityDiff !== 0) {
    return priorityDiff;
  }

  return new Date(first.pullRequest.createdAt).getTime() - new Date(second.pullRequest.createdAt).getTime();
}

function queuePriority(bucket: string, pullRequest: PullRequestSummary) {
  const openDays = Math.floor(ageMs(pullRequest.createdAt) / dayMs);
  const idleDays = Math.floor(updatedAgeMs(pullRequest) / dayMs);
  const botPenalty = isBotAuthor(pullRequest.author) ? 120 : 0;

  switch (bucket) {
    case 'Ready to merge':
      return 200 + openDays * 4 + ageDays(pullRequest.review.lastReviewedAt ?? pullRequest.updatedAt) * 3 - botPenalty;
    case 'Needs review':
      return 180 + openDays * 6 + idleDays - botPenalty;
    case 'Review started':
      return 160 + openDays * 4 + pullRequest.review.reviewerCount * 8 + pullRequest.review.commentedReviewCount - botPenalty;
    case 'Author response':
      return 220 + openDays * 3 + ageDays(pullRequest.review.lastReviewedAt ?? pullRequest.updatedAt) * 6 - botPenalty;
    case 'Stalled':
      return 170 + idleDays * 8 + openDays * 2 - botPenalty;
    case 'Draft':
      return openDays - botPenalty;
    default:
      return openDays - botPenalty;
  }
}

function reviewBucketLabel(pullRequest: PullRequestSummary) {
  if (pullRequest.draft) {
    return 'Draft';
  }

  if (pullRequest.review.state === 'approved') {
    return 'Ready to merge';
  }

  if (pullRequest.review.state === 'waiting') {
    return 'Needs review';
  }

  if (pullRequest.review.state === 'changes_requested') {
    return 'Author response';
  }

  if (isIdle(pullRequest)) {
    return 'Stalled';
  }

  return 'Review started';
}

function reviewSignal(pullRequest: PullRequestSummary) {
  if (pullRequest.draft) {
    return 'Draft';
  }

  if (pullRequest.review.state === 'changes_requested') {
    return 'Changes requested';
  }

  if (pullRequest.review.state === 'approved') {
    return `${formatCount(pullRequest.review.approvalCount, 'approval')}`;
  }

  if (pullRequest.review.state === 'waiting') {
    return 'No reviews';
  }

  if (isIdle(pullRequest)) {
    return `Idle ${formatRelative(pullRequest.updatedAt)}`;
  }

  return formatCount(pullRequest.review.reviewerCount, 'reviewer');
}

function createAttentionSignals(item: AttentionItem): AttentionSignal[] {
  const pullRequest = item.pullRequest;
  const signals: AttentionSignal[] = [actionSignal(pullRequest)];

  if (isIdle(pullRequest)) {
    signals.push({ label: `idle ${formatAge(pullRequest.updatedAt)}`, tone: 'warning' });
  }

  const ageSignal = oldFirstSignal(pullRequest);
  if (ageSignal) {
    signals.push(ageSignal);
  }

  signals.push({
    label: `open ${formatAge(pullRequest.createdAt)}`,
    tone: Date.now() - new Date(pullRequest.createdAt).getTime() >= 7 * dayMs ? 'warning' : 'muted',
  });

  const progress = reviewProgressSignal(pullRequest);
  if (progress) {
    signals.push(progress);
  }

  if (pullRequest.review.lastReviewedAt && pullRequest.review.state !== 'waiting') {
    signals.push({ label: `reviewed ${formatAge(pullRequest.review.lastReviewedAt)}`, tone: 'muted' });
  }

  if (pullRequest.review.commentedReviewCount > 0) {
    signals.push({ label: formatCount(pullRequest.review.commentedReviewCount, 'review comment'), tone: 'muted' });
  }

  for (const label of pullRequest.labels.slice(0, 2)) {
    signals.push({ label, tone: 'accent' });
  }

  if (isBotAuthor(pullRequest.author)) {
    signals.push({ label: 'bot', tone: 'accent' });
  }

  return signals.slice(0, 7);
}

function oldFirstSignal(pullRequest: PullRequestSummary): AttentionSignal | null {
  const openAge = ageMs(pullRequest.createdAt);
  if (openAge >= 14 * dayMs) {
    return { label: 'review debt', tone: 'danger' };
  }

  if (openAge >= 7 * dayMs) {
    return { label: 'old first', tone: 'warning' };
  }

  if (openAge < 12 * hourMs) {
    return { label: 'newer', tone: 'muted' };
  }

  return null;
}

function actionSignal(pullRequest: PullRequestSummary): AttentionSignal {
  if (pullRequest.draft) {
    return { label: 'draft', tone: 'muted' };
  }

  if (pullRequest.review.state === 'changes_requested') {
    return { label: 'author fix', tone: 'danger' };
  }

  if (pullRequest.review.state === 'approved') {
    return { label: 'merge', tone: 'success' };
  }

  if (pullRequest.review.state === 'waiting') {
    return { label: 'needs reviewer', tone: 'warning' };
  }

  if (isIdle(pullRequest)) {
    return { label: 'unstick', tone: 'warning' };
  }

  return { label: 'finish review', tone: 'accent' };
}

function reviewProgressSignal(pullRequest: PullRequestSummary): AttentionSignal | null {
  if (pullRequest.review.state === 'waiting') {
    return { label: 'no reviews', tone: 'warning' };
  }

  if (pullRequest.review.state === 'changes_requested') {
    return {
      label: formatCount(Math.max(1, pullRequest.review.changesRequestedCount), 'change request'),
      tone: 'danger',
    };
  }

  if (pullRequest.review.approvalCount > 0) {
    return { label: formatCount(pullRequest.review.approvalCount, 'approval'), tone: 'success' };
  }

  if (pullRequest.review.reviewerCount > 0) {
    return { label: `${formatCount(pullRequest.review.reviewerCount, 'reviewer')} · 0 approvals`, tone: 'accent' };
  }

  return null;
}

function updatedAgeMs(pullRequest: PullRequestSummary) {
  return ageMs(pullRequest.updatedAt);
}

function ageMs(value: string) {
  return Date.now() - new Date(value).getTime();
}

function ageDays(value: string) {
  return Math.floor(ageMs(value) / dayMs);
}

function createTriageModel(
  pullRequest: PullRequestSummary,
  stats: TimelineStats,
  items: TimelineItem[],
): TriageModel {
  const action = actionSignal(pullRequest);
  const signals = createAttentionSignals({ pullRequest, reason: '' })
    .filter((signal) => signal.label !== action.label)
    .slice(0, 6);

  if (stats.firstReviewDelayMs != null) {
    signals.push({ label: `first review ${formatDuration(stats.firstReviewDelayMs)}`, tone: 'accent' });
  }

  if (stats.longestHumanCommentGapMs != null) {
    signals.push({ label: `longest gap ${formatDuration(stats.longestHumanCommentGapMs)}`, tone: 'warning' });
  }

  return {
    action,
    why: triageWhy(pullRequest, stats, items),
    waitingOn: waitingOn(pullRequest),
    signals: dedupeSignals(signals).slice(0, 8),
    participants: createTriageParticipants(stats.developers),
    milestones: createSignalMilestones(pullRequest, items),
  };
}

function triageWhy(pullRequest: PullRequestSummary, stats: TimelineStats, items: TimelineItem[]) {
  if (pullRequest.draft) {
    return `Draft for ${formatAge(pullRequest.createdAt)}; keep it off the active review queue.`;
  }

  if (pullRequest.review.state === 'approved') {
    return `Approved ${formatAge(pullRequest.review.lastReviewedAt ?? pullRequest.updatedAt)} ago and still open.`;
  }

  if (pullRequest.review.state === 'changes_requested') {
    return `Changes were requested; the author needs to close the loop.`;
  }

  if (pullRequest.review.state === 'waiting') {
    return `Open for ${formatAge(pullRequest.createdAt)} with no human review recorded.`;
  }

  const latestHuman = [...items]
    .filter((item) => !isBotAuthor(item.actor))
    .sort((first, second) => new Date(second.occurredAt).getTime() - new Date(first.occurredAt).getTime())[0];

  if (latestHuman) {
    return `Review started, but there is no approval yet; last human signal was ${formatAge(latestHuman.occurredAt)} ago.`;
  }

  return stats.reviewCount > 0
    ? `Review started, but there is no approval yet.`
    : `No clear human review signal yet.`;
}

function waitingOn(pullRequest: PullRequestSummary) {
  if (pullRequest.draft || pullRequest.review.state === 'changes_requested') {
    return pullRequest.author;
  }

  if (pullRequest.review.state === 'approved') {
    return 'maintainer';
  }

  if (pullRequest.review.state === 'waiting') {
    return 'reviewer';
  }

  return 'reviewers';
}

function participantSummary(developer: DeveloperStats) {
  const parts = [
    developer.commitCount > 0 ? formatCount(developer.commitCount, 'commit') : null,
    developer.reviewCount > 0 ? formatCount(developer.reviewCount, 'review') : null,
    developer.approvalCount > 0 ? formatCount(developer.approvalCount, 'approval') : null,
    developer.changesRequestedCount > 0 ? formatCount(developer.changesRequestedCount, 'change request') : null,
    developer.commentCount > 0 ? formatCount(developer.commentCount, 'comment') : null,
  ].filter(Boolean);

  return parts.length > 0 ? parts.join(' · ') : formatCount(developer.activityCount, 'activity', 'activities');
}

function createTriageParticipants(developers: DeveloperStats[]): TriageParticipant[] {
  const groups: { key: string; actors: string[]; developers: DeveloperStats[] }[] = [];

  for (const developer of developers) {
    const key = actorIdentityKey(developer.actor);
    const group = groups.find((candidate) => actorKeysMatch(candidate.key, key));
    if (group) {
      group.actors.push(developer.actor);
      group.developers.push(developer);
    } else {
      groups.push({ key, actors: [developer.actor], developers: [developer] });
    }
  }

  return groups
    .map((group) => {
      const developer = mergeDevelopers(group.developers, preferredParticipantName(group.actors));
      return {
        actor: developer.actor,
        role: developerRole(developer),
        summary: participantSummary(developer),
      };
    })
    .slice(0, 6);
}

function mergeDevelopers(developers: DeveloperStats[], actor: string): DeveloperStats {
  const ordered = developers.flatMap((developer) => [developer.firstActivityAt, developer.lastActivityAt]).sort();
  return {
    actor,
    activityCount: developers.reduce((total, developer) => total + developer.activityCount, 0),
    commitCount: developers.reduce((total, developer) => total + developer.commitCount, 0),
    commentCount: developers.reduce((total, developer) => total + developer.commentCount, 0),
    reviewCount: developers.reduce((total, developer) => total + developer.reviewCount, 0),
    approvalCount: developers.reduce((total, developer) => total + developer.approvalCount, 0),
    changesRequestedCount: developers.reduce((total, developer) => total + developer.changesRequestedCount, 0),
    firstActivityAt: ordered[0] ?? developers[0].firstActivityAt,
    lastActivityAt: ordered[ordered.length - 1] ?? developers[0].lastActivityAt,
  };
}

function actorIdentityKey(actor: string) {
  return actor.toLowerCase().replace(/[^a-z0-9]/g, '');
}

function actorKeysMatch(first: string, second: string) {
  return first === second
    || (Math.abs(first.length - second.length) <= 2 && (first.startsWith(second) || second.startsWith(first)));
}

function preferredParticipantName(actors: string[]) {
  return [...new Set(actors)]
    .sort((first, second) =>
      Number(first.includes(' ')) - Number(second.includes(' '))
      || first.length - second.length
      || first.localeCompare(second))
    [0];
}

function dedupeSignals(signals: AttentionSignal[]) {
  const seen = new Set<string>();
  return signals.filter((signal) => {
    if (seen.has(signal.label)) {
      return false;
    }

    seen.add(signal.label);
    return true;
  });
}

function createSignalMilestones(pullRequest: PullRequestSummary, items: TimelineItem[]): SignalMilestone[] {
  const milestones: SignalMilestone[] = [
    {
      id: 'opened',
      occurredAt: pullRequest.createdAt,
      event: 'opened',
      title: `${pullRequest.author} opened the PR`,
      detail: `Open for ${formatAge(pullRequest.createdAt)}.`,
      tone: 'muted',
      url: pullRequest.htmlUrl,
    },
  ];

  const reviewRequests = items.filter(isReviewRequestEvent);
  if (reviewRequests.length > 0) {
    milestones.push({
      id: 'review-requests',
      occurredAt: reviewRequests[0].occurredAt,
      event: 'review requested',
      title: `${formatCount(reviewRequests.length, 'review request')}`,
      detail: summarizeReviewRequests(reviewRequests),
      tone: 'accent',
    });
  }

  const firstHumanComment = items.find((item) => item.event === 'commented' && !isBotAuthor(item.actor));
  if (firstHumanComment) {
    milestones.push({
      id: `first-human-comment-${firstHumanComment.id}`,
      occurredAt: firstHumanComment.occurredAt,
      event: 'human comment',
      title: `${firstHumanComment.actor} commented`,
      detail: 'First human discussion signal.',
      tone: 'accent',
      url: firstHumanComment.htmlUrl,
    });
  }

  for (const review of items.filter((item) => item.event === 'reviewed' && !isBotAuthor(item.actor))) {
    milestones.push({
      id: `review-${review.id}`,
      occurredAt: review.occurredAt,
      event: storyEventLabel(review),
      title: storyHeadline(review),
      detail: review.state ? `Review state: ${review.state.toLowerCase().replace(/_/g, ' ')}.` : undefined,
      tone: review.state?.toUpperCase() === 'APPROVED'
        ? 'success'
        : review.state?.toUpperCase() === 'CHANGES_REQUESTED'
          ? 'danger'
          : 'accent',
      url: review.htmlUrl,
    });
  }

  const latestCommit = [...items]
    .filter((item) => item.event === 'committed')
    .sort((first, second) => new Date(second.occurredAt).getTime() - new Date(first.occurredAt).getTime())[0];
  if (latestCommit) {
    milestones.push({
      id: `latest-commit-${latestCommit.id}`,
      occurredAt: latestCommit.occurredAt,
      event: 'latest commit',
      title: latestCommit.summary,
      detail: 'Most recent code-change signal.',
      tone: 'warning',
      url: latestCommit.htmlUrl,
    });
  }

  for (const gap of createQuietMilestones(items)) {
    milestones.push(gap);
  }

  const sorted = milestones.sort((first, second) => {
      if (first.id === 'opened') {
        return -1;
      }

      if (second.id === 'opened') {
        return 1;
      }

      return new Date(first.occurredAt).getTime() - new Date(second.occurredAt).getTime();
    });

  return sorted.length > 12 ? [sorted[0], ...sorted.slice(-11)] : sorted;
}

function createQuietMilestones(items: TimelineItem[]): SignalMilestone[] {
  const ordered = [...items]
    .filter((item) => !isLowSignalTimelineEvent(item))
    .sort((first, second) => new Date(first.occurredAt).getTime() - new Date(second.occurredAt).getTime());

  return ordered.flatMap((item, index) => {
    const next = ordered[index + 1];
    if (!next) {
      return [];
    }

    const gapMs = new Date(next.occurredAt).getTime() - new Date(item.occurredAt).getTime();
    if (gapMs < 2 * dayMs) {
      return [];
    }

    return [{
      id: `quiet-${item.id}-${next.id}`,
      occurredAt: item.occurredAt,
      event: 'quiet gap',
      title: `${formatDuration(gapMs)} with no high-signal activity`,
      detail: `Between ${storyEventLabel(item)} and ${storyEventLabel(next)}.`,
      tone: 'warning' as const,
    }];
  });
}

function createTimelineStory(items: TimelineItem[]): TimelineStoryEntry[] {
  const entries: TimelineStoryEntry[] = [];
  let reviewRequests: TimelineItem[] = [];
  let hiddenEvents: TimelineItem[] = [];

  function flushSummaries() {
    if (reviewRequests.length > 0) {
      entries.push(createSummaryEntry(
        'review-requests',
        reviewRequests,
        `${formatCount(reviewRequests.length, 'review request')}`,
        summarizeReviewRequests(reviewRequests),
      ));
      reviewRequests = [];
    }

    if (hiddenEvents.length > 0) {
      entries.push(createSummaryEntry(
        'hidden-events',
        hiddenEvents,
        `${formatCount(hiddenEvents.length, 'low-signal event')} hidden`,
        summarizeEvents(hiddenEvents),
      ));
      hiddenEvents = [];
    }
  }

  for (const item of items) {
    if (isReviewRequestEvent(item)) {
      reviewRequests.push(item);
      continue;
    }

    if (isLowSignalTimelineEvent(item)) {
      hiddenEvents.push(item);
      continue;
    }

    flushSummaries();
    entries.push({
      kind: 'event',
      id: item.id,
      occurredAt: item.occurredAt,
      event: storyEventLabel(item),
      item,
    });
  }

  flushSummaries();
  return entries;
}

function createSummaryEntry(
  kind: string,
  items: TimelineItem[],
  summary: string,
  detail: string,
): TimelineStoryEntry {
  return {
    kind: 'summary',
    id: `${kind}-${items[0].id}`,
    occurredAt: items[0].occurredAt,
    event: 'summary',
    summary,
    detail,
    count: items.length,
  };
}

function isReviewRequestEvent(item: TimelineItem) {
  return item.event === 'review_requested';
}

function isLowSignalTimelineEvent(item: TimelineItem) {
  if (isBotAuthor(item.actor)) {
    return true;
  }

  if (item.event === 'committed' || item.event === 'reviewed' || item.event === 'merged' || item.event === 'closed') {
    return false;
  }

  if (item.event === 'commented') {
    return isBotAuthor(item.actor) || !item.body;
  }

  return item.event === 'labeled'
    || item.event === 'unlabeled'
    || item.event === 'assigned'
    || item.event === 'unassigned'
    || item.event === 'copilot_work_started'
    || item.event === 'cross-referenced';
}

function storyEventLabel(item: TimelineItem) {
  if (item.event === 'reviewed' && item.state) {
    return item.state.toLowerCase().replace(/_/g, ' ');
  }

  return item.event.replace(/_/g, ' ');
}

function storyHeadline(item: TimelineItem) {
  if (item.event === 'reviewed') {
    const state = item.state?.toUpperCase();
    if (state === 'APPROVED') {
      return `${item.actor} approved`;
    }

    if (state === 'CHANGES_REQUESTED') {
      return `${item.actor} requested changes`;
    }

    if (state === 'COMMENTED') {
      return `${item.actor} left a review`;
    }
  }

  return item.summary;
}

function summarizeReviewRequests(items: TimelineItem[]) {
  return items
    .slice(0, 4)
    .map((item) => item.summary)
    .join(' · ');
}

function summarizeEvents(items: TimelineItem[]) {
  const counts = new Map<string, number>();
  for (const item of items) {
    const label = summaryEventLabel(item.event);
    counts.set(label, (counts.get(label) ?? 0) + 1);
  }

  return [...counts.entries()]
    .sort((first, second) => second[1] - first[1])
    .slice(0, 3)
    .map(([label, count]) => `${count} ${label}`)
    .join(' · ');
}

function summaryEventLabel(event: string) {
  return event === 'review_requested'
    ? 'review requests'
    : event === 'commented'
      ? 'comments'
      : event.replace(/_/g, ' ');
}

function createActivityModel(items: TimelineItem[]): ActivityModel | null {
  const ordered = [...items]
    .filter((item) => Number.isFinite(new Date(item.occurredAt).getTime()))
    .sort((first, second) => new Date(first.occurredAt).getTime() - new Date(second.occurredAt).getTime());

  if (ordered.length < 2) {
    return null;
  }

  const startMs = new Date(ordered[0].occurredAt).getTime();
  const endMs = new Date(ordered[ordered.length - 1].occurredAt).getTime();
  const spanMs = Math.max(hourMs, endMs - startMs);

  const markers = ordered.map((item) => {
    const occurredMs = new Date(item.occurredAt).getTime();
    return {
      id: item.id,
      left: clampPercent(((occurredMs - startMs) / spanMs) * 100),
      label: item.event.replace(/_/g, ' '),
      title: `${formatActivityTitle(item)} · ${formatRelative(item.occurredAt)}`,
      tone: activityTone(item),
    };
  });

  const gaps = ordered.flatMap((item, index) => {
    if (index === ordered.length - 1) {
      return [];
    }

    const currentMs = new Date(item.occurredAt).getTime();
    const nextMs = new Date(ordered[index + 1].occurredAt).getTime();
    const gapMs = nextMs - currentMs;

    if (gapMs < 2 * dayMs) {
      return [];
    }

    return [{
      id: `${item.id}-${ordered[index + 1].id}`,
      left: clampPercent(((currentMs - startMs) / spanMs) * 100),
      width: Math.max(3, clampPercent((gapMs / spanMs) * 100)),
      label: `${formatDuration(gapMs)} quiet`,
    }];
  });

  const humanEvents = ordered.filter((item) => !isBotAuthor(item.actor)).length;
  const botEvents = ordered.length - humanEvents;
  const reviews = markers.filter((marker) =>
    marker.tone === 'review' || marker.tone === 'approval' || marker.tone === 'changes').length;
  const commits = markers.filter((marker) => marker.tone === 'commit').length;
  const signals: AttentionSignal[] = [
    { label: `${formatCount(humanEvents, 'human event')}`, tone: humanEvents > 0 ? 'accent' : 'muted' },
    { label: `${formatCount(commits, 'commit')}`, tone: commits > 0 ? 'warning' : 'muted' },
    { label: `${formatCount(reviews, 'review')}`, tone: reviews > 0 ? 'accent' : 'muted' },
    { label: `${formatCount(botEvents, 'bot event')}`, tone: botEvents > humanEvents ? 'warning' : 'muted' },
  ];

  if (gaps.length > 0) {
    signals.push({ label: `${formatCount(gaps.length, 'quiet gap')}`, tone: 'warning' });
  }

  return {
    markers,
    gaps,
    signals,
    startLabel: formatDateShort(ordered[0].occurredAt),
    endLabel: formatDateShort(ordered[ordered.length - 1].occurredAt),
  };
}

function activityTone(item: TimelineItem): ActivityMarker['tone'] {
  if (isBotAuthor(item.actor)) {
    return 'bot';
  }

  if (item.event === 'committed') {
    return 'commit';
  }

  if (item.event === 'reviewed') {
    if (item.state?.toUpperCase() === 'APPROVED') {
      return 'approval';
    }

    if (item.state?.toUpperCase() === 'CHANGES_REQUESTED') {
      return 'changes';
    }

    return 'review';
  }

  if (item.event === 'commented') {
    return 'comment';
  }

  return 'event';
}

function formatActivityTitle(item: TimelineItem) {
  const event = item.event.replace(/_/g, ' ');
  const state = item.state ? ` (${item.state.toLowerCase().replace(/_/g, ' ')})` : '';
  return `${item.actor} ${event}${state}`;
}

function clampPercent(value: number) {
  return Math.min(100, Math.max(0, value));
}

function developerRole(developer: DeveloperStats) {
  if (developer.approvalCount > 0) {
    return 'Approver';
  }

  if (developer.changesRequestedCount > 0) {
    return 'Unblocker';
  }

  if (developer.reviewCount >= developer.commentCount && developer.reviewCount > 0) {
    return 'Reviewer';
  }

  if (developer.commentCount > developer.commitCount) {
    return 'Commentator';
  }

  if (developer.commitCount > 0) {
    return 'Builder';
  }

  return 'Participant';
}

async function readJson<T>(response: Response): Promise<T> {
  if (response.ok) {
    return (await response.json()) as T;
  }

  const payload = await response.json().catch(() => null);
  const detail =
    typeof payload?.detail === 'string'
      ? payload.detail
      : typeof payload?.title === 'string'
        ? payload.title
        : `HTTP ${response.status}`;
  throw new Error(detail);
}

function isIdle(pullRequest: PullRequestSummary) {
  return Date.now() - new Date(pullRequest.updatedAt).getTime() >= 2 * dayMs;
}

function delay(ms: number) {
  return new Promise((resolve) => window.setTimeout(resolve, ms));
}

function formatCount(count: number, singular: string, plural = `${singular}s`) {
  return `${count.toLocaleString()} ${count === 1 ? singular : plural}`;
}

function formatRelative(value: string) {
  const date = new Date(value);
  const diffMs = Date.now() - date.getTime();
  const diffHours = Math.max(1, Math.round(diffMs / 1000 / 60 / 60));

  if (diffHours < 24) {
    return `${diffHours}h ago`;
  }

  const diffDays = Math.round(diffHours / 24);
  return `${diffDays}d ago`;
}

function formatAge(value: string) {
  return formatRelative(value).replace(' ago', '');
}

function formatDateShort(value: string) {
  return new Date(value).toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
  });
}

function isBotAuthor(author: string) {
  const normalized = author.toLowerCase();
  return normalized.endsWith('[bot]')
    || normalized.includes('bot')
    || normalized === 'copilot'
    || normalized === 'github-actions';
}

function formatTime(value: string) {
  return new Date(value).toLocaleTimeString(undefined, {
    hour: 'numeric',
    minute: '2-digit',
  });
}

function formatDuration(value?: number) {
  if (value === undefined || value === null) {
    return 'n/a';
  }

  const totalMinutes = Math.max(0, Math.round(value / 1000 / 60));
  if (totalMinutes < 60) {
    return `${totalMinutes}m`;
  }

  const totalHours = Math.round(totalMinutes / 60);
  if (totalHours < 48) {
    return `${totalHours}h`;
  }

  const days = Math.floor(totalHours / 24);
  const hours = totalHours % 24;
  return hours === 0 ? `${days}d` : `${days}d ${hours}h`;
}

function colorForText(value: string) {
  let hash = 0;
  for (const char of value) {
    hash = (hash * 31 + char.charCodeAt(0)) >>> 0;
  }

  const hue = hash % 360;
  return `hsl(${hue} 68% 58%)`;
}

function initials(value: string) {
  return value
    .split(/[\s-]+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase())
    .join('');
}

function truncate(value: string, length: number) {
  return value.length > length ? `${value.slice(0, length)}...` : value;
}

export default App;
