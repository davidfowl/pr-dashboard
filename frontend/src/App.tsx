import { useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties, FormEvent } from 'react';
import './App.css';
import AppInfo from './components/AppInfo';
import AuthCard from './components/AuthCard';
import DashboardView from './components/dashboard/DashboardView';
import DetailView from './components/detail/DetailView';
import {
  currentRelease,
  defaultRepoInput,
  defaultRepos,
  defaultShipWeekRepoInput,
  defaultShipWeekRepos,
  defaultShipWeekReleaseBranch,
  docsFromCodeLabel,
  docsFromCodeRepository,
} from './constants';
import type {
  AuthStatus,
  DashboardMode,
  IssueListResponse,
  MergeableState,
  PullRequestChecksRequest,
  PullRequestChecksResponse,
  PullRequestSummary,
  PullState,
  ShipWeekIssueSummary,
  ShipWeekLoadingState,
  ShipWeekResponse,
  TimelineItem,
  TimelineResponse,
  TimelineStats,
  TimelineStoryEntry,
} from './types';
import { colorForText } from './utils/format';
import { readJson } from './utils/http';
import { beginAbortableLoad } from './utils/loadLifecycle';
import { streamPullRequests, upsertByUpdatedAt, upsertManyByUpdatedAt } from './utils/pullRequests';
import {
  createActivityModel,
  createAttentionBuckets,
  createDeveloperPullRequestCounts,
  createForMeItems,
  createRegressionIssueBuckets,
  createTimelineStory,
  createTriageModel,
  isGeneratedDocsPullRequest,
} from './utils/models';
import {
  parseBucketHash,
  parseDashboardMode,
  parseDetailHash,
  parseRepositories,
  pushDashboardModeHistory,
  pushDetailHistory,
  replaceBucketHistory,
} from './utils/routing';

type VisibleChecksRequestItem = {
  repository: string;
  number: number;
  headSha: string;
};

type LoadOptions = {
  forceRefresh?: boolean;
};

const emptyShipWeekLoadingState: ShipWeekLoadingState = {
  milestone: false,
  baseBranch: false,
  docs: false,
  issues: false,
};

function App() {
  const [repo, setRepo] = useState(defaultRepoInput);
  const [activeRepo, setActiveRepo] = useState(defaultRepos[0]);
  const [state, setState] = useState<PullState>('open');
  const [authStatus, setAuthStatus] = useState<AuthStatus | null>(null);
  const [dashboardMode, setDashboardMode] = useState<DashboardMode>(() => parseDashboardMode(window.location.search));
  const [pullRequests, setPullRequests] = useState<PullRequestSummary[]>([]);
  const [reviewRegressionIssues, setReviewRegressionIssues] = useState<ShipWeekIssueSummary[]>([]);
  const [shipWeekRepo, setShipWeekRepo] = useState(defaultShipWeekRepoInput);
  const [shipWeekMilestone, setShipWeekMilestone] = useState(currentRelease);
  const [shipWeekReleaseBranch, setShipWeekReleaseBranch] = useState(defaultShipWeekReleaseBranch);
  const [shipWeek, setShipWeek] = useState<ShipWeekResponse | null>(null);
  const [selectedPullRequest, setSelectedPullRequest] = useState<PullRequestSummary | null>(null);
  const [timelineItems, setTimelineItems] = useState<TimelineItem[]>([]);
  const [timelineStats, setTimelineStats] = useState<TimelineStats | null>(null);
  const [mergeableState, setMergeableState] = useState<MergeableState | null>(null);
  const [pullsLoading, setPullsLoading] = useState(false);
  const [timelineLoading, setTimelineLoading] = useState(false);
  const [shipWeekLoading, setShipWeekLoading] = useState(false);
  const [shipWeekSectionLoading, setShipWeekSectionLoading] = useState<ShipWeekLoadingState>(emptyShipWeekLoadingState);
  const [loginLoading, setLoginLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [shipWeekError, setShipWeekError] = useState<string | null>(null);
  const [viewMode, setViewMode] = useState<'dashboard' | 'details'>('dashboard');
  const [locationHash, setLocationHash] = useState(window.location.hash);
  const [selectedBucketId, setSelectedBucketId] = useState(parseBucketHash(window.location.hash)?.bucketId ?? '');
  // Tracks the PR currently selected for detail view. Updated synchronously when loadTimeline
  // starts (and on logout / repo reload) so async completions can reliably detect whether they
  // still belong to the latest selection. A useEffect would lag behind a render and cause fast
  // cache hits to be incorrectly dropped.
  const currentSelectionRef = useRef<{ repository: string; number: number } | null>(null);
  const checksRequestVersionRef = useRef(0);
  const visibleChecksQueueRef = useRef(new Map<string, VisibleChecksRequestItem>());
  const pendingVisibleChecksRef = useRef(new Set<string>());
  const visibleChecksTimerRef = useRef<number | null>(null);
  const visibleChecksAbortControllerRef = useRef<AbortController | null>(null);
  const forceVisibleChecksRefreshRef = useRef(false);
  const pullRequestsLoadVersionRef = useRef(0);
  const pullRequestsAbortControllerRef = useRef<AbortController | null>(null);
  const shipWeekLoadVersionRef = useRef(0);
  const shipWeekAbortControllerRef = useRef<AbortController | null>(null);

  const selectedTitle = selectedPullRequest
    ? `#${selectedPullRequest.number} ${selectedPullRequest.title}`
    : 'Select a pull request';
  const currentMilestoneLabel = shipWeek?.milestone ?? shipWeekMilestone;

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
  const developerPullRequestCounts = useMemo(() => createDeveloperPullRequestCounts(pullRequests), [pullRequests]);
  const attentionBuckets = useMemo(() => createAttentionBuckets(pullRequests), [pullRequests]);
  const regressionIssueBuckets = useMemo(
    () => createRegressionIssueBuckets(reviewRegressionIssues),
    [reviewRegressionIssues],
  );
  const forMeItems = useMemo(
    () => createForMeItems(pullRequests, authStatus?.login),
    [authStatus?.login, pullRequests],
  );
  const activityModel = useMemo(() => createActivityModel(timelineItems), [timelineItems]);
  const triageModel = useMemo(
    () =>
      selectedPullRequest && timelineStats
        ? createTriageModel(selectedPullRequest, timelineStats, timelineItems, mergeableState)
        : null,
    [mergeableState, selectedPullRequest, timelineItems, timelineStats],
  );

  useEffect(() => {
    void loadAuthStatus();
    if (dashboardMode === 'ship') {
      void loadShipWeek(defaultShipWeekRepoInput, currentRelease, defaultShipWeekReleaseBranch);
    } else {
      void loadPullRequests(defaultRepoInput, state);
    }
    // Initial load intentionally captures the default pull state once.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    function onPopState() {
      const hash = window.location.hash;
      setLocationHash(hash);
      setDashboardMode(parseDashboardMode(window.location.search));
      if (!hash.startsWith('#pr/')) {
        setViewMode('dashboard');
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
    // loadTimeline closes over component state; including it would re-fire on every render.
    // The effect intentionally tracks only the hash + pull-request list + current selection.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [locationHash, pullRequests, selectedPullRequest]);

  useEffect(() => {
    const bucket = parseBucketHash(locationHash);
    if (bucket) {
      setSelectedBucketId(bucket.bucketId);
      setViewMode('dashboard');
      return;
    }

    if (!locationHash.startsWith('#pr/')) {
      setSelectedBucketId('');
    }
  }, [locationHash]);

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

    const returnUrl = `${window.location.pathname}${window.location.search}${window.location.hash}`;
    window.location.assign(`/api/github/login?returnUrl=${encodeURIComponent(returnUrl)}`);
  }

  async function logoutGitHub() {
    setError(null);
    cancelVisibleChecksRequests();

    try {
      const response = await fetch('/api/github/logout', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: '{}',
      });
      await readJson(response);
      await loadAuthStatus();
      setPullRequests([]);
      setReviewRegressionIssues([]);
      setShipWeek(null);
      setShipWeekError(null);
      currentSelectionRef.current = null;
      setSelectedPullRequest(null);
      setTimelineItems([]);
      setTimelineStats(null);
      setMergeableState(null);
      setViewMode('dashboard');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to sign out.');
    }
  }

  async function loadPullRequests(repositoryInput: string, pullState: PullState, options: LoadOptions = {}) {
    const load = beginAbortableLoad(pullRequestsLoadVersionRef, pullRequestsAbortControllerRef);
    const { abortController, isCurrentLoad } = load;

    setPullsLoading(true);
    setError(null);
    beginVisibleChecksRequestScope();
    forceVisibleChecksRefreshRef.current = options.forceRefresh ?? false;
    currentSelectionRef.current = null;
    setSelectedPullRequest(null);
    setTimelineItems([]);
    setTimelineStats(null);
    setMergeableState(null);
    setViewMode('dashboard');

    try {
      const repositories = parseRepositories(repositoryInput);
      setActiveRepo(repositories.length === 1 ? repositories[0] : `${repositories.length} repos`);
      setPullRequests([]);
      setReviewRegressionIssues([]);

      const pullRequestTasks = repositories.map(async (repository) => {
        const query = new URLSearchParams({ repo: repository, state: pullState });
        if (options.forceRefresh) {
          query.set('refresh', 'true');
        }

        await streamPullRequests(`/api/github/pulls/stream?${query}`, {
          signal: abortController.signal,
          onPullRequest: (pullRequest) => {
            if (!isCurrentLoad()) {
              return;
            }

            setPullRequests((currentPullRequests) => upsertByUpdatedAt(currentPullRequests, pullRequest));
          },
        });
      });

      await Promise.all([
        Promise.all(pullRequestTasks),
        Promise.all(
          repositories.map(async (repository) => {
            const query = new URLSearchParams({ repo: repository, state: 'open' });
            if (options.forceRefresh) {
              query.set('refresh', 'true');
            }

            const response = await fetch(`/api/github/regression-issues?${query}`, { signal: abortController.signal });
            const data = await readJson<IssueListResponse>(response);
            if (!isCurrentLoad()) {
              return;
            }

            setReviewRegressionIssues((currentIssues) => upsertManyByUpdatedAt(currentIssues, data.issues));
          }),
        ),
      ]);
    } catch (err) {
      if (!isCurrentLoad() || (err instanceof DOMException && err.name === 'AbortError')) {
        return;
      }

      setError(err instanceof Error ? err.message : 'Unable to load pull requests.');
      setPullRequests([]);
      setReviewRegressionIssues([]);
    } finally {
      if (isCurrentLoad()) {
        setPullsLoading(false);
        load.finish();
      }
    }
  }

  async function loadShipWeek(
    repositoryInput: string,
    milestoneInput: string,
    releaseBranchInput: string,
    options: LoadOptions = {},
  ) {
    const load = beginAbortableLoad(shipWeekLoadVersionRef, shipWeekAbortControllerRef);
    const { abortController, isCurrentLoad } = load;

    setShipWeekLoading(true);
    setShipWeekSectionLoading(emptyShipWeekLoadingState);
    setShipWeekError(null);
    beginVisibleChecksRequestScope();
    forceVisibleChecksRefreshRef.current = options.forceRefresh ?? false;

    try {
      const repositories = parseRepositories(repositoryInput, defaultShipWeekRepos);
      const milestone = milestoneInput.trim() || currentRelease;
      const releaseBranch = releaseBranchInput.trim();
      const releaseScopeRepositories = repositories.filter((repository) => !isDocsFromCodeRepository(repository));
      const docsRepositories = repositories.filter(isDocsFromCodeRepository);
      setActiveRepo(repositories.length === 1 ? repositories[0] : `${repositories.length} repos`);
      setShipWeek(null);
      setShipWeekSectionLoading({
        milestone: releaseScopeRepositories.length > 0,
        baseBranch: releaseScopeRepositories.length > 0,
        docs: docsRepositories.length > 0,
        issues: releaseScopeRepositories.length > 0,
      });

      let releaseResponses: ShipWeekResponse[] = [];
      let docsPullRequests: PullRequestSummary[] = [];
      const publishShipWeek = () => {
        if (!isCurrentLoad()) {
          return;
        }

        setShipWeek(combineShipWeekResponses(
          repositories,
          milestone,
          releaseBranch,
          releaseResponses,
          docsPullRequests,
        ));
      };

      const releaseTasks = releaseScopeRepositories.map(async (repository) => {
        const response = await loadRepositoryShipWeek(repository, milestone, releaseBranch, options, abortController.signal);
        releaseResponses = [...releaseResponses, response];
        publishShipWeek();
      });
      const docsTasks = docsRepositories.map((repository) =>
        loadDocsFromCodePullRequests(repository, options, abortController.signal, (pullRequest) => {
          docsPullRequests = upsertByUpdatedAt(docsPullRequests, pullRequest);
          publishShipWeek();
        }));
      const releaseDoneTask = Promise.all(releaseTasks).finally(() => {
        if (isCurrentLoad()) {
          setShipWeekSectionLoading((current) => ({
            ...current,
            milestone: false,
            baseBranch: false,
            issues: false,
          }));
        }
      });
      const docsDoneTask = Promise.all(docsTasks).finally(() => {
        if (isCurrentLoad()) {
          setShipWeekSectionLoading((current) => ({
            ...current,
            docs: false,
          }));
        }
      });

      publishShipWeek();
      await Promise.all([releaseDoneTask, docsDoneTask]);
    } catch (err) {
      if (!isCurrentLoad() || (err instanceof DOMException && err.name === 'AbortError')) {
        return;
      }

      setShipWeekError(err instanceof Error ? err.message : 'Unable to load ship-week data.');
      setShipWeek(null);
    } finally {
      if (isCurrentLoad()) {
        setShipWeekLoading(false);
        setShipWeekSectionLoading(emptyShipWeekLoadingState);
        load.finish();
      }
    }
  }

  function beginVisibleChecksRequestScope() {
    cancelVisibleChecksRequests();
    visibleChecksAbortControllerRef.current = new AbortController();
  }

  function cancelVisibleChecksRequests() {
    checksRequestVersionRef.current += 1;
    visibleChecksQueueRef.current.clear();
    pendingVisibleChecksRef.current.clear();
    if (visibleChecksTimerRef.current !== null) {
      window.clearTimeout(visibleChecksTimerRef.current);
      visibleChecksTimerRef.current = null;
    }

    visibleChecksAbortControllerRef.current?.abort();
    visibleChecksAbortControllerRef.current = null;
  }

  function requestVisibleChecks(repository: string, pullRequest: PullRequestSummary) {
    if (
      pullRequest.state !== 'open'
      || !pullRequest.headSha
      || pullRequest.checks?.state !== 'unknown'
    ) {
      return;
    }

    const key = checksRequestKey(repository, pullRequest.number, pullRequest.headSha);
    if (pendingVisibleChecksRef.current.has(key) || visibleChecksQueueRef.current.has(key)) {
      return;
    }

    pendingVisibleChecksRef.current.add(key);
    visibleChecksQueueRef.current.set(key, {
      repository,
      number: pullRequest.number,
      headSha: pullRequest.headSha,
    });

    if (visibleChecksTimerRef.current === null) {
      const requestVersion = checksRequestVersionRef.current;
      visibleChecksTimerRef.current = window.setTimeout(() => {
        void flushVisibleChecksQueue(requestVersion);
      }, 50);
    }
  }

  async function flushVisibleChecksQueue(requestVersion: number) {
    visibleChecksTimerRef.current = null;
    const queuedItems = [...visibleChecksQueueRef.current.values()];
    visibleChecksQueueRef.current.clear();
    if (queuedItems.length === 0 || requestVersion !== checksRequestVersionRef.current) {
      return;
    }

    const abortController = visibleChecksAbortControllerRef.current ?? new AbortController();
    visibleChecksAbortControllerRef.current = abortController;
    const forceRefresh = forceVisibleChecksRefreshRef.current;
    if (forceRefresh) {
      forceVisibleChecksRefreshRef.current = false;
    }
    const itemsByRepository = queuedItems.reduce((groups, item) => {
      const repositoryItems = groups.get(item.repository) ?? [];
      repositoryItems.push(item);
      groups.set(item.repository, repositoryItems);
      return groups;
    }, new Map<string, VisibleChecksRequestItem[]>());
    await Promise.all([...itemsByRepository].map(([repository, items]) =>
      loadVisibleChecks(repository, items, requestVersion, abortController.signal, forceRefresh)));
  }

  async function loadVisibleChecks(
    repository: string,
    items: VisibleChecksRequestItem[],
    requestVersion: number,
    signal: AbortSignal,
    forceRefresh: boolean,
  ) {
    const requestedKeys = items.map((item) => checksRequestKey(item.repository, item.number, item.headSha));
    try {
      const query = new URLSearchParams({ repo: repository });
      if (forceRefresh) {
        query.set('refresh', 'true');
      }
      const body: PullRequestChecksRequest = {
        pullRequests: items.map((item) => ({
          number: item.number,
          headSha: item.headSha,
        })),
      };
      const response = await fetch(`/api/github/pulls/checks?${query}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      });
      const data = await readJson<PullRequestChecksResponse>(response);
      if (signal.aborted || requestVersion !== checksRequestVersionRef.current) {
        return;
      }

      const checksByKey = new Map(
        data.pullRequests.map((pullRequest) => [
          checksRequestKey(data.repository, pullRequest.number, pullRequest.headSha),
          pullRequest.checks,
        ]),
      );

      setPullRequests((current) =>
        current.map((pullRequest) => {
          const headSha = pullRequest.headSha;
          if (!headSha) {
            return pullRequest;
          }

          const checks = checksByKey.get(checksRequestKey(pullRequest.repository, pullRequest.number, headSha));
          return checks ? { ...pullRequest, checks } : pullRequest;
        }),
      );
      setShipWeek((current) =>
        current
          ? {
            ...current,
            pullRequests: current.pullRequests.map((item) => {
              const headSha = item.pullRequest.headSha;
              if (!headSha) {
                return item;
              }

              const checks = checksByKey.get(checksRequestKey(item.pullRequest.repository, item.pullRequest.number, headSha));
              return checks ? { ...item, pullRequest: { ...item.pullRequest, checks } } : item;
            }),
          }
          : current,
      );
      setSelectedPullRequest((current) => {
        const headSha = current?.headSha;
        if (!current || !headSha) {
          return current;
        }

        const checks = checksByKey.get(checksRequestKey(current.repository, current.number, headSha));
        return checks ? { ...current, checks } : current;
      });
    } catch (err) {
      if (!isAbortError(err)) {
        console.warn('Unable to load visible pull request checks.', err);
      }
    } finally {
      for (const key of requestedKeys) {
        pendingVisibleChecksRef.current.delete(key);
      }
    }
  }

  async function loadTimeline(repository: string, pullRequest: PullRequestSummary, updateHistory = true) {
    setTimelineLoading(true);
    setError(null);
    // Set the selection ref BEFORE the async fetch starts so isSelectionStillCurrent always
    // sees the most recent selection — even when the timeline resolves from cache before the
    // next render commit.
    currentSelectionRef.current = { repository, number: pullRequest.number };
    setSelectedPullRequest(pullRequest);
    setActiveRepo(repository);
    setViewMode('details');
    if (updateHistory) {
      pushDetailHistory(repository, pullRequest.number);
      setLocationHash(window.location.hash);
    }

    const requestedRepository = repository;
    const requestedNumber = pullRequest.number;

    try {
      const query = new URLSearchParams({ repo: repository });
      const response = await fetch(`/api/github/pulls/${pullRequest.number}/timeline?${query}`);
      const data = await readJson<TimelineResponse>(response);
      setSelectedPullRequest((current) =>
        current && current.repository === requestedRepository && current.number === requestedNumber
          ? {
              ...current,
              checks: data.checks ?? current.checks,
            }
          : current,
      );
      setTimelineStats((current) =>
        isSelectionStillCurrent(requestedRepository, requestedNumber) ? data.stats : current,
      );
      setTimelineItems((current) =>
        isSelectionStillCurrent(requestedRepository, requestedNumber) ? data.items : current,
      );
      setMergeableState((current) =>
        isSelectionStillCurrent(requestedRepository, requestedNumber) ? (data.mergeableState ?? null) : current,
      );
    } catch (err) {
      if (!isSelectionStillCurrent(requestedRepository, requestedNumber)) {
        return;
      }
      setError(err instanceof Error ? err.message : 'Unable to load pull request timeline.');
      setTimelineItems([]);
      setTimelineStats(null);
      setMergeableState(null);
    } finally {
      if (isSelectionStillCurrent(requestedRepository, requestedNumber)) {
        setTimelineLoading(false);
      }
    }
  }

  function isSelectionStillCurrent(repository: string, number: number) {
    const current = currentSelectionRef.current;
    return current?.repository === repository && current.number === number;
  }

  function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    void loadPullRequests(repo.trim(), state);
  }

  function onShipWeekSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    switchDashboardMode('ship');
    void loadShipWeek(shipWeekRepo, shipWeekMilestone, shipWeekReleaseBranch);
  }

  function onRefresh() {
    if (dashboardMode === 'ship') {
      void loadShipWeek(shipWeekRepo, shipWeekMilestone, shipWeekReleaseBranch, { forceRefresh: true });
    } else {
      void loadPullRequests(repo.trim(), state, { forceRefresh: true });
    }
  }

  function switchDashboardMode(mode: DashboardMode) {
    pushDashboardModeHistory(mode);
    setDashboardMode(mode);
    if (mode === 'ship' && !shipWeek && !shipWeekLoading) {
      void loadShipWeek(shipWeekRepo, shipWeekMilestone, shipWeekReleaseBranch);
    } else if (mode === 'review' && pullRequests.length === 0 && !pullsLoading) {
      void loadPullRequests(repo.trim(), state);
    }
  }

  function showDashboard(updateHistory = true) {
    setViewMode('dashboard');
    if (updateHistory && window.location.hash) {
      if (selectedBucketId) {
        replaceBucketHistory(selectedBucketId);
        setLocationHash(window.location.hash);
      } else {
        window.history.replaceState({ view: 'dashboard' }, '', window.location.pathname + window.location.search);
        setLocationHash('');
      }
    }
  }

  function selectBucket(bucketId: string) {
    setSelectedBucketId(bucketId);
    replaceBucketHistory(bucketId);
    setLocationHash(window.location.hash);
  }

  return (
    <div className="app-shell" style={{ '--repo-accent': repoAccent } as CSSProperties}>
      <header className="hero">
        <div>
          <div className="hero-brand">
            <img className="hero-brand-logo" src="/aspire-logo-light-horizontal.svg" alt="Aspire" />
            <p className="eyebrow">Team focus</p>
          </div>
          <h1>PR focus</h1>
          <div className="hero-mode-row">
            <div className="mode-toggle" role="group" aria-label="Dashboard mode">
              <button
                type="button"
                className={dashboardMode === 'review' ? 'selected' : undefined}
                aria-pressed={dashboardMode === 'review'}
                onClick={() => switchDashboardMode('review')}
              >
                Review mode
              </button>
              <button
                type="button"
                className={dashboardMode === 'ship' ? 'selected' : undefined}
                aria-pressed={dashboardMode === 'ship'}
                onClick={() => switchDashboardMode('ship')}
              >
                Ship mode
              </button>
            </div>
            {dashboardMode === 'ship' && (
              <span className="mode-status ship">Milestone {currentMilestoneLabel}</span>
            )}
          </div>
          <p className="hero-copy">
            {dashboardMode === 'ship'
              ? 'Only milestone and base-branch work is shown; the normal attention queue is hidden.'
              : 'Find the pull requests that need attention and keep reviews moving.'}
          </p>
        </div>

        <AuthCard
          authStatus={authStatus}
          loginLoading={loginLoading}
          onLogin={() => void startGitHubLogin()}
          onLogout={() => void logoutGitHub()}
        />
      </header>

      <main className={`workspace ${viewMode}`}>
        {viewMode === 'dashboard' && (
          <DashboardView
            dashboardMode={dashboardMode}
            repo={repo}
            state={state}
            pullsLoading={pullsLoading}
            pullRequests={pullRequests}
            error={error}
            developerPullRequestCounts={developerPullRequestCounts}
            attentionBuckets={attentionBuckets}
            regressionIssueBuckets={regressionIssueBuckets}
            forMeItems={forMeItems}
            shipWeek={shipWeek}
            shipWeekLoading={shipWeekLoading}
            shipWeekSectionLoading={shipWeekSectionLoading}
            shipWeekError={shipWeekError}
            shipWeekRepo={shipWeekRepo}
            shipWeekMilestone={shipWeekMilestone}
            shipWeekReleaseBranch={shipWeekReleaseBranch}
            selectedBucketId={selectedBucketId}
            login={authStatus?.login}
            onRepoChange={setRepo}
            onStateChange={setState}
            onSubmit={onSubmit}
            onRefresh={onRefresh}
            onShipWeekRepoChange={setShipWeekRepo}
            onShipWeekMilestoneChange={setShipWeekMilestone}
            onShipWeekReleaseBranchChange={setShipWeekReleaseBranch}
            onShipWeekSubmit={onShipWeekSubmit}
            onSelectBucket={selectBucket}
            onSelectPullRequest={(repository, pullRequest) => void loadTimeline(repository, pullRequest)}
            onVisiblePullRequest={requestVisibleChecks}
          />
        )}

        {viewMode === 'details' && (
          <DetailView
            activeRepo={activeRepo}
            selectedTitle={selectedTitle}
            selectedPullRequest={selectedPullRequest}
            timelineLoading={timelineLoading}
            timelineItems={timelineItems}
            triageModel={triageModel}
            activityModel={activityModel}
            groupedTimeline={groupedTimeline}
            mergeableState={mergeableState}
            onBack={() => showDashboard()}
          />
        )}
      </main>

      <AppInfo />
    </div>
  );
}

function checksRequestKey(repository: string, number: number, headSha: string) {
  return `${repository.toLowerCase()}#${number}:${headSha}`;
}

async function loadRepositoryShipWeek(
  repository: string,
  milestone: string,
  releaseBranch: string,
  options: LoadOptions = {},
  signal?: AbortSignal,
) {
  const query = new URLSearchParams({ repo: repository, milestone });
  if (releaseBranch) {
    query.set('releaseBranch', releaseBranch);
  }
  if (options.forceRefresh) {
    query.set('refresh', 'true');
  }

  const response = await fetch(`/api/github/ship-week?${query}`, { signal });
  return normalizeShipWeekResponse(await readJson<ShipWeekResponse>(response));
}

function loadDocsFromCodePullRequests(
  repository: string,
  options: LoadOptions = {},
  signal?: AbortSignal,
  onPullRequest?: (pullRequest: PullRequestSummary) => void,
) {
  const query = new URLSearchParams({ repo: repository, state: 'open' });
  query.set('label', docsFromCodeLabel);
  if (options.forceRefresh) {
    query.set('refresh', 'true');
  }

  return streamPullRequests(`/api/github/pulls/stream?${query}`, {
    signal,
    filter: isGeneratedDocsPullRequest,
    onPullRequest,
  });
}

function combineShipWeekResponses(
  repositories: string[],
  requestedMilestone: string,
  requestedReleaseBranch: string,
  releaseResponses: ShipWeekResponse[],
  docsPullRequests: PullRequestSummary[],
): ShipWeekResponse {
  const releaseBranches = [...new Set(releaseResponses.map((response) => response.releaseBranch).filter(Boolean))];
  const repositoryLabel = repositories.length === 1 ? repositories[0] : `${repositories.length} repos`;
  const releaseBranch = requestedReleaseBranch
    || (releaseBranches.length === 1 ? releaseBranches[0] : releaseBranches.length > 1 ? 'release branches' : '');

  return {
    repository: repositoryLabel,
    milestone: releaseResponses[0]?.milestone ?? requestedMilestone,
    releaseBranch,
    pullRequests: [
      ...releaseResponses.flatMap((response) => response.pullRequests),
      ...docsPullRequests.map(createDocsFromCodeShipWeekPullRequest),
    ].sort(compareShipWeekPullRequests),
    issues: releaseResponses
      .flatMap((response) => response.issues)
      .sort((first, second) => new Date(second.updatedAt).getTime() - new Date(first.updatedAt).getTime()),
  };
}

function createDocsFromCodeShipWeekPullRequest(pullRequest: PullRequestSummary) {
  return {
    pullRequest,
    releaseScope: {
      inMilestone: false,
      targetsReleaseBranch: false,
      releaseBranchException: false,
      milestoneIssueNumbers: [],
      docsFromCode: true,
    },
  };
}

function compareShipWeekPullRequests(first: ShipWeekResponse['pullRequests'][number], second: ShipWeekResponse['pullRequests'][number]) {
  return Number(second.releaseScope.releaseBranchException) - Number(first.releaseScope.releaseBranchException)
    || Number(second.releaseScope.docsFromCode) - Number(first.releaseScope.docsFromCode)
    || new Date(first.pullRequest.createdAt).getTime() - new Date(second.pullRequest.createdAt).getTime()
    || first.pullRequest.repository.localeCompare(second.pullRequest.repository)
    || first.pullRequest.number - second.pullRequest.number;
}

function normalizeShipWeekResponse(response: ShipWeekResponse): ShipWeekResponse {
  return {
    ...response,
    pullRequests: response.pullRequests.map((item) => ({
      ...item,
      releaseScope: {
        ...item.releaseScope,
        docsFromCode: item.releaseScope.docsFromCode ?? false,
      },
      pullRequest: {
        ...item.pullRequest,
        repository: item.pullRequest.repository ?? response.repository,
      },
    })),
  };
}

function isDocsFromCodeRepository(repository: string) {
  return repository.toLowerCase() === docsFromCodeRepository;
}

function isAbortError(err: unknown) {
  return err instanceof DOMException && err.name === 'AbortError';
}

export default App;
