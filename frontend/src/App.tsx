import { useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties, FormEvent } from 'react';
import './App.css';
import AppInfo from './components/AppInfo';
import AuthCard from './components/AuthCard';
import MobileNav from './components/MobileNav';
import NotificationSettings from './components/NotificationSettings';
import DashboardView from './components/dashboard/DashboardView';
import DetailView from './components/detail/DetailView';
import { emptyDashboardConfig, fetchDashboardConfig, getDashboardConfig } from './dashboardConfig';
import type {
  AuthStatus,
  DashboardConfig,
  DashboardMode,
  DevelopmentGitHubAccount,
  DevelopmentGitHubAccountsResponse,
  IssueListResponse,
  MergeableState,
  PullRequestChecksRequest,
  PullRequestChecksResponse,
  PullRequestSummary,
  PullState,
  ReviewLoadPerfStats,
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
import { beginAbortableLoad, cancelAbortableLoad } from './utils/loadLifecycle';
import { useMediaQuery } from './utils/useMediaQuery';
import {
  fetchPullRequestList,
  fetchPullRequests,
  replacePullRequestsByUpdatedAt,
  upsertManyByUpdatedAt,
} from './utils/pullRequests';
import type { PullRequestListResult } from './utils/pullRequests';
import {
  createActivityModel,
  createAttentionBuckets,
  createDeveloperPullRequestCounts,
  createFocusIssueBuckets,
  createForMeItems,
  createTimelineStory,
  createTriageModel,
  isGeneratedDocsPullRequest,
  needsVisibleCheckDetails,
} from './utils/models';
import {
  createShipWeekUrl,
  normalizeShipWeekRouteParams,
  parseBucketHash,
  parseDashboardMode,
  parseDetailHash,
  parseRepositories,
  parseShipWeekRouteParams,
  pushDashboardModeHistory,
  pushDetailHistory,
  replaceLegacyRepositorySearchParam,
  replaceBucketHistory,
} from './utils/routing';
import type { ShipWeekRouteParams } from './utils/routing';

type VisibleChecksRequestItem = {
  repository: string;
  number: number;
  headSha: string;
};

type LoadOptions = {
  forceRefresh?: boolean;
  preserveResults?: boolean;
  refreshSelectedPullRequest?: boolean;
  clearResultsOnError?: boolean;
};

type ReviewRefreshParams = {
  repositoryInput: string;
  pullState: PullState;
};

type IssueRefreshParams = ReviewRefreshParams;

type ShipWeekRefreshParams = ShipWeekRouteParams;

const autoRefreshIntervalMs = 5 * 60_000;
const autoRefreshJitterMs = 60_000;
const pullRequestSnapshotPollIntervalMs = 750;
const pullRequestSnapshotMaxPolls = 40;
const clipboardWriteTimeoutMs = 10_000;

function getAutoRefreshDelayMs() {
  return autoRefreshIntervalMs + Math.floor(Math.random() * autoRefreshJitterMs);
}

const emptyShipWeekLoadingState: ShipWeekLoadingState = {
  milestone: false,
  baseBranch: false,
  docs: false,
  issues: false,
};

function App() {
  const [dashboardConfig, setDashboardConfig] = useState<DashboardConfig>(emptyDashboardConfig);
  const [activeRepo, setActiveRepo] = useState('');
  const [state, setState] = useState<PullState>('open');
  const [authStatus, setAuthStatus] = useState<AuthStatus | null>(null);
  const [developmentAccounts, setDevelopmentAccounts] = useState<DevelopmentGitHubAccount[]>([]);
  const [selectedDevelopmentAccount, setSelectedDevelopmentAccount] = useState('');
  const [developmentAccountLoading, setDevelopmentAccountLoading] = useState(false);
  const isMobileNav = useMediaQuery('(max-width: 720px)');
  const [dashboardMode, setDashboardMode] = useState<DashboardMode>(() => parseDashboardMode(window.location.search));
  const [pullRequests, setPullRequests] = useState<PullRequestSummary[]>([]);
  const [issues, setIssues] = useState<ShipWeekIssueSummary[]>([]);
  const [reviewLastUpdatedAt, setReviewLastUpdatedAt] = useState<string | null>(null);
  const [reviewSnapshotStatus, setReviewSnapshotStatus] = useState<string | null>(null);
  const [reviewSnapshotError, setReviewSnapshotError] = useState<string | null>(null);
  const [reviewLoadPerfStats, setReviewLoadPerfStats] = useState<ReviewLoadPerfStats | null>(null);
  const [issuesLastUpdatedAt, setIssuesLastUpdatedAt] = useState<string | null>(null);
  const [shipWeekMilestone, setShipWeekMilestone] = useState('');
  const [shipWeekReleaseBranch, setShipWeekReleaseBranch] = useState('');
  const [shipWeekShareParams, setShipWeekShareParams] = useState<ShipWeekRouteParams>({
    repositoryInput: '',
    milestoneInput: '',
    releaseBranchInput: '',
  });
  const [shipWeek, setShipWeek] = useState<ShipWeekResponse | null>(null);
  const [shipWeekLastUpdatedAt, setShipWeekLastUpdatedAt] = useState<string | null>(null);
  const [selectedPullRequest, setSelectedPullRequest] = useState<PullRequestSummary | null>(null);
  const [timelineItems, setTimelineItems] = useState<TimelineItem[]>([]);
  const [timelineStats, setTimelineStats] = useState<TimelineStats | null>(null);
  const [mergeableState, setMergeableState] = useState<MergeableState | null>(null);
  const [pullsLoading, setPullsLoading] = useState(false);
  const [issuesLoading, setIssuesLoading] = useState(false);
  const [timelineLoading, setTimelineLoading] = useState(false);
  const [shipWeekLoading, setShipWeekLoading] = useState(false);
  const [shipWeekSectionLoading, setShipWeekSectionLoading] = useState<ShipWeekLoadingState>(emptyShipWeekLoadingState);
  const [loginLoading, setLoginLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [issuesError, setIssuesError] = useState<string | null>(null);
  const [shipWeekError, setShipWeekError] = useState<string | null>(null);
  const [shipWeekSnapshotStatus, setShipWeekSnapshotStatus] = useState<string | null>(null);
  const [shipWeekSnapshotError, setShipWeekSnapshotError] = useState<string | null>(null);
  const [isShipWeekSnapshotExporting, setIsShipWeekSnapshotExporting] = useState(false);
  const [isShipWeekSnapshotCopying, setIsShipWeekSnapshotCopying] = useState(false);
  const [showShipWeekSnapshotDownload, setShowShipWeekSnapshotDownload] = useState(false);
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
  const pullRequestsLoadVersionRef = useRef(0);
  const pullRequestsAbortControllerRef = useRef<AbortController | null>(null);
  const issuesLoadVersionRef = useRef(0);
  const issuesAbortControllerRef = useRef<AbortController | null>(null);
  const shipWeekLoadVersionRef = useRef(0);
  const shipWeekAbortControllerRef = useRef<AbortController | null>(null);
  const shipWeekSnapshotRef = useRef<HTMLElement | null>(null);
  const dashboardConfigRef = useRef<DashboardConfig>(emptyDashboardConfig);
  const reviewRefreshParamsRef = useRef<ReviewRefreshParams>({
    repositoryInput: '',
    pullState: 'open',
  });
  const issueRefreshParamsRef = useRef<IssueRefreshParams>({
    repositoryInput: '',
    pullState: 'open',
  });
  const shipWeekRefreshParamsRef = useRef<ShipWeekRefreshParams>({
    repositoryInput: '',
    milestoneInput: '',
    releaseBranchInput: '',
  });

  const selectedTitle = selectedPullRequest
    ? `#${selectedPullRequest.number} ${selectedPullRequest.title}`
    : 'Select a pull request';
  const currentMilestoneLabel = shipWeek?.milestone ?? shipWeekMilestone;
  const shipWeekShareUrl = createShipWeekUrl(shipWeekShareParams, dashboardConfig);

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
  // Model helpers read the active dashboard config singleton; keep these memos keyed by config changes.
  const developerPullRequestCounts = useMemo(
    () => {
      void dashboardConfig;
      return createDeveloperPullRequestCounts(pullRequests);
    },
    [dashboardConfig, pullRequests],
  );
  const attentionBuckets = useMemo(
    () => {
      void dashboardConfig;
      return createAttentionBuckets(pullRequests, authStatus?.login);
    },
    [authStatus?.login, dashboardConfig, pullRequests],
  );
  const issueBuckets = useMemo(
    () => createFocusIssueBuckets(issues, authStatus?.login),
    [authStatus?.login, issues],
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
    void initializeApp();
    // Initial load intentionally captures the default pull state once.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (viewMode !== 'dashboard') {
      return;
    }

    let timer: number | null = null;
    const scheduleNextRefresh = () => {
      timer = window.setTimeout(() => {
        if (document.visibilityState !== 'visible') {
          scheduleNextRefresh();
          return;
        }

        if (dashboardMode === 'ship') {
          if (!shipWeekLoading) {
            const params = shipWeekRefreshParamsRef.current;
            void loadShipWeek(params.repositoryInput, params.milestoneInput, params.releaseBranchInput, {
              preserveResults: true,
            });
          }

          scheduleNextRefresh();
          return;
        }

        if (dashboardMode === 'issues') {
          if (!issuesLoading) {
            const params = issueRefreshParamsRef.current;
            void loadIssues(params.repositoryInput, params.pullState, {
              preserveResults: true,
            });
          }

          scheduleNextRefresh();
          return;
        }

        if (!pullsLoading) {
          const params = reviewRefreshParamsRef.current;
          void loadPullRequests(params.repositoryInput, params.pullState, {
            preserveResults: true,
          });
        }

        scheduleNextRefresh();
      }, getAutoRefreshDelayMs());
    };

    scheduleNextRefresh();

    return () => {
      if (timer !== null) {
        window.clearTimeout(timer);
      }
    };
    // The timer only needs the active mode and loading guards; the loaders are stable function declarations.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dashboardMode, issuesLoading, pullsLoading, shipWeekLoading, viewMode]);

  useEffect(() => {
    function syncHashState() {
      const hash = window.location.hash;
      setLocationHash(hash);
      if (!hash.startsWith('#pr/')) {
        setViewMode('dashboard');
      }
    }

    function onPopState() {
      replaceLegacyRepositorySearchParam();
      const nextMode = parseDashboardMode(window.location.search);
      setDashboardMode(nextMode);
      if (nextMode === 'ship') {
        const shipWeekParams = parseShipWeekRouteParams(window.location.search, dashboardConfigRef.current);
        setShipWeekMilestone(shipWeekParams.milestoneInput);
        setShipWeekReleaseBranch(shipWeekParams.releaseBranchInput);
        if (!areShipWeekParamsEqual(shipWeekParams, shipWeekRefreshParamsRef.current)) {
          void loadShipWeek(
            shipWeekParams.repositoryInput,
            shipWeekParams.milestoneInput,
            shipWeekParams.releaseBranchInput,
          );
        }
      } else if (nextMode === 'issues') {
        if (issues.length === 0 && !issuesLoading) {
          const params = issueRefreshParamsRef.current;
          void loadIssues(params.repositoryInput, params.pullState);
        }
      } else if (pullRequests.length === 0 && !pullsLoading) {
        const params = reviewRefreshParamsRef.current;
        void loadPullRequests(params.repositoryInput, params.pullState);
      }
      syncHashState();
    }

    window.addEventListener('popstate', onPopState);
    window.addEventListener('hashchange', syncHashState);
    return () => {
      window.removeEventListener('popstate', onPopState);
      window.removeEventListener('hashchange', syncHashState);
    };
    // These listeners must be registered once; ship-mode reload decisions use refs and current URL state.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const detail = parseDetailHash(locationHash);
    if (!detail || pullRequests.length === 0) {
      return;
    }

    if (
      selectedPullRequest
      && selectedPullRequest.repository.toLowerCase() === detail.repository.toLowerCase()
      && selectedPullRequest.number === detail.number
    ) {
      setViewMode('details');
      return;
    }

    // Repository casing can drift between the deep-link URL and the loaded data (GitHub repo
    // names are case-insensitive), so match case-insensitively and use the canonical casing
    // from the loaded pull request when loading its timeline.
    const pullRequest = pullRequests.find(
      (item) =>
        item.repository.toLowerCase() === detail.repository.toLowerCase()
        && item.number === detail.number,
    );

    if (pullRequest) {
      void loadTimeline(pullRequest.repository, pullRequest, false);
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

  async function initializeApp() {
    const config = await loadDashboardConfiguration();
    if (!config) {
      return;
    }

    const repositoryInput = config.repositoryInput;
    replaceLegacyRepositorySearchParam();
    const shipWeekParams = parseShipWeekRouteParams(window.location.search, config);
    setActiveRepo(config.repositories[0] ?? '');
    setShipWeekMilestone(shipWeekParams.milestoneInput);
    setShipWeekReleaseBranch(shipWeekParams.releaseBranchInput);
    setShipWeekShareParams(shipWeekParams);
    reviewRefreshParamsRef.current = { repositoryInput, pullState: state };
    issueRefreshParamsRef.current = { repositoryInput, pullState: state };
    shipWeekRefreshParamsRef.current = shipWeekParams;

    await loadAuthStatus();
    if (dashboardMode === 'ship') {
      void loadShipWeek(shipWeekParams.repositoryInput, shipWeekParams.milestoneInput, shipWeekParams.releaseBranchInput);
    } else if (dashboardMode === 'issues') {
      void loadIssues(repositoryInput, state);
    } else {
      void loadPullRequests(repositoryInput, state);
    }
  }

  async function loadDashboardConfiguration() {
    try {
      const config = await fetchDashboardConfig();
      dashboardConfigRef.current = config;
      setDashboardConfig(config);
      return config;
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to load dashboard configuration.');
      return null;
    }
  }

  async function loadAuthStatus() {
    try {
      const response = await fetch('/api/github/auth-status');
      setAuthStatus(await readJson<AuthStatus>(response));
      await loadDevelopmentAccounts();
    } catch (err) {
      setAuthStatus({
        authenticated: false,
        configured: false,
        canLogin: false,
        message: err instanceof Error ? err.message : 'Unable to check GitHub authentication.',
      });
      setDevelopmentAccounts([]);
      setSelectedDevelopmentAccount('');
    }
  }

  async function loadDevelopmentAccounts() {
    try {
      const response = await fetch('/api/github/dev/accounts');
      if (response.status === 404) {
        setDevelopmentAccounts([]);
        setSelectedDevelopmentAccount('');
        return;
      }

      const data = await readJson<DevelopmentGitHubAccountsResponse>(response);
      setDevelopmentAccounts(data.accounts);
      setSelectedDevelopmentAccount(data.selectedLogin ?? '');
    } catch {
      setDevelopmentAccounts([]);
      setSelectedDevelopmentAccount('');
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
      cancelAbortableLoad(pullRequestsLoadVersionRef, pullRequestsAbortControllerRef);
      cancelAbortableLoad(issuesLoadVersionRef, issuesAbortControllerRef);
      cancelAbortableLoad(shipWeekLoadVersionRef, shipWeekAbortControllerRef);
      cancelVisibleChecksRequests();
      setPullsLoading(false);
      setIssuesLoading(false);
      setShipWeekLoading(false);
      setTimelineLoading(false);
      const config = await loadDashboardConfiguration();
      if (config) {
        const shipWeekParams = parseShipWeekRouteParams(window.location.search, config);
        setActiveRepo(config.repositories[0] ?? '');
        setShipWeekMilestone(shipWeekParams.milestoneInput);
        setShipWeekReleaseBranch(shipWeekParams.releaseBranchInput);
        setShipWeekShareParams(shipWeekParams);
        reviewRefreshParamsRef.current = { repositoryInput: config.repositoryInput, pullState: state };
        issueRefreshParamsRef.current = { repositoryInput: config.repositoryInput, pullState: state };
        shipWeekRefreshParamsRef.current = shipWeekParams;
      }
      clearLoadedGitHubData();
      showDashboard(true, false);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to sign out.');
    }
  }

  async function changeDevelopmentGitHubAccount(login: string) {
    if (login === selectedDevelopmentAccount) {
      return;
    }

    const previousDevelopmentAccount = selectedDevelopmentAccount;
    setSelectedDevelopmentAccount(login);
    setDevelopmentAccountLoading(true);
    setError(null);
    cancelVisibleChecksRequests();

    try {
      const response = await fetch('/api/github/dev/account', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ login: login || null }),
      });
      await readJson(response);
      clearLoadedGitHubData();
      showDashboard(true, false);
      const authStatusUpdate = loadAuthStatus();
      refreshCurrentDashboardData({
        refreshSelectedPullRequest: false,
        clearResultsOnError: true,
      });
      await authStatusUpdate;
    } catch (err) {
      setSelectedDevelopmentAccount(previousDevelopmentAccount);
      setError(err instanceof Error ? err.message : 'Unable to switch GitHub account.');
    } finally {
      setDevelopmentAccountLoading(false);
    }
  }

  function clearLoadedGitHubData() {
    setPullRequests([]);
    setIssues([]);
    setIssuesError(null);
    setReviewLastUpdatedAt(null);
    setReviewSnapshotStatus(null);
    setReviewSnapshotError(null);
    setReviewLoadPerfStats(null);
    setIssuesLastUpdatedAt(null);
    setShipWeek(null);
    setShipWeekLastUpdatedAt(null);
    setShipWeekError(null);
    resetShipWeekSnapshotState();
    clearSelectedPullRequest();
    setViewMode('dashboard');
  }

  function clearSelectedPullRequest() {
    currentSelectionRef.current = null;
    setSelectedPullRequest(null);
    setTimelineItems([]);
    setTimelineStats(null);
    setMergeableState(null);
    setTimelineLoading(false);
  }

  function refreshCurrentDashboardData(options: LoadOptions = {}) {
    if (dashboardMode === 'ship') {
      const params = shipWeekRefreshParamsRef.current;
      void loadShipWeek(params.repositoryInput, params.milestoneInput, params.releaseBranchInput, options);
    } else if (dashboardMode === 'issues') {
      const params = issueRefreshParamsRef.current;
      void loadIssues(params.repositoryInput, params.pullState, options);
    } else {
      const params = reviewRefreshParamsRef.current;
      void loadPullRequests(params.repositoryInput, params.pullState, { ...options, forceRefresh: true });
      if (options.refreshSelectedPullRequest !== false && selectedPullRequest) {
        void loadTimeline(selectedPullRequest.repository, selectedPullRequest, false);
      }
    }
  }

  async function loadPullRequests(repositoryInput: string, pullState: PullState, options: LoadOptions = {}) {
    const load = beginAbortableLoad(pullRequestsLoadVersionRef, pullRequestsAbortControllerRef);
    const { abortController, isCurrentLoad } = load;
    const previousPullRequests = pullRequests;
    const previousLastUpdatedAt = reviewLastUpdatedAt;
    const previousSnapshotStatus = reviewSnapshotStatus;
    const previousSnapshotError = reviewSnapshotError;
    const previousLoadPerfStats = reviewLoadPerfStats;
    const loadStartedAt = performance.now();
    let firstRowsMs: number | null = null;
    let requestCount = 0;

    setPullsLoading(true);
    setError(null);
    beginVisibleChecksRequestScope();
    if (!options.preserveResults) {
      clearSelectedPullRequest();
      setViewMode('dashboard');
    }

    try {
      const repositories = parseRepositories(repositoryInput, dashboardConfigRef.current.repositories);
      const hideRepositoryErrors = isConfiguredRepositoryInput(repositoryInput, dashboardConfigRef.current.repositoryInput);
      reviewRefreshParamsRef.current = { repositoryInput, pullState };
      setActiveRepo(repositories.length === 1 ? repositories[0] : `${repositories.length} repos`);
      if (!options.preserveResults) {
        setPullRequests([]);
        setReviewLastUpdatedAt(null);
        setReviewSnapshotStatus(null);
        setReviewSnapshotError(null);
        setReviewLoadPerfStats(null);
      }

      const fetchGroups = async () => {
        const groups = await fetchPullRequestGroups(
          repositories,
          pullState,
          options,
          abortController.signal,
          hideRepositoryErrors,
        );
        requestCount += groups.length;
        return groups;
      };

      let pullRequestGroups = await fetchGroups();
      if (isCurrentLoad()) {
        applyPullRequestListResults(pullRequestGroups, !shouldPollPullRequestSnapshots(pullRequestGroups));
      }

      for (let pollAttempt = 0;
        pollAttempt < pullRequestSnapshotMaxPolls
          && isCurrentLoad()
          && shouldPollPullRequestSnapshots(pullRequestGroups);
        pollAttempt++) {
        await waitForPullRequestSnapshotPoll(abortController.signal);
        pullRequestGroups = await fetchGroups();
        if (isCurrentLoad()) {
          applyPullRequestListResults(pullRequestGroups, !shouldPollPullRequestSnapshots(pullRequestGroups));
        }
      }
    } catch (err) {
      if (!isCurrentLoad() || (err instanceof DOMException && err.name === 'AbortError')) {
        return;
      }

      setError(err instanceof Error ? err.message : 'Unable to load pull requests.');
      if (!options.preserveResults || options.clearResultsOnError) {
        setPullRequests([]);
        setReviewLastUpdatedAt(null);
        setReviewSnapshotStatus(null);
        setReviewSnapshotError(null);
        setReviewLoadPerfStats(null);
      } else {
        setPullRequests(previousPullRequests);
        setReviewLastUpdatedAt(previousLastUpdatedAt);
        setReviewSnapshotStatus(previousSnapshotStatus);
        setReviewSnapshotError(previousSnapshotError);
        setReviewLoadPerfStats(previousLoadPerfStats);
      }
    } finally {
      if (isCurrentLoad()) {
        setPullsLoading(false);
        load.finish();
      }
    }

    function applyPullRequestListResults(pullRequestGroups: PullRequestListResult[], settled: boolean) {
      const nextPullRequests = replacePullRequestsByUpdatedAt(
        [],
        pullRequestGroups.flatMap((group) => group.pullRequests),
      );
      setPullRequests((currentPullRequests) => replacePullRequestsByUpdatedAt(currentPullRequests, nextPullRequests));
      setReviewLastUpdatedAt(getPullRequestListLastUpdatedAt(nextPullRequests, pullRequestGroups));
      const snapshotState = getPullRequestSnapshotState(pullRequestGroups);
      setReviewSnapshotStatus(snapshotState.status);
      setReviewSnapshotError(snapshotState.error);
      firstRowsMs ??= Math.round(performance.now() - loadStartedAt);
      setReviewLoadPerfStats({
        firstRowsMs,
        settledMs: settled ? Math.round(performance.now() - loadStartedAt) : null,
        requestCount,
        staleSnapshotCount: pullRequestGroups.filter((group) => group.snapshot?.stale).length,
      });
    }
  }

  async function loadIssues(repositoryInput: string, issueState: PullState, options: LoadOptions = {}) {
    const load = beginAbortableLoad(issuesLoadVersionRef, issuesAbortControllerRef);
    const { abortController, isCurrentLoad } = load;
    const previousIssues = issues;
    const previousLastUpdatedAt = issuesLastUpdatedAt;

    setIssuesLoading(true);
    setIssuesError(null);

    try {
      const repositories = parseRepositories(repositoryInput, dashboardConfigRef.current.repositories);
      const hideRepositoryErrors = isConfiguredRepositoryInput(repositoryInput, dashboardConfigRef.current.repositoryInput);
      issueRefreshParamsRef.current = { repositoryInput, pullState: issueState };
      setActiveRepo(repositories.length === 1 ? repositories[0] : `${repositories.length} repos`);
      if (!options.preserveResults) {
        setIssues([]);
        setIssuesLastUpdatedAt(null);
      }

      const issueFailures: string[] = [];
      const issueGroups = await Promise.all(
        repositories.map(async (repository) => {
          const query = new URLSearchParams({ repo: repository, state: issueState });
          if (options.forceRefresh) {
            query.set('refresh', 'true');
          }

          let data: IssueListResponse;
          try {
            const response = await fetch(`/api/github/issues/focus?${query}`, { signal: abortController.signal });
            data = await readJson<IssueListResponse>(response);
          } catch (err) {
            if (isAbortError(err)) {
              throw err;
            }

            const message = repositoryLoadError(repository, err);
            console.warn(message, err);
            if (!hideRepositoryErrors) {
              issueFailures.push(message);
            }
            return [];
          }

          if (!isCurrentLoad()) {
            return [];
          }

          setIssues((currentIssues) => upsertManyByUpdatedAt(currentIssues, data.issues));
          return data.issues;
        }),
      );

      if (isCurrentLoad()) {
        const nextIssues = upsertManyByUpdatedAt([], issueGroups.flat());
        setIssues(nextIssues);
        setIssuesLastUpdatedAt(getIssuesLastUpdatedAt(nextIssues));
        setIssuesError(issueFailures.length > 0 ? issueFailures.join(' ') : null);
      }
    } catch (err) {
      if (!isCurrentLoad() || (err instanceof DOMException && err.name === 'AbortError')) {
        return;
      }

      setIssuesError(err instanceof Error ? err.message : 'Unable to load issues.');
      if (!options.preserveResults || options.clearResultsOnError) {
        setIssues([]);
        setIssuesLastUpdatedAt(null);
      } else {
        setIssues(previousIssues);
        setIssuesLastUpdatedAt(previousLastUpdatedAt);
      }
    } finally {
      if (isCurrentLoad()) {
        setIssuesLoading(false);
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
    setShipWeekSnapshotStatus(null);
    setShipWeekSnapshotError(null);
    setShowShipWeekSnapshotDownload(false);
    beginVisibleChecksRequestScope();

    try {
      const shipWeekParams = normalizeShipWeekRouteParams(
        { repositoryInput, milestoneInput, releaseBranchInput },
        dashboardConfigRef.current,
      );
      const repositories = parseRepositories(shipWeekParams.repositoryInput, dashboardConfigRef.current.shipWeekRepositories);
      const hideRepositoryErrors = isConfiguredRepositoryInput(
        shipWeekParams.repositoryInput,
        dashboardConfigRef.current.shipWeekRepositoryInput,
      );
      const milestone = shipWeekParams.milestoneInput;
      const releaseBranch = shipWeekParams.releaseBranchInput;
      shipWeekRefreshParamsRef.current = shipWeekParams;
      setShipWeekShareParams(shipWeekParams);
      const releaseScopeRepositories = repositories.filter((repository) => !isDocsFromCodeRepository(repository));
      const docsRepositories = repositories.filter(isDocsFromCodeRepository);
      setActiveRepo(repositories.length === 1 ? repositories[0] : `${repositories.length} repos`);
      if (!options.preserveResults) {
        setShipWeek(null);
        setShipWeekLastUpdatedAt(null);
      }
      setShipWeekSectionLoading({
        milestone: releaseScopeRepositories.length > 0,
        baseBranch: releaseScopeRepositories.length > 0,
        docs: docsRepositories.length > 0,
        issues: releaseScopeRepositories.length > 0,
      });

      let releaseResponses: ShipWeekResponse[] = [];
      let docsPullRequests: PullRequestSummary[] = [];
      const shipWeekFailures: string[] = [];
      const releaseTasks = releaseScopeRepositories.map(async (repository) => {
        try {
          const response = await loadRepositoryShipWeek(repository, milestone, releaseBranch, options, abortController.signal);
          releaseResponses = [...releaseResponses, response];
        } catch (err) {
          if (isAbortError(err)) {
            throw err;
          }

          const message = repositoryLoadError(repository, err);
          console.warn(message, err);
          if (!hideRepositoryErrors) {
            shipWeekFailures.push(message);
          }
        }
      });
      const docsTasks = docsRepositories.map(async (repository) =>
        loadDocsFromCodePullRequests(repository, options, abortController.signal).catch((err: unknown) => {
          if (isAbortError(err)) {
            throw err;
          }

          const message = repositoryLoadError(repository, err);
          console.warn(message, err);
          if (!hideRepositoryErrors) {
            shipWeekFailures.push(message);
          }
          return [];
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
      const docsDoneTask = Promise.all(docsTasks).then((docsPullRequestGroups) => {
        docsPullRequests = replacePullRequestsByUpdatedAt(docsPullRequests, docsPullRequestGroups.flat());
      }).finally(() => {
        if (isCurrentLoad()) {
          setShipWeekSectionLoading((current) => ({
            ...current,
            docs: false,
          }));
        }
      });

      await Promise.all([releaseDoneTask, docsDoneTask]);
      if (isCurrentLoad()) {
        const nextShipWeek = combineShipWeekResponses(
          repositories,
          milestone,
          releaseBranch,
          releaseResponses,
          docsPullRequests,
        );
        setShipWeek(nextShipWeek);
        setShipWeekLastUpdatedAt(getShipWeekLastUpdatedAt(nextShipWeek));
        setShipWeekSnapshotError(shipWeekFailures.length > 0 ? shipWeekFailures.join(' ') : null);
      }
    } catch (err) {
      if (!isCurrentLoad() || (err instanceof DOMException && err.name === 'AbortError')) {
        return;
      }

      setShipWeekError(err instanceof Error ? err.message : 'Unable to load ship-week data.');
      if (!options.preserveResults || options.clearResultsOnError) {
        setShipWeek(null);
        setShipWeekLastUpdatedAt(null);
      }
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

  function requestVisibleChecks(
    repository: string,
    pullRequest: PullRequestSummary,
  ) {
    if (
      pullRequest.state !== 'open'
      || !pullRequest.headSha
      || !needsVisibleCheckDetails(pullRequest)
    ) {
      return false;
    }

    const key = checksRequestKey(repository, pullRequest.number, pullRequest.headSha);
    if (pendingVisibleChecksRef.current.has(key)) {
      return false;
    }

    const queuedItem = visibleChecksQueueRef.current.get(key);
    if (queuedItem) {
      return true;
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

    return true;
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
    const itemsByRepository = queuedItems.reduce((groups, item) => {
      const repositoryItems = groups.get(item.repository) ?? [];
      repositoryItems.push(item);
      groups.set(item.repository, repositoryItems);
      return groups;
    }, new Map<string, VisibleChecksRequestItem[]>());
    await Promise.all([...itemsByRepository].map(([repository, items]) =>
      loadVisibleChecks(
        repository,
        items,
        requestVersion,
        abortController.signal,
      )));
  }

  async function loadVisibleChecks(
    repository: string,
    items: VisibleChecksRequestItem[],
    requestVersion: number,
    signal: AbortSignal,
  ) {
    const requestedKeys = items.map((item) => checksRequestKey(item.repository, item.number, item.headSha));
    try {
      const query = new URLSearchParams({ repo: repository });
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
    const repositoryInput = dashboardConfigRef.current.repositoryInput;
    if (dashboardMode === 'issues') {
      void loadIssues(repositoryInput, state);
    } else {
      void loadPullRequests(repositoryInput, state);
    }
  }

  function onShipWeekSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const shipWeekParams = normalizeShipWeekRouteParams({
      repositoryInput: dashboardConfigRef.current.shipWeekRepositoryInput,
      milestoneInput: shipWeekMilestone,
      releaseBranchInput: shipWeekReleaseBranch,
    }, dashboardConfigRef.current);
    setShipWeekMilestone(shipWeekParams.milestoneInput);
    setShipWeekReleaseBranch(shipWeekParams.releaseBranchInput);
    pushDashboardModeHistory('ship', dashboardConfigRef.current, shipWeekParams);
    setLocationHash(window.location.hash);
    setSelectedBucketId('');
    setViewMode('dashboard');
    setDashboardMode('ship');
    void loadShipWeek(shipWeekParams.repositoryInput, shipWeekParams.milestoneInput, shipWeekParams.releaseBranchInput);
  }

  function onRefresh() {
    if (dashboardMode === 'ship') {
      const params = shipWeekRefreshParamsRef.current;
      void loadShipWeek(params.repositoryInput, params.milestoneInput, params.releaseBranchInput, {
        preserveResults: true,
      });
    } else if (dashboardMode === 'issues') {
      const params = issueRefreshParamsRef.current;
      void loadIssues(params.repositoryInput, params.pullState, {
        preserveResults: true,
      });
    } else {
      const params = reviewRefreshParamsRef.current;
      void loadPullRequests(params.repositoryInput, params.pullState, {
        forceRefresh: true,
        preserveResults: true,
      });
    }
  }

  function switchDashboardMode(mode: DashboardMode) {
    pushDashboardModeHistory(mode, dashboardConfigRef.current, shipWeekShareParams);
    setLocationHash(window.location.hash);
    setSelectedBucketId('');
    setViewMode('dashboard');
    setDashboardMode(mode);
    if (mode === 'ship' && !shipWeek && !shipWeekLoading) {
      const params = shipWeekRefreshParamsRef.current;
      void loadShipWeek(params.repositoryInput, params.milestoneInput, params.releaseBranchInput);
    } else if (mode === 'issues' && issues.length === 0 && !issuesLoading) {
      const params = issueRefreshParamsRef.current;
      void loadIssues(params.repositoryInput, params.pullState);
    } else if (mode === 'review' && pullRequests.length === 0 && !pullsLoading) {
      const params = reviewRefreshParamsRef.current;
      void loadPullRequests(params.repositoryInput, params.pullState);
    }
  }

  async function copyShipWeekShareLink() {
    clearShipWeekSnapshotMessage();

    if (!navigator.clipboard?.writeText) {
      setShipWeekSnapshotError('Clipboard unavailable. Copy the address bar URL instead.');
      return;
    }

    try {
      await navigator.clipboard.writeText(shipWeekShareUrl);
      setShipWeekSnapshotStatus('Share link copied.');
    } catch (err) {
      setShipWeekSnapshotError(err instanceof Error ? err.message : 'Unable to copy the share link.');
    }
  }

  async function downloadShipWeekSnapshot() {
    const element = shipWeekSnapshotRef.current;
    if (!element || !shipWeek) {
      setShipWeekSnapshotError('Load ship mode before exporting a snapshot.');
      return;
    }

    clearShipWeekSnapshotMessage();
    setIsShipWeekSnapshotExporting(true);

    try {
      const blob = await createShipWeekSnapshotBlob(element);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = createShipWeekSnapshotFilename(shipWeekShareParams, dashboardConfigRef.current);
      document.body.append(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
      setShipWeekSnapshotStatus('Snapshot downloaded.');
    } catch (err) {
      setShipWeekSnapshotError(err instanceof Error ? err.message : 'Unable to export the ship mode snapshot.');
      setShowShipWeekSnapshotDownload(true);
    } finally {
      setIsShipWeekSnapshotExporting(false);
    }
  }

  async function copyShipWeekSnapshotImage() {
    const element = shipWeekSnapshotRef.current;
    if (!element || !shipWeek) {
      setShipWeekSnapshotError('Load ship mode before copying a snapshot.');
      setShowShipWeekSnapshotDownload(false);
      return;
    }

    if (!navigator.clipboard?.write || !('ClipboardItem' in window)) {
      setShipWeekSnapshotError('This browser cannot copy PNG images to the clipboard. Download the PNG instead.');
      setShowShipWeekSnapshotDownload(true);
      return;
    }

    try {
      const writePromise = navigator.clipboard.write([
        new ClipboardItem({
          'image/png': createShipWeekSnapshotBlob(element),
        }),
      ]);
      clearShipWeekSnapshotMessage();
      setIsShipWeekSnapshotCopying(true);
      await withTimeout(
        writePromise,
        clipboardWriteTimeoutMs,
        'Clipboard blocked. Download PNG instead.',
      );
      setShipWeekSnapshotStatus('PNG copied to clipboard.');
    } catch (err) {
      setShipWeekSnapshotError(formatClipboardSnapshotError(err));
      setShowShipWeekSnapshotDownload(true);
    } finally {
      setIsShipWeekSnapshotCopying(false);
    }
  }

  function resetShipWeekSnapshotState() {
    clearShipWeekSnapshotMessage();
    setIsShipWeekSnapshotExporting(false);
    setIsShipWeekSnapshotCopying(false);
  }

  function clearShipWeekSnapshotMessage() {
    setShipWeekSnapshotStatus(null);
    setShipWeekSnapshotError(null);
    setShowShipWeekSnapshotDownload(false);
  }

  function showDashboard(updateHistory = true, preserveBucket = true) {
    setViewMode('dashboard');
    if (updateHistory && window.location.hash) {
      if (preserveBucket && selectedBucketId) {
        replaceBucketHistory(selectedBucketId);
        setLocationHash(window.location.hash);
      } else {
        setSelectedBucketId('');
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
      <MobileNav
        isMobile={isMobileNav}
        dashboardMode={dashboardMode}
        authStatus={authStatus}
        loginLoading={loginLoading}
        currentMilestoneLabel={currentMilestoneLabel}
        onSwitchMode={switchDashboardMode}
        onLogin={() => void startGitHubLogin()}
        onLogout={() => void logoutGitHub()}
      />
      <header className="hero">
        <div>
          <div className="hero-brand">
            <img className="hero-brand-logo" src="/aspire-logo-light-horizontal.svg" alt="Aspire" />
            <p className="eyebrow">Aspire Team App</p>
          </div>
          <h1>Aspire Team App</h1>
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
                className={dashboardMode === 'issues' ? 'selected' : undefined}
                aria-pressed={dashboardMode === 'issues'}
                onClick={() => switchDashboardMode('issues')}
              >
                Issues mode
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
              : dashboardMode === 'issues'
                ? 'Find the issues that need focused follow-up without mixing them into PR review work.'
                : 'Find the pull requests that need attention and keep reviews moving.'}
          </p>
        </div>

        {!isMobileNav && (
          <div className="hero-actions">
            <NotificationSettings authStatus={authStatus} />
            <AuthCard
              authStatus={authStatus}
              developmentAccounts={developmentAccounts}
              selectedDevelopmentAccount={selectedDevelopmentAccount}
              developmentAccountLoading={developmentAccountLoading}
              loginLoading={loginLoading}
              onLogin={() => void startGitHubLogin()}
              onLogout={() => void logoutGitHub()}
              onDevelopmentAccountChange={(login) => void changeDevelopmentGitHubAccount(login)}
            />
          </div>
        )}
      </header>

      <main className={`workspace ${viewMode}`}>
        {viewMode === 'dashboard' && (
          <DashboardView
            dashboardMode={dashboardMode}
            state={state}
            pullsLoading={pullsLoading}
            pullRequests={pullRequests}
            error={error}
            developerPullRequestCounts={developerPullRequestCounts}
            attentionBuckets={attentionBuckets}
            forMeItems={forMeItems}
            issues={issues}
            issueBuckets={issueBuckets}
            issuesLoading={issuesLoading}
            issuesError={issuesError}
            shipWeek={shipWeek}
            shipWeekLoading={shipWeekLoading}
            shipWeekSectionLoading={shipWeekSectionLoading}
            shipWeekError={shipWeekError}
            shipWeekMilestone={shipWeekMilestone}
            currentRelease={dashboardConfig.currentRelease}
            shipWeekReleaseBranch={shipWeekReleaseBranch}
            shipWeekSnapshotStatus={shipWeekSnapshotStatus}
            shipWeekSnapshotError={shipWeekSnapshotError}
            shipWeekSnapshotExporting={isShipWeekSnapshotExporting}
            shipWeekSnapshotCopying={isShipWeekSnapshotCopying}
            showShipWeekSnapshotDownload={showShipWeekSnapshotDownload}
            shipWeekSnapshotRef={shipWeekSnapshotRef}
            selectedBucketId={selectedBucketId}
            pullRequestSnapshotStatus={dashboardMode === 'review' ? reviewSnapshotStatus : null}
            pullRequestSnapshotError={dashboardMode === 'review' ? reviewSnapshotError : null}
            pullRequestLoadPerfStats={dashboardMode === 'review' ? reviewLoadPerfStats : null}
            lastUpdatedAt={dashboardMode === 'ship'
              ? shipWeekLastUpdatedAt
              : dashboardMode === 'issues'
                ? issuesLastUpdatedAt
                : reviewLastUpdatedAt}
            autoRefreshIntervalMs={autoRefreshIntervalMs}
            login={authStatus?.login}
            onStateChange={setState}
            onSubmit={onSubmit}
            onRefresh={onRefresh}
            onShipWeekMilestoneChange={setShipWeekMilestone}
            onShipWeekReleaseBranchChange={setShipWeekReleaseBranch}
            onShipWeekSubmit={onShipWeekSubmit}
            onCopyShipWeekShareLink={() => void copyShipWeekShareLink()}
            onCopyShipWeekSnapshotImage={() => void copyShipWeekSnapshotImage()}
            onDownloadShipWeekSnapshot={() => void downloadShipWeekSnapshot()}
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

      <footer className="app-footer">
        <AppInfo />
      </footer>
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
) {
  const query = new URLSearchParams({ repo: repository, state: 'open' });
  query.set('label', getDashboardConfig().docsFromCodeLabel);
  if (options.forceRefresh) {
    query.set('refresh', 'true');
  }

  return fetchPullRequests(`/api/github/pulls/graphql?${query}`, {
    signal,
    filter: isGeneratedDocsPullRequest,
  });
}

function fetchPullRequestGroups(
  repositories: string[],
  pullState: PullState,
  options: LoadOptions,
  signal: AbortSignal,
  hideRepositoryErrors: boolean,
) {
  return Promise.all(repositories.map(async (repository) => {
    const query = new URLSearchParams({ repo: repository, state: pullState });
    if (options.forceRefresh) {
      query.set('refresh', 'true');
    }

    try {
      return await fetchPullRequestList(`/api/github/pulls/graphql?${query}`, { signal });
    } catch (err) {
      if (isAbortError(err)) {
        throw err;
      }

      const message = repositoryLoadError(repository, err);
      console.warn(message, err);
      if (hideRepositoryErrors) {
        return null;
      }

      return {
        repository,
        pullRequests: [],
        snapshot: {
          source: 'error',
          fetchedAt: new Date().toISOString(),
          stale: false,
          refreshInProgress: false,
          refreshQueued: false,
          error: message,
        },
      } satisfies PullRequestListResult;
    }
  })).then((groups) => groups.filter((group): group is PullRequestListResult => group !== null));
}

function repositoryLoadError(repository: string, err: unknown) {
  const detail = err instanceof Error ? err.message : 'Unknown error';
  return `Unable to load ${repository}: ${detail}`;
}

function waitForPullRequestSnapshotPoll(signal: AbortSignal) {
  return new Promise<void>((resolve, reject) => {
    if (signal.aborted) {
      reject(new DOMException('The operation was aborted.', 'AbortError'));
      return;
    }

    const onAbort = () => {
      window.clearTimeout(timer);
      reject(new DOMException('The operation was aborted.', 'AbortError'));
    };
    const timer = window.setTimeout(() => {
      signal.removeEventListener('abort', onAbort);
      resolve();
    }, pullRequestSnapshotPollIntervalMs);
    signal.addEventListener('abort', onAbort, { once: true });
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
      ...docsPullRequests
        .filter((pullRequest) => docsFromCodeMatchesReleaseBranch(pullRequest, releaseBranch))
        .map(createDocsFromCodeShipWeekPullRequest),
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

function docsFromCodeMatchesReleaseBranch(pullRequest: PullRequestSummary, releaseBranch: string) {
  if (!releaseBranch || releaseBranch === 'release branches') {
    return true;
  }

  return pullRequest.baseRef === releaseBranch;
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

function getReviewLastUpdatedAt(
  pullRequests: PullRequestSummary[],
) {
  return getLatestFetchedAt(pullRequests.map((pullRequest) => pullRequest.fetchedAt));
}

function getPullRequestListLastUpdatedAt(
  pullRequests: PullRequestSummary[],
  pullRequestGroups: PullRequestListResult[],
) {
  return getLatestFetchedAt([
    getReviewLastUpdatedAt(pullRequests),
    ...pullRequestGroups.map((group) => group.snapshot?.fetchedAt),
  ]);
}

function shouldPollPullRequestSnapshots(pullRequestGroups: PullRequestListResult[]) {
  return pullRequestGroups.some((group) => group.snapshot?.refreshInProgress || group.snapshot?.refreshQueued);
}

function getPullRequestSnapshotState(pullRequestGroups: PullRequestListResult[]) {
  const snapshots = pullRequestGroups
    .map((group) => group.snapshot)
    .filter((snapshot): snapshot is NonNullable<typeof snapshot> => snapshot !== null && snapshot !== undefined);
  const error = snapshots.find((snapshot) => snapshot.error)?.error ?? null;
  if (snapshots.some((snapshot) => snapshot.refreshInProgress || snapshot.refreshQueued)) {
    return {
      status: snapshots.some((snapshot) => snapshot.stale)
        ? 'Showing cached data while checking GitHub for updates.'
        : 'Checking GitHub for updates.',
      error,
    };
  }

  if (snapshots.some((snapshot) => snapshot.stale)) {
    return {
      status: 'Showing cached data from the last successful refresh.',
      error,
    };
  }

  return { status: null, error };
}

function getIssuesLastUpdatedAt(issues: ShipWeekIssueSummary[]) {
  return getLatestFetchedAt(issues.map((issue) => issue.fetchedAt));
}

function getShipWeekLastUpdatedAt(shipWeek: ShipWeekResponse) {
  return getLatestFetchedAt([
    ...shipWeek.pullRequests.map((item) => item.pullRequest.fetchedAt),
    ...shipWeek.issues.map((issue) => issue.fetchedAt),
  ]);
}

function getLatestFetchedAt(timestamps: Array<string | null | undefined>) {
  let latestTime = Number.NEGATIVE_INFINITY;
  let latestTimestamp: string | null = null;

  for (const timestamp of timestamps) {
    if (!timestamp) {
      continue;
    }

    const time = Date.parse(timestamp);
    if (Number.isFinite(time) && time > latestTime) {
      latestTime = time;
      latestTimestamp = new Date(time).toISOString();
    }
  }

  return latestTimestamp;
}

function isDocsFromCodeRepository(repository: string) {
  const config = getDashboardConfig();
  return config.docsFromCodeRepository.length > 0
    && repository.toLowerCase() === config.docsFromCodeRepository.toLowerCase();
}

function areShipWeekParamsEqual(first: ShipWeekRouteParams, second: ShipWeekRouteParams) {
  return first.repositoryInput === second.repositoryInput
    && first.milestoneInput === second.milestoneInput
    && first.releaseBranchInput === second.releaseBranchInput;
}

function isConfiguredRepositoryInput(value: string, configuredValue: string) {
  const normalized = value.trim();
  return normalized.length === 0 || normalized === configuredValue.trim();
}

function createShipWeekSnapshotFilename(params: ShipWeekRouteParams, config: DashboardConfig) {
  const repositoryLabel = isConfiguredRepositoryInput(params.repositoryInput, config.shipWeekRepositoryInput)
    ? 'configured-repositories'
    : params.repositoryInput;
  return `ship-mode-${slugifyFilenamePart(params.milestoneInput)}-${slugifyFilenamePart(repositoryLabel)}.png`;
}

function slugifyFilenamePart(value: string) {
  return value
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    || 'snapshot';
}

async function createShipWeekSnapshotBlob(element: HTMLElement) {
  const { toBlob } = await import('html-to-image');
  const blob = await toBlob(element, {
    backgroundColor: '#120f24',
    cacheBust: true,
    pixelRatio: 2,
    filter: (node) =>
      !(node instanceof HTMLElement && node.dataset.snapshotExport === 'exclude'),
  });

  if (!blob) {
    throw new Error('Unable to render the ship mode snapshot.');
  }

  return blob;
}

function withTimeout<T>(promise: Promise<T>, timeoutMs: number, message: string) {
  let timeoutId: number | null = null;
  const timeout = new Promise<never>((_, reject) => {
    timeoutId = window.setTimeout(() => reject(new Error(message)), timeoutMs);
  });

  return Promise.race([promise, timeout]).finally(() => {
    if (timeoutId !== null) {
      window.clearTimeout(timeoutId);
    }
  });
}

function formatClipboardSnapshotError(err: unknown) {
  if (err instanceof Error && err.message === 'Clipboard blocked. Download PNG instead.') {
    return err.message;
  }

  return 'Clipboard blocked. Download PNG instead.';
}

function isAbortError(err: unknown) {
  return err instanceof DOMException && err.name === 'AbortError';
}

export default App;
