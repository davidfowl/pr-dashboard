import { useEffect, useMemo, useState } from 'react';
import type { CSSProperties, FormEvent } from 'react';
import './App.css';
import AppInfo from './components/AppInfo';
import AuthCard from './components/AuthCard';
import DashboardView from './components/dashboard/DashboardView';
import DetailView from './components/detail/DetailView';
import { defaultRepoInput, defaultRepos } from './constants';
import type {
  AuthStatus,
  PullRequestListResponse,
  PullRequestSummary,
  PullState,
  TimelineItem,
  TimelineResponse,
  TimelineStats,
  TimelineStoryEntry,
} from './types';
import { colorForText } from './utils/format';
import { readJson } from './utils/http';
import {
  createActivityModel,
  createAttentionBuckets,
  createDeveloperPullRequestCounts,
  createForMeItems,
  createTimelineStory,
  createTriageModel,
} from './utils/models';
import { parseBucketHash, parseDetailHash, parseRepositories, pushDetailHistory, replaceBucketHistory } from './utils/routing';

function App() {
  const [repo, setRepo] = useState(defaultRepoInput);
  const [activeRepo, setActiveRepo] = useState(defaultRepos[0]);
  const [state, setState] = useState<PullState>('open');
  const [authStatus, setAuthStatus] = useState<AuthStatus | null>(null);
  const [pullRequests, setPullRequests] = useState<PullRequestSummary[]>([]);
  const [selectedPullRequest, setSelectedPullRequest] = useState<PullRequestSummary | null>(null);
  const [timelineItems, setTimelineItems] = useState<TimelineItem[]>([]);
  const [timelineStats, setTimelineStats] = useState<TimelineStats | null>(null);
  const [pullsLoading, setPullsLoading] = useState(false);
  const [timelineLoading, setTimelineLoading] = useState(false);
  const [loginLoading, setLoginLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [viewMode, setViewMode] = useState<'dashboard' | 'details'>('dashboard');
  const [locationHash, setLocationHash] = useState(window.location.hash);
  const [selectedBucketId, setSelectedBucketId] = useState(parseBucketHash(window.location.hash)?.bucketId ?? '');

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
  const developerPullRequestCounts = useMemo(() => createDeveloperPullRequestCounts(pullRequests), [pullRequests]);
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
    // Initial load intentionally captures the default pull state once.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    function onPopState() {
      const hash = window.location.hash;
      setLocationHash(hash);
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

  async function loadPullRequests(repositoryInput: string, pullState: PullState) {
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
          <p className="eyebrow">Aspire team focus</p>
          <h1>Aspire PR focus</h1>
          <p className="hero-copy">
            Find the pull requests that need attention and keep reviews moving.
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
            repo={repo}
            state={state}
            pullsLoading={pullsLoading}
            pullRequests={pullRequests}
            error={error}
            developerPullRequestCounts={developerPullRequestCounts}
            attentionBuckets={attentionBuckets}
            forMeItems={forMeItems}
            selectedBucketId={selectedBucketId}
            login={authStatus?.login}
            onRepoChange={setRepo}
            onStateChange={setState}
            onSubmit={onSubmit}
            onSelectBucket={selectBucket}
            onSelectPullRequest={(repository, pullRequest) => void loadTimeline(repository, pullRequest)}
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
            onBack={() => showDashboard()}
          />
        )}
      </main>

      <AppInfo />
    </div>
  );
}

export default App;
