// @vitest-environment jsdom

import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import type {
  AppInfoResponse,
  AuthStatus,
  CheckState,
  DashboardConfig,
  DevelopmentGitHubAccountsResponse,
  PullRequestChecksRequest,
  PullRequestChecksResponse,
  PullRequestListResponse,
  PullRequestSummary,
  ShipWeekResponse,
  TimelineResponse,
} from './types';

type ActEnvironment = typeof globalThis & {
  IS_REACT_ACT_ENVIRONMENT?: boolean;
};

(globalThis as ActEnvironment).IS_REACT_ACT_ENVIRONMENT = true;

const testDashboardConfig: DashboardConfig = {
  repositories: [
    'microsoft/aspire',
    'microsoft/aspire.dev',
    'microsoft/aspire-skills',
    'microsoft/dcp',
    'CommunityToolkit/Aspire',
    'devdiv-microsoft/aspire-1p',
  ],
  repositoryInput:
    'microsoft/aspire, microsoft/aspire.dev, microsoft/aspire-skills, microsoft/dcp, CommunityToolkit/Aspire, devdiv-microsoft/aspire-1p',
  shipWeekRepositories: ['microsoft/aspire', 'microsoft/aspire.dev'],
  shipWeekRepositoryInput: 'microsoft/aspire, microsoft/aspire.dev',
  coreTeamMembers: ['davidfowl', 'karolz-ms', 'DamianEdwards'],
  coreTeamMemberAliasSuffixes: ['_microsoft'],
  communityRepositories: ['CommunityToolkit/Aspire'],
  currentRelease: '13.4',
  shipWeekReleaseBranch: '',
  docsFromCodeRepository: 'microsoft/aspire.dev',
  docsFromCodeLabel: 'docs-from-code',
  doNotMergeLabels: ['needs-author-action', 'no-merge'],
  botAuthors: ['dotnet-maestro', 'copilot-swe-agent'],
  nonBlockingCheckFailureRules: [
    {
      repository: 'devdiv-microsoft/aspire-1p',
      label: 'proof of presence',
      checkNames: ['GitOps/GitHubPop'],
      checkNameContains: ['proof of presence'],
    },
  ],
};

describe('App navigation', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
    window.history.replaceState(null, '', '/');
  });

  it('returns to the dashboard and clears PR hash when switching modes from detail view', async () => {
    window.history.replaceState(null, '', '/');
    vi.stubGlobal('fetch', createFetchMock());
    const { root } = await renderApp();

    await waitFor(() => getButton('View timeline'));

    await clickButton('View timeline');
    await waitFor(() => getButton('Back to dashboard'));
    expect(window.location.hash).toBe('#pr/microsoft%2Faspire/101');
    expect(document.body.textContent).toContain('#101 Fix dashboard navigation');

    await clickButton('Ship mode');

    await waitFor(() => {
      expect(document.body.textContent).toContain('Ship mode data');
      expect(document.body.textContent).not.toContain('Back to dashboard');
    });
    expect(window.location.search).toContain('mode=ship');
    expect(window.location.hash).toBe('');

    await unmountApp(root);
  });

  it('does not show repository entry controls', async () => {
    window.history.replaceState(null, '', '/');
    vi.stubGlobal('fetch', createFetchMock());
    const { root } = await renderApp();

    await waitFor(() => getButton('View timeline'));
    expect(queryInputByLabel('Repositories')).toBeNull();

    await clickButton('Issues mode');
    await waitFor(() => getButton('Load issues'));
    expect(queryInputByLabel('Issue repositories')).toBeNull();

    await clickButton('Ship mode');

    await waitFor(() => getButton('Load ship mode'));
    expect(window.location.search).not.toContain('repos=');
    expect(queryInputByLabel('Ship mode repositories')).toBeNull();

    await unmountApp(root);
  });

  it('returns to the dashboard and clears PR hash when switching dev accounts from detail view', async () => {
    window.history.replaceState(null, '', '/');
    const fetchMock = createFetchMock({
      authenticated: true,
      developmentAccounts: ['octocat', 'monalisa'],
      selectedDevelopmentAccount: 'octocat',
    });
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => getButton('View timeline'));
    await clickButton('View timeline');
    await waitFor(() => getButton('Back to dashboard'));
    expect(window.location.hash).toBe('#pr/microsoft%2Faspire/101');

    await changeDevAccount('monalisa');

    await waitFor(() => {
      expect(document.body.textContent).toContain('monalisa');
      expect(document.body.textContent).not.toContain('Back to dashboard');
    });
    expect(window.location.hash).toBe('');
    expect(requestUrls(fetchMock, '/api/github/pulls/101/timeline')).toHaveLength(1);

    await unmountApp(root);
  });

  it('clears previous account rows immediately while a dev-account switch refresh is pending', async () => {
    window.history.replaceState(null, '', '/');
    const switchedRefresh = createDeferred<void>();
    const fetchMock = createFetchMock({
      authenticated: true,
      developmentAccounts: ['octocat', 'monalisa'],
      selectedDevelopmentAccount: 'octocat',
      delayPullRequestsAfterDevelopmentAccountSwitch: switchedRefresh.promise,
      switchedPullRequestTitle: 'Monalisa account row',
    });
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Fix dashboard navigation');
    });

    await changeDevAccount('monalisa');

    await waitFor(() => {
      expect(document.body.textContent).toContain('monalisa');
      expect(document.body.textContent).not.toContain('Fix dashboard navigation');
      expect(document.body.textContent).not.toContain('Monalisa account row');
    });
    expect(pullRequestListUrls(fetchMock).some((url) => url.searchParams.get('refresh') === 'true')).toBe(true);

    await act(async () => {
      switchedRefresh.resolve();
    });

    await waitFor(() => {
      expect(document.body.textContent).toContain('Monalisa account row');
      expect(document.body.textContent).not.toContain('Fix dashboard navigation');
    });

    await unmountApp(root);
  });

  it('returns to the dashboard and clears PR hash on logout from detail view', async () => {
    window.history.replaceState(null, '', '/');
    const fetchMock = createFetchMock({ authenticated: true });
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => getButton('View timeline'));
    await clickButton('View timeline');
    await waitFor(() => getButton('Back to dashboard'));
    expect(window.location.hash).toBe('#pr/microsoft%2Faspire/101');

    await clickButton('Sign out');

    await waitFor(() => {
      expect(document.body.textContent).toContain('Sign in');
      expect(document.body.textContent).not.toContain('Back to dashboard');
      expect(document.body.textContent).not.toContain('#101 Fix dashboard navigation');
    });
    expect(window.location.hash).toBe('');
    expect(requestUrls(fetchMock, '/api/github/pulls/101/timeline')).toHaveLength(1);

    await unmountApp(root);
  });

  it('clears previous account rows and hides configured repo errors when a dev-account switch refresh fails', async () => {
    window.history.replaceState(null, '', '/');
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const fetchMock = createFetchMock({
      authenticated: true,
      developmentAccounts: ['octocat', 'monalisa'],
      selectedDevelopmentAccount: 'octocat',
      failPullRequestsAfterDevelopmentAccountSwitch: true,
    });
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Fix dashboard navigation');
    });

    await changeDevAccount('monalisa');

    await waitFor(() => {
      expect(document.body.textContent).not.toContain('Fix dashboard navigation');
      expect(document.body.textContent).not.toContain('Unable to load microsoft/aspire');
      expect(document.body.textContent).toContain('No pull requests loaded yet.');
    });
    expect(warn).toHaveBeenCalledWith(
      expect.stringContaining('Unable to load microsoft/aspire'),
      expect.any(Error),
    );

    await unmountApp(root);
  });

  it('hides the dev-account picker when OAuth is the active token source', async () => {
    window.history.replaceState(null, '', '/');
    const fetchMock = createFetchMock({
      authenticated: true,
      authSource: 'oauth',
      developmentAccounts: ['octocat', 'monalisa'],
      selectedDevelopmentAccount: 'octocat',
    });
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Fix dashboard navigation');
    });
    expect(document.querySelector('.dev-account-picker')).toBeNull();

    await unmountApp(root);
  });

  it('uses live PR-list refresh without forcing visible checks', async () => {
    window.history.replaceState(null, '', '/');
    const fetchMock = createFetchMock({
      authenticated: true,
      checksState: 'unknown',
      visibleChecksState: 'success',
    });
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => {
      expect(checksRequestUrls(fetchMock).some((url) => !url.searchParams.has('refresh'))).toBe(true);
    });
    await waitFor(() => {
      expect(document.body.textContent).toContain('octocat');
      expect((getButton('Refresh now') as HTMLButtonElement).disabled).toBe(false);
    });
    expect(document.body.textContent).toMatch(/Loaded in \d+ms \(6 GraphQL requests\)\./);

    const initialPullListRequestCount = pullRequestListUrls(fetchMock).length;

    await clickButton('Refresh now');
    await waitFor(() => {
      expect(pullRequestListUrls(fetchMock).length).toBeGreaterThan(initialPullListRequestCount);
    });
    expect(pullRequestListUrls(fetchMock).some((url) => url.searchParams.get('refresh') === 'true')).toBe(true);
    expect(checksRequestUrls(fetchMock).some((url) => url.searchParams.has('refresh'))).toBe(false);

    await unmountApp(root);
  });

  it('keeps loaded repositories visible and hides UI errors when one configured repository fails', async () => {
    window.history.replaceState(null, '', '/');
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const fetchMock = createFetchMock({
      authenticated: true,
      failedRepository: 'devdiv-microsoft/aspire-1p',
    });
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Fix dashboard navigation');
      expect(document.body.textContent).not.toContain('Unable to load devdiv-microsoft/aspire-1p');
      expect(document.body.textContent).not.toContain('devdiv-microsoft/aspire-1p');
    });
    expect(warn).toHaveBeenCalledWith(
      expect.stringContaining('Unable to load devdiv-microsoft/aspire-1p'),
      expect.any(Error),
    );
    expect(pullRequestListUrls(fetchMock).map((url) => url.searchParams.get('repo'))).toEqual(
      expect.arrayContaining(testDashboardConfig.repositories),
    );

    await unmountApp(root);
  });

  it('skips the checks fan-out when the PR-list already carries CI status', async () => {
    window.history.replaceState(null, '', '/');
    const fetchMock = createFetchMock({
      authenticated: true,
      checksState: 'success',
    });
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('octocat');
      expect((getButton('Refresh now') as HTMLButtonElement).disabled).toBe(false);
    });

    // The GraphQL list now resolves checks inline, so no follow-up /pulls/checks request fires.
    expect(checksRequestUrls(fetchMock)).toEqual([]);

    const initialPullListRequestCount = pullRequestListUrls(fetchMock).length;
    await clickButton('Refresh now');
    await waitFor(() => {
      expect(pullRequestListUrls(fetchMock).length).toBeGreaterThan(initialPullListRequestCount);
    });
    // A manual refresh re-fetches the list (which carries fresh checks) without a separate checks call.
    expect(checksRequestUrls(fetchMock)).toEqual([]);

    await unmountApp(root);
  });

  it('does not force-refresh rows that become visible after a live PR-list refresh', async () => {
    window.history.replaceState(null, '', '/');
    const observers = installIntersectionObserverMock();
    const fetchMock = createDelayedVisibleChecksFetchMock();
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('First visible row');
      expect(document.body.textContent).toContain('Late visible row');
      expect((getButton('Refresh now') as HTMLButtonElement).disabled).toBe(false);
    });

    const initialPullListRequestCount = pullRequestListUrls(fetchMock).length;
    await clickButton('Refresh now');
    await waitFor(() => {
      expect(pullRequestListUrls(fetchMock).length).toBeGreaterThan(initialPullListRequestCount);
    });

    await act(async () => {
      for (const observer of observers) {
        observer.trigger();
      }
    });
    expect(refreshChecksRequestNumbers(fetchMock)).toEqual([]);

    await unmountApp(root);
  });

  it('loads last-good rows immediately on F5 and swaps after the background refresh finishes', async () => {
    window.history.replaceState(null, '', '/');
    const liveRefresh = createDeferred<void>();
    const fetchMock = createInitialStaleSnapshotFetchMock(liveRefresh.promise);
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Last-good row');
      expect(document.body.textContent).toContain('Showing cached data while checking GitHub for updates.');
      expect(document.body.textContent).toMatch(/Shown in \d+ms; still refreshing \(\d+ GraphQL requests\)\./);
      expect((getButton('Refreshing...') as HTMLButtonElement).disabled).toBe(true);
    });

    await act(async () => {
      liveRefresh.resolve();
    });

    await waitFor(() => {
      expect(document.body.textContent).toContain('Fresh row');
      expect(document.body.textContent).not.toContain('Last-good row');
      expect(document.body.textContent).not.toContain('Showing cached data while checking GitHub for updates.');
      expect(document.body.textContent).toMatch(/Shown in \d+ms; settled in \d+ms \(\d+ GraphQL requests\)\./);
    });

    await unmountApp(root);
  });

  it('uses the agent review queue endpoint for the homepage Needs attention list', async () => {
    window.history.replaceState(null, '', '/');
    const serverQueueWinner = createPullRequest('success', {
      number: 401,
      title: 'Server-ranked review item',
      author: 'karolz-ms',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/401',
    });
    const rawClientCandidate = createPullRequest('success', {
      number: 402,
      title: 'Raw client candidate',
      author: 'davidfowl',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/402',
    });
    const fetchMock = createAgentReviewQueueFetchMock(serverQueueWinner, rawClientCandidate);
    vi.stubGlobal('fetch', fetchMock);

    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Server-ranked review item');
    });
    const focusPanel = document.querySelector('[aria-label="Focused attention queue"]');
    expect(focusPanel?.textContent).toContain('Server-ranked review item');
    expect(focusPanel?.textContent).not.toContain('Raw client candidate');
    const agentQueueUrls = requestUrls(fetchMock, '/api/agents/review-queue');
    expect(agentQueueUrls).toHaveLength(1);
    expect(agentQueueUrls[0].searchParams.get('limit')).toBe('1000');

    await unmountApp(root);
  });

  it('shows API outside Needs attention items when logged out', async () => {
    window.history.replaceState(null, '', '/');
    const serverQueueWinner = createPullRequest('success', {
      number: 410,
      title: 'Focused server item',
      author: 'karolz-ms',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/410',
    });
    const outsideItem = createPullRequest('failure', {
      number: 411,
      title: 'Outside server item',
      author: 'davidfowl',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/411',
    });
    const fetchMock = createLoggedOutAgentReviewQueueFetchMock(serverQueueWinner, outsideItem);
    vi.stubGlobal('fetch', fetchMock);

    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Focused server item');
    });
    const outsidePanel = document.querySelector('[aria-label="Pull requests outside Needs attention"]');
    expect(outsidePanel?.textContent).toContain('Outside Needs attention');
    expect(outsidePanel?.textContent).toContain('Outside server item');
    expect(outsidePanel?.textContent).toContain('Failing checks keep it out until CI is green again.');

    await unmountApp(root);
  });

  it('uses an empty successful agent review queue instead of falling back to client focus items', async () => {
    window.history.replaceState(null, '', '/');
    const rawClientCandidate = createPullRequest('success', {
      number: 403,
      title: 'Client fallback candidate',
      author: 'davidfowl',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/403',
    });
    const fetchMock = createEmptyAgentReviewQueueFetchMock(rawClientCandidate);
    vi.stubGlobal('fetch', fetchMock);

    const { root } = await renderApp();

    await waitFor(() => {
      expect(requestUrls(fetchMock, '/api/agents/review-queue')).toHaveLength(1);
    });
    const focusPanel = document.querySelector('[aria-label="Focused attention queue"]');
    expect(focusPanel?.textContent).toContain('No PRs with recent action-relevant activity need attention in the current results.');
    expect(focusPanel?.textContent).not.toContain('Client fallback candidate');

    await unmountApp(root);
  });

  it('does not use the open agent review queue while viewing closed pull requests', async () => {
    window.history.replaceState(null, '', '/');
    const serverQueueWinner = createPullRequest('success', {
      number: 404,
      title: 'Open server queue item',
      author: 'karolz-ms',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/404',
    });
    const rawClientCandidate = createPullRequest('success', {
      number: 405,
      title: 'Closed client candidate',
      state: 'closed',
      author: 'davidfowl',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/405',
    });
    const fetchMock = createAgentReviewQueueFetchMock(serverQueueWinner, rawClientCandidate);
    vi.stubGlobal('fetch', fetchMock);

    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Open server queue item');
    });
    await changePullRequestState('closed');
    await clickButton('Load PRs');

    await waitFor(() => {
      expect(pullRequestListUrls(fetchMock).some((url) => url.searchParams.get('state') === 'closed')).toBe(true);
    });
    const focusPanel = document.querySelector('[aria-label="Focused attention queue"]');
    expect(focusPanel?.textContent).not.toContain('Open server queue item');
    expect(requestUrls(fetchMock, '/api/agents/review-queue')).toHaveLength(1);

    await unmountApp(root);
  });

  it('ignores stale agent review queue responses after a later pull request load starts', async () => {
    window.history.replaceState(null, '', '/');
    const agentQueueLoaded = createDeferred<void>();
    const serverQueueWinner = createPullRequest('success', {
      number: 406,
      title: 'Stale server queue item',
      author: 'karolz-ms',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/406',
    });
    const rawClientCandidate = createPullRequest('success', {
      number: 407,
      title: 'Current closed row',
      state: 'closed',
      author: 'davidfowl',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/407',
    });
    const fetchMock = createDelayedAgentReviewQueueFetchMock(
      agentQueueLoaded.promise,
      serverQueueWinner,
      rawClientCandidate,
    );
    vi.stubGlobal('fetch', fetchMock);

    const { root } = await renderApp();

    await waitFor(() => {
      expect(requestUrls(fetchMock, '/api/agents/review-queue')).toHaveLength(1);
    });
    await changePullRequestState('closed');
    await clickButton('Load PRs');

    await waitFor(() => {
      expect(pullRequestListUrls(fetchMock).some((url) => url.searchParams.get('state') === 'closed')).toBe(true);
    });
    await act(async () => {
      agentQueueLoaded.resolve();
    });

    await waitFor(() => {
      const focusPanel = document.querySelector('[aria-label="Focused attention queue"]');
      expect(focusPanel?.textContent).not.toContain('Stale server queue item');
    });

    await unmountApp(root);
  });

  it('removes agent review queue rows when visible check enrichment finds failing CI', async () => {
    window.history.replaceState(null, '', '/');
    const serverQueueWinner = createPullRequest('unknown', {
      number: 408,
      title: 'Server queue item with stale checks',
      author: 'karolz-ms',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/408',
      headSha: 'stale-checks-sha',
    });
    const rawClientCandidate = createPullRequest('success', {
      number: 409,
      title: 'Raw client candidate after checks',
      author: 'davidfowl',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/409',
    });
    const fetchMock = createAgentReviewQueueVisibleChecksFetchMock(
      serverQueueWinner,
      rawClientCandidate,
      'failure',
    );
    vi.stubGlobal('fetch', fetchMock);

    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Server queue item with stale checks');
    });
    await waitFor(() => {
      expect(requestUrls(fetchMock, '/api/github/pulls/checks')).toHaveLength(1);
    });
    await waitFor(() => {
      const focusPanel = document.querySelector('[aria-label="Focused attention queue"]');
      expect(focusPanel?.textContent).not.toContain('Server queue item with stale checks');
    });

    await unmountApp(root);
  });

  it('keeps visible check enrichment safe before an agent review queue is loaded', async () => {
    window.history.replaceState(null, '', '/');
    const fetchMock = createFetchMock({
      authenticated: true,
      checksState: 'unknown',
      visibleChecksState: 'success',
    });
    vi.stubGlobal('fetch', fetchMock);

    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Fix dashboard navigation');
      expect(checksRequestUrls(fetchMock)).toHaveLength(1);
    });
    expect(requestUrls(fetchMock, '/api/agents/review-queue')).toHaveLength(1);

    await unmountApp(root);
  });

  it('falls back to client focus when the agent queue has partial repository errors', async () => {
    window.history.replaceState(null, '', '/');
    const serverQueueWinner = createPullRequest('success', {
      number: 410,
      title: 'Partial server queue item',
      author: 'karolz-ms',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/410',
    });
    const rawClientCandidate = createPullRequest('success', {
      number: 411,
      title: 'Client fallback after partial error',
      author: 'davidfowl',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/411',
    });
    const fetchMock = createPartialAgentReviewQueueFetchMock(serverQueueWinner, rawClientCandidate);
    vi.stubGlobal('fetch', fetchMock);

    const { root } = await renderApp();

    await waitFor(() => {
      expect(requestUrls(fetchMock, '/api/agents/review-queue')).toHaveLength(1);
    });
    const focusPanel = document.querySelector('[aria-label="Focused attention queue"]');
    expect(focusPanel?.textContent).toContain('Client fallback after partial error');
    expect(focusPanel?.textContent).not.toContain('Partial server queue item');

    await unmountApp(root);
  });

  it('loads the agent queue after same-load snapshot polling settles', async () => {
    window.history.replaceState(null, '', '/');
    const staleServerQueueWinner = createPullRequest('success', {
      number: 412,
      title: 'Older same-load queue item',
      author: 'karolz-ms',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/412',
    });
    const freshServerQueueWinner = createPullRequest('success', {
      number: 413,
      title: 'Fresher same-load queue item',
      author: 'karolz-ms',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/413',
      updatedAt: '2026-01-04T00:00:00Z',
    });
    const fetchMock = createSameLoadAgentReviewQueueRaceFetchMock(
      staleServerQueueWinner,
      freshServerQueueWinner,
    );
    vi.stubGlobal('fetch', fetchMock);

    const { root } = await renderApp();

    await waitFor(() => {
      expect(requestUrls(fetchMock, '/api/agents/review-queue')).toHaveLength(1);
    });

    await waitFor(() => {
      const focusPanel = document.querySelector('[aria-label="Focused attention queue"]');
      expect(focusPanel?.textContent).toContain('Fresher same-load queue item');
      expect(focusPanel?.textContent).not.toContain('Older same-load queue item');
    });

    await unmountApp(root);
  });

  it('keeps cached rows visible during live refresh and swaps when live rows finish loading', async () => {
    window.history.replaceState(null, '', '/');
    const liveRefresh = createDeferred<void>();
    const fetchMock = createHardRefreshFetchMock(liveRefresh.promise);
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Cached row');
    });
    await waitFor(() => {
      expect((getButton('Refresh now') as HTMLButtonElement).disabled).toBe(false);
    });

    await clickButton('Refresh now');

    await waitFor(() => {
      expect(document.body.textContent).toContain('Cached row');
      expect(document.body.textContent).not.toContain('Live refreshed row');
    });
    expect(pullRequestListUrls(fetchMock).some((url) => url.searchParams.get('refresh') === 'true')).toBe(true);
    expect(requestUrls(fetchMock, '/api/agents/review-queue')).toHaveLength(1);

    await act(async () => {
      liveRefresh.resolve();
    });

    await waitFor(() => {
      expect(document.body.textContent).toContain('Live refreshed row');
      expect(document.body.textContent).not.toContain('Cached row');
    });
    expect(checksRequestUrls(fetchMock).some((url) => url.searchParams.has('refresh'))).toBe(false);
    expect(requestUrls(fetchMock, '/api/agents/review-queue').some((url) => url.searchParams.has('refresh'))).toBe(false);
    expect(requestUrls(fetchMock, '/api/agents/review-queue')).toHaveLength(2);

    await unmountApp(root);
  });

  it('keeps current rows visible until the enriched refresh result is ready', async () => {
    window.history.replaceState(null, '', '/');
    const liveRefresh = createDeferred<void>();
    const fetchMock = createStalePreservingRefreshFetchMock(liveRefresh.promise);
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Cached enriched row');
      expect((getButton('Refresh now') as HTMLButtonElement).disabled).toBe(false);
    });

    await clickButton('Refresh now');

    await waitFor(() => {
      expect(document.body.textContent).toContain('Cached enriched row');
      expect(document.body.textContent).not.toContain('Live enriched row');
    });
    expect((getButton('Refreshing...') as HTMLButtonElement).disabled).toBe(true);

    await act(async () => {
      liveRefresh.resolve();
    });

    await waitFor(() => {
      expect(document.body.textContent).toContain('Live enriched row');
      expect(document.body.textContent).not.toContain('Cached enriched row');
    });

    await unmountApp(root);
  });

  it('clears ship snapshot status on logout before ship mode reloads', async () => {
    window.history.replaceState(null, '', '/');
    const clipboard = { writeText: vi.fn().mockResolvedValue(undefined) };
    Object.defineProperty(window.navigator, 'clipboard', {
      configurable: true,
      value: clipboard,
    });
    vi.stubGlobal('fetch', createFetchMock({ authenticated: true }));
    const { root } = await renderApp();

    await waitFor(() => getButton('Ship mode'));
    await clickButton('Ship mode');
    await waitFor(() => getButton('Copy link'));

    await clickButton('Copy link');
    await waitFor(() => {
      expect(document.body.textContent).toContain('Share link copied.');
    });

    await clickButton('Sign out');
    await waitFor(() => getButton('Sign in'));
    await clickButton('Review mode');
    await clickButton('Ship mode');

    await waitFor(() => {
      expect(document.body.textContent).toContain('Ship mode data');
      expect(document.body.textContent).not.toContain('Share link copied.');
    });

    await unmountApp(root);
  });
});

type FetchMockOptions = {
  authenticated?: boolean;
  authSource?: string;
  checksState?: CheckState;
  visibleChecksState?: CheckState;
  developmentAccounts?: string[];
  selectedDevelopmentAccount?: string;
  failedRepository?: string;
  failPullRequestsAfterDevelopmentAccountSwitch?: boolean;
  delayPullRequestsAfterDevelopmentAccountSwitch?: Promise<void>;
  switchedPullRequestTitle?: string;
};

function createFetchMock(options: FetchMockOptions = {}) {
  let authenticated = options.authenticated ?? false;
  let selectedDevelopmentAccount = options.selectedDevelopmentAccount ?? '';
  const initialDevelopmentAccount = options.selectedDevelopmentAccount ?? '';
  const pullRequest = createPullRequest(options.checksState ?? 'none');
  const switchedPullRequest = createPullRequest(options.checksState ?? 'none', {
    title: options.switchedPullRequestTitle ?? 'Switched account row',
  });

  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/dashboard/config') {
      return dashboardConfigResponse();
    }

    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated,
        configured: true,
        canLogin: true,
        login: authenticated ? selectedDevelopmentAccount || 'octocat' : undefined,
        message: authenticated ? 'Signed in.' : 'Using anonymous public cache.',
        source: options.authSource,
      });
    }

    if (url.pathname === '/api/github/dev/accounts') {
      if (!options.developmentAccounts) {
        return jsonResponse({ detail: 'Not found' }, 404);
      }

      return jsonResponse<DevelopmentGitHubAccountsResponse>({
        accounts: options.developmentAccounts.map((login) => ({
          login,
          active: login === selectedDevelopmentAccount,
        })),
        selectedLogin: selectedDevelopmentAccount || null,
      });
    }

    if (url.pathname === '/api/github/dev/account') {
      const body = typeof init?.body === 'string'
        ? JSON.parse(init.body) as { login?: string | null }
        : { login: null };
      selectedDevelopmentAccount = body.login ?? '';
      authenticated = true;
      return jsonResponse({ selectedLogin: selectedDevelopmentAccount || null });
    }

    if (url.pathname === '/api/github/logout') {
      authenticated = false;
      return jsonResponse({});
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/github/pulls/graphql') {
      if (url.searchParams.get('label')) {
        return jsonResponse(pullRequestList(url.searchParams.get('repo') ?? pullRequest.repository, []));
      }

      const repository = url.searchParams.get('repo');
      const developmentAccountSwitched = selectedDevelopmentAccount !== initialDevelopmentAccount;
      if (
        repository
        && (
          repository.toLowerCase() === options.failedRepository?.toLowerCase()
          || (options.failPullRequestsAfterDevelopmentAccountSwitch
            && developmentAccountSwitched)
        )
      ) {
        return jsonResponse({ detail: `Cannot access ${repository}` }, 404);
      }
      if (
        developmentAccountSwitched
        && options.delayPullRequestsAfterDevelopmentAccountSwitch
        && repository === pullRequest.repository
      ) {
        await options.delayPullRequestsAfterDevelopmentAccountSwitch;
      }

      const currentPullRequest = developmentAccountSwitched && options.switchedPullRequestTitle
        ? switchedPullRequest
        : pullRequest;
      return jsonResponse(pullRequestList(
        repository ?? currentPullRequest.repository,
        repository === currentPullRequest.repository ? [currentPullRequest] : [],
      ));
    }

    if (url.pathname === '/api/github/pulls/101/timeline') {
      return jsonResponse<TimelineResponse>({
        repository: pullRequest.repository,
        number: pullRequest.number,
        checks: pullRequest.checks,
        mergeableState: null,
        stats: {
          commitCount: 1,
          humanCommenterCount: 1,
          humanCommentCount: 1,
          reviewCount: 0,
          approvalCount: 0,
          firstHumanCommentDelayMs: 60_000,
          developers: [],
        },
        items: [
          {
            id: 'comment-1',
            event: 'commented',
            actor: 'reviewer',
            occurredAt: pullRequest.updatedAt,
            summary: 'reviewer commented',
            body: 'Looks good.',
            htmlUrl: 'https://github.com/microsoft/aspire/pull/101#issuecomment-1',
          },
        ],
      });
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: pullRequest.repository,
        pullRequests: [
          {
            number: pullRequest.number,
            headSha: pullRequest.headSha ?? '',
            checks: checksStatus(options.visibleChecksState ?? 'success'),
          },
        ],
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createHardRefreshFetchMock(liveRefresh: Promise<void>) {
  const cached = createPullRequest('success', {
    title: 'Cached row',
    updatedAt: '2026-01-01T00:00:00Z',
  });
  const live = createPullRequest('success', {
    title: 'Live refreshed row',
    updatedAt: '2026-01-03T00:00:00Z',
  });
  let pullListRequestCount = 0;

  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/dashboard/config') {
      return dashboardConfigResponse();
    }

    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated: true,
        configured: true,
        canLogin: true,
        login: 'octocat',
        message: 'Signed in.',
      });
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/github/pulls/graphql') {
      if (url.searchParams.get('label')) {
        return jsonResponse(pullRequestList(url.searchParams.get('repo') ?? cached.repository, []));
      }

      if (url.searchParams.get('repo') !== cached.repository) {
        return jsonResponse(pullRequestList(url.searchParams.get('repo') ?? cached.repository, []));
      }

      if (url.searchParams.get('refresh') === 'true') {
        await liveRefresh;
        return jsonResponse(pullRequestList(cached.repository, [live]));
      }

      pullListRequestCount += 1;
      if (pullListRequestCount === 1) {
        return jsonResponse(pullRequestList(cached.repository, [cached]));
      }

      if (pullListRequestCount === 2) {
        return jsonResponse(pullRequestList(cached.repository, [cached], {
          source: 'last-good',
          stale: true,
          refreshInProgress: true,
          refreshQueued: true,
        }));
      }

      await liveRefresh;
      return jsonResponse(pullRequestList(cached.repository, [live]));
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: cached.repository,
        pullRequests: [
          {
            number: cached.number,
            headSha: cached.headSha ?? '',
            checks: cached.checks,
          },
          {
            number: live.number,
            headSha: live.headSha ?? '',
            checks: live.checks,
          },
        ],
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createInitialStaleSnapshotFetchMock(liveRefresh: Promise<void>) {
  const lastGood = createPullRequest('success', {
    title: 'Last-good row',
    updatedAt: '2026-01-01T00:00:00Z',
  });
  const fresh = createPullRequest('success', {
    title: 'Fresh row',
    updatedAt: '2026-01-03T00:00:00Z',
  });
  let pullListRequestCount = 0;

  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/dashboard/config') {
      return dashboardConfigResponse();
    }

    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated: true,
        configured: true,
        canLogin: true,
        login: 'octocat',
        message: 'Signed in.',
      });
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/github/pulls/graphql') {
      if (url.searchParams.get('label')) {
        return jsonResponse(pullRequestList(url.searchParams.get('repo') ?? lastGood.repository, []));
      }

      if (url.searchParams.get('repo') !== lastGood.repository) {
        return jsonResponse(pullRequestList(url.searchParams.get('repo') ?? lastGood.repository, []));
      }

      pullListRequestCount += 1;
      if (pullListRequestCount === 1) {
        return jsonResponse(pullRequestList(lastGood.repository, [lastGood], {
          source: 'last-good',
          stale: true,
          refreshInProgress: true,
          refreshQueued: true,
        }));
      }

      await liveRefresh;
      return jsonResponse(pullRequestList(lastGood.repository, [fresh]));
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: lastGood.repository,
        pullRequests: [lastGood, fresh].map((pullRequest) => ({
          number: pullRequest.number,
          headSha: pullRequest.headSha ?? '',
          checks: pullRequest.checks,
        })),
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createStalePreservingRefreshFetchMock(liveRefresh: Promise<void>) {
  const cached = createPullRequest('success', {
    title: 'Cached enriched row',
    updatedAt: '2026-01-01T00:00:00Z',
  });
  const liveEnriched = createPullRequest('success', {
    title: 'Live enriched row',
    updatedAt: '2026-01-03T00:00:00Z',
  });
  let pullListRequestCount = 0;

  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/dashboard/config') {
      return dashboardConfigResponse();
    }

    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated: true,
        configured: true,
        canLogin: true,
        login: 'octocat',
        message: 'Signed in.',
      });
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/github/pulls/graphql') {
      if (url.searchParams.get('label')) {
        return jsonResponse(pullRequestList(url.searchParams.get('repo') ?? cached.repository, []));
      }

      if (url.searchParams.get('repo') !== cached.repository) {
        return jsonResponse(pullRequestList(url.searchParams.get('repo') ?? cached.repository, []));
      }

      if (url.searchParams.get('refresh') === 'true') {
        await liveRefresh;
        return jsonResponse(pullRequestList(cached.repository, [liveEnriched]));
      }

      pullListRequestCount += 1;
      if (pullListRequestCount === 1) {
        return jsonResponse(pullRequestList(cached.repository, [cached]));
      }

      if (pullListRequestCount === 2) {
        return jsonResponse(pullRequestList(cached.repository, [cached], {
          source: 'last-good',
          stale: true,
          refreshInProgress: true,
          refreshQueued: true,
        }));
      }

      await liveRefresh;
      return jsonResponse(pullRequestList(cached.repository, [liveEnriched]));
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: cached.repository,
        pullRequests: [cached, liveEnriched].map((pullRequest) => ({
          number: pullRequest.number,
          headSha: pullRequest.headSha ?? '',
          checks: pullRequest.checks,
        })),
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createDelayedVisibleChecksFetchMock() {
  const pullRequests = [
    createPullRequest('success', { title: 'First visible row' }),
    createPullRequest('success', {
      number: 102,
      title: 'Late visible row',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/102',
      headSha: 'def456',
    }),
  ];

  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/dashboard/config') {
      return dashboardConfigResponse();
    }

    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated: true,
        configured: true,
        canLogin: true,
        login: 'octocat',
        message: 'Signed in.',
      });
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/github/pulls/graphql') {
      if (url.searchParams.get('label')) {
        return jsonResponse(pullRequestList(url.searchParams.get('repo') ?? pullRequests[0].repository, []));
      }

      return jsonResponse(pullRequestList(
        url.searchParams.get('repo') ?? pullRequests[0].repository,
        url.searchParams.get('repo') === pullRequests[0].repository ? pullRequests : [],
      ));
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: pullRequests[0].repository,
        pullRequests: pullRequests.map((pullRequest) => ({
          number: pullRequest.number,
          headSha: pullRequest.headSha ?? '',
          checks: pullRequest.checks,
        })),
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createAgentReviewQueueFetchMock(serverQueueWinner: PullRequestSummary, rawClientCandidate: PullRequestSummary) {
  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/dashboard/config') {
      return dashboardConfigResponse();
    }

    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated: true,
        configured: true,
        canLogin: true,
        login: 'octocat',
        message: 'Signed in.',
      });
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/agents/review-queue') {
      return jsonResponse(agentReviewQueue([{
        repository: serverQueueWinner.repository,
        pullRequest: serverQueueWinner,
        bucketLabel: 'Needs review',
        reason: 'No reviews',
      }]));
    }

    if (url.pathname === '/api/github/pulls/graphql') {
      if (url.searchParams.get('label')) {
        return jsonResponse(pullRequestList(url.searchParams.get('repo') ?? rawClientCandidate.repository, []));
      }

      return jsonResponse(pullRequestList(
        url.searchParams.get('repo') ?? rawClientCandidate.repository,
        url.searchParams.get('repo') === rawClientCandidate.repository ? [rawClientCandidate] : [],
      ));
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: rawClientCandidate.repository,
        pullRequests: [rawClientCandidate, serverQueueWinner].map((pullRequest) => ({
          number: pullRequest.number,
          headSha: pullRequest.headSha ?? '',
          checks: pullRequest.checks,
        })),
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createLoggedOutAgentReviewQueueFetchMock(serverQueueWinner: PullRequestSummary, outsideItem: PullRequestSummary) {
  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/dashboard/config') {
      return dashboardConfigResponse();
    }

    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated: false,
        configured: false,
        canLogin: false,
        message: 'Not signed in.',
      });
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/agents/review-queue') {
      return jsonResponse(agentReviewQueue(
        [{
          repository: serverQueueWinner.repository,
          pullRequest: serverQueueWinner,
          bucketLabel: 'Needs review',
          reason: 'No reviews',
        }],
        [{
          repository: outsideItem.repository,
          pullRequest: outsideItem,
          bucketLabels: ['CI failing', 'Needs review'],
          reason: {
            kind: 'ci-failing',
            label: 'CI failing',
            detail: 'Failing checks keep it out until CI is green again.',
            tone: 'danger',
          },
        }],
      ));
    }

    if (url.pathname === '/api/github/pulls/graphql') {
      if (url.searchParams.get('label')) {
        return jsonResponse(pullRequestList(url.searchParams.get('repo') ?? serverQueueWinner.repository, []));
      }

      return jsonResponse(pullRequestList(
        url.searchParams.get('repo') ?? serverQueueWinner.repository,
        url.searchParams.get('repo') === serverQueueWinner.repository ? [serverQueueWinner, outsideItem] : [],
      ));
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: serverQueueWinner.repository,
        pullRequests: [serverQueueWinner, outsideItem].map((pullRequest) => ({
          number: pullRequest.number,
          headSha: pullRequest.headSha ?? '',
          checks: pullRequest.checks,
        })),
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createEmptyAgentReviewQueueFetchMock(rawClientCandidate: PullRequestSummary) {
  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/dashboard/config') {
      return dashboardConfigResponse();
    }

    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated: true,
        configured: true,
        canLogin: true,
        login: 'octocat',
        message: 'Signed in.',
      });
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/agents/review-queue') {
      return jsonResponse(agentReviewQueue([]));
    }

    if (url.pathname === '/api/github/pulls/graphql') {
      if (url.searchParams.get('label')) {
        return jsonResponse(pullRequestList(url.searchParams.get('repo') ?? rawClientCandidate.repository, []));
      }

      return jsonResponse(pullRequestList(
        url.searchParams.get('repo') ?? rawClientCandidate.repository,
        url.searchParams.get('repo') === rawClientCandidate.repository ? [rawClientCandidate] : [],
      ));
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: rawClientCandidate.repository,
        pullRequests: [
          {
            number: rawClientCandidate.number,
            headSha: rawClientCandidate.headSha ?? '',
            checks: rawClientCandidate.checks,
          },
        ],
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createDelayedAgentReviewQueueFetchMock(
  agentQueueLoaded: Promise<void>,
  serverQueueWinner: PullRequestSummary,
  rawClientCandidate: PullRequestSummary,
) {
  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/dashboard/config') {
      return dashboardConfigResponse();
    }

    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated: true,
        configured: true,
        canLogin: true,
        login: 'octocat',
        message: 'Signed in.',
      });
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/agents/review-queue') {
      await agentQueueLoaded;
      return jsonResponse(agentReviewQueue([{
        repository: serverQueueWinner.repository,
        pullRequest: serverQueueWinner,
        bucketLabel: 'Needs review',
        reason: 'No reviews',
      }]));
    }

    if (url.pathname === '/api/github/pulls/graphql') {
      if (url.searchParams.get('label')) {
        return jsonResponse(pullRequestList(url.searchParams.get('repo') ?? rawClientCandidate.repository, []));
      }

      return jsonResponse(pullRequestList(
        url.searchParams.get('repo') ?? rawClientCandidate.repository,
        url.searchParams.get('repo') === rawClientCandidate.repository ? [rawClientCandidate] : [],
      ));
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: rawClientCandidate.repository,
        pullRequests: [rawClientCandidate, serverQueueWinner].map((pullRequest) => ({
          number: pullRequest.number,
          headSha: pullRequest.headSha ?? '',
          checks: pullRequest.checks,
        })),
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createAgentReviewQueueVisibleChecksFetchMock(
  serverQueueWinner: PullRequestSummary,
  rawClientCandidate: PullRequestSummary,
  visibleChecksState: CheckState,
) {
  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/dashboard/config') {
      return dashboardConfigResponse();
    }

    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated: true,
        configured: true,
        canLogin: true,
        login: 'octocat',
        message: 'Signed in.',
      });
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/agents/review-queue') {
      return jsonResponse(agentReviewQueue([{
        repository: serverQueueWinner.repository,
        pullRequest: serverQueueWinner,
        bucketLabel: 'Needs review',
        reason: 'No reviews',
      }]));
    }

    if (url.pathname === '/api/github/pulls/graphql') {
      if (url.searchParams.get('label')) {
        return jsonResponse(pullRequestList(url.searchParams.get('repo') ?? rawClientCandidate.repository, []));
      }

      return jsonResponse(pullRequestList(
        url.searchParams.get('repo') ?? rawClientCandidate.repository,
        url.searchParams.get('repo') === rawClientCandidate.repository ? [rawClientCandidate] : [],
      ));
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: serverQueueWinner.repository,
        pullRequests: [
          {
            number: serverQueueWinner.number,
            headSha: serverQueueWinner.headSha ?? '',
            checks: checksStatus(visibleChecksState),
          },
        ],
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createPartialAgentReviewQueueFetchMock(serverQueueWinner: PullRequestSummary, rawClientCandidate: PullRequestSummary) {
  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/dashboard/config') {
      return dashboardConfigResponse();
    }

    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated: true,
        configured: true,
        canLogin: true,
        login: 'octocat',
        message: 'Signed in.',
      });
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/agents/review-queue') {
      return jsonResponse({
        ...agentReviewQueue([{
          repository: serverQueueWinner.repository,
          pullRequest: serverQueueWinner,
          bucketLabel: 'Needs review',
          reason: 'No reviews',
        }]),
        repositories: [
          {
            repository: serverQueueWinner.repository,
            pullRequestCount: 1,
            snapshot: null,
            error: null,
          },
          {
            repository: 'microsoft/dcp',
            pullRequestCount: 0,
            snapshot: null,
            error: 'Cannot access microsoft/dcp',
          },
        ],
      });
    }

    if (url.pathname === '/api/github/pulls/graphql') {
      if (url.searchParams.get('label')) {
        return jsonResponse(pullRequestList(url.searchParams.get('repo') ?? rawClientCandidate.repository, []));
      }

      return jsonResponse(pullRequestList(
        url.searchParams.get('repo') ?? rawClientCandidate.repository,
        url.searchParams.get('repo') === rawClientCandidate.repository ? [rawClientCandidate] : [],
      ));
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: rawClientCandidate.repository,
        pullRequests: [rawClientCandidate].map((pullRequest) => ({
          number: pullRequest.number,
          headSha: pullRequest.headSha ?? '',
          checks: pullRequest.checks,
        })),
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createSameLoadAgentReviewQueueRaceFetchMock(
  staleServerQueueWinner: PullRequestSummary,
  freshServerQueueWinner: PullRequestSummary,
) {
  let pullListRequestCount = 0;
  let pullListSettled = false;

  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/dashboard/config') {
      return dashboardConfigResponse();
    }

    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated: true,
        configured: true,
        canLogin: true,
        login: 'octocat',
        message: 'Signed in.',
      });
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/agents/review-queue') {
      return jsonResponse(agentReviewQueue([{
        repository: pullListSettled ? freshServerQueueWinner.repository : staleServerQueueWinner.repository,
        pullRequest: pullListSettled ? freshServerQueueWinner : staleServerQueueWinner,
        bucketLabel: 'Needs review',
        reason: 'No reviews',
      }]));
    }

    if (url.pathname === '/api/github/pulls/graphql') {
      if (url.searchParams.get('label')) {
        return jsonResponse(pullRequestList(url.searchParams.get('repo') ?? staleServerQueueWinner.repository, []));
      }

      pullListRequestCount += 1;
      if (pullListRequestCount === 1) {
        return jsonResponse(pullRequestList(staleServerQueueWinner.repository, [staleServerQueueWinner], {
          source: 'last-good',
          stale: true,
          refreshInProgress: true,
          refreshQueued: true,
        }));
      }

      pullListSettled = true;
      return jsonResponse(pullRequestList(freshServerQueueWinner.repository, [freshServerQueueWinner]));
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: staleServerQueueWinner.repository,
        pullRequests: [staleServerQueueWinner, freshServerQueueWinner].map((pullRequest) => ({
          number: pullRequest.number,
          headSha: pullRequest.headSha ?? '',
          checks: pullRequest.checks,
        })),
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createPullRequest(
  checksState: CheckState,
  overrides: Partial<PullRequestSummary> = {},
): PullRequestSummary {
  const now = overrides.updatedAt ?? new Date().toISOString();
  const number = overrides.number ?? 101;
  return {
    repository: 'microsoft/aspire',
    number,
    title: 'Fix dashboard navigation',
    state: 'open',
    draft: false,
    author: 'davidfowl',
    htmlUrl: `https://github.com/microsoft/aspire/pull/${number}`,
    createdAt: now,
    updatedAt: now,
    fetchedAt: now,
    labels: [],
    requestedReviewers: [],
    linkedIssues: [],
    commitCount: 3,
    additions: 120,
    deletions: 20,
    changedFiles: 5,
    lastCommitAt: now,
    headSha: 'abc123',
    baseRef: 'main',
    mergeableState: null,
    review: {
      state: 'waiting',
      reviewerCount: 0,
      approvalCount: 0,
      changesRequestedCount: 0,
      commentedReviewCount: 0,
      unresolvedThreadCount: 0,
      requiresConversationResolution: false,
    },
    checks: checksStatus(checksState),
    ...overrides,
  };
}

function checksStatus(state: CheckState): PullRequestSummary['checks'] {
  return {
    state,
    totalCount: state === 'none' || state === 'unknown' ? 0 : 1,
    successCount: state === 'success' ? 1 : 0,
    failureCount: state === 'failure' ? 1 : 0,
    pendingCount: state === 'pending' ? 1 : 0,
    neutralCount: 0,
    skippedCount: 0,
    completedAt: state === 'success' ? new Date().toISOString() : null,
    failingChecks: [],
  };
}

function withoutRepository(pullRequest: PullRequestSummary): Omit<PullRequestSummary, 'repository'> {
  const { repository: _repository, ...rest } = pullRequest;
  void _repository;
  return rest;
}

function jsonResponse<T>(body: T, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function dashboardConfigResponse() {
  return jsonResponse<DashboardConfig>({
    ...testDashboardConfig,
  });
}

function pullRequestList(
  repository: string,
  pullRequests: PullRequestSummary[],
  snapshot?: Partial<NonNullable<PullRequestListResponse['snapshot']>>,
): PullRequestListResponse {
  return {
    repository,
    pullRequests: pullRequests.map(withoutRepository),
    snapshot: snapshot
      ? {
          source: 'fresh-cache',
          fetchedAt: pullRequests[0]?.fetchedAt ?? new Date().toISOString(),
          stale: false,
          refreshInProgress: false,
          refreshQueued: false,
          ...snapshot,
        }
      : undefined,
  };
}

function agentReviewQueue(items: Array<{
  repository: string;
  pullRequest: PullRequestSummary;
  bucketLabel: string;
  reason: string;
}>, outsideNeedsAttentionItems: Array<{
  repository: string;
  pullRequest: PullRequestSummary;
  bucketLabels: string[];
  reason: {
    kind:
      | 'ci-failing'
      | 'merge-conflicts'
      | 'unresolved-feedback'
      | 'held-by-label'
      | 'author-response'
      | 'stale-activity'
      | 'community-list'
      | 'specialized-lane'
      | 'stalled-only'
      | 'outside-queue';
    label: string;
    detail: string;
    tone: 'danger' | 'warning' | 'success' | 'accent' | 'muted';
  };
}> = []) {
  return {
    items: items.map((item) => ({
      repository: item.repository,
      pullRequest: withoutRepository(item.pullRequest),
      bucketLabel: item.bucketLabel,
      reason: item.reason,
    })),
    outsideNeedsAttentionItems: outsideNeedsAttentionItems.map((item) => ({
      repository: item.repository,
      pullRequest: withoutRepository(item.pullRequest),
      bucketLabels: item.bucketLabels,
      reason: item.reason,
    })),
    repositories: [
      {
        repository: items[0]?.repository ?? 'microsoft/aspire',
        pullRequestCount: items.length,
        snapshot: null,
        error: null,
      },
    ],
    totalCount: items.length,
    outsideNeedsAttentionTotalCount: outsideNeedsAttentionItems.length,
    generatedAt: new Date().toISOString(),
  };
}

async function renderApp() {
  const host = document.createElement('div');
  document.body.append(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(<App />);
  });
  return { root };
}

async function unmountApp(root: ReturnType<typeof createRoot>) {
  await act(async () => {
    root.unmount();
  });
}

async function clickButton(name: string) {
  await act(async () => {
    getButton(name).dispatchEvent(new MouseEvent('click', { bubbles: true }));
  });
}

async function changeDevAccount(login: string) {
  const select = document.querySelector('.dev-account-picker select');
  if (!(select instanceof HTMLSelectElement)) {
    throw new Error('Unable to find dev account selector.');
  }

  await act(async () => {
    select.value = login;
    select.dispatchEvent(new Event('change', { bubbles: true }));
  });
}

async function changePullRequestState(state: string) {
  const select = Array.from(document.querySelectorAll('select'))
    .find((candidate): candidate is HTMLSelectElement =>
      candidate instanceof HTMLSelectElement
      && Array.from(candidate.options).some((option) => option.value === 'closed'));
  if (!select) {
    throw new Error('Unable to find pull request state selector.');
  }

  await act(async () => {
    select.value = state;
    select.dispatchEvent(new Event('change', { bubbles: true }));
  });
}

function getButton(name: string) {
  const button = [...document.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim() === name);
  if (!button) {
    throw new Error(`Unable to find button: ${name}`);
  }

  return button;
}

function queryInputByLabel(name: string) {
  const label = [...document.querySelectorAll('label')]
    .find((candidate) => candidate.querySelector('span')?.textContent?.trim() === name);
  const input = label?.querySelector('input');
  return input instanceof HTMLInputElement ? input : null;
}

type AppFetchMock =
  | ReturnType<typeof createFetchMock>
  | ReturnType<typeof createHardRefreshFetchMock>
  | ReturnType<typeof createInitialStaleSnapshotFetchMock>
  | ReturnType<typeof createStalePreservingRefreshFetchMock>
  | ReturnType<typeof createDelayedVisibleChecksFetchMock>;

function checksRequestUrls(fetchMock: AppFetchMock) {
  return requestUrls(fetchMock, '/api/github/pulls/checks');
}

function refreshChecksRequestNumbers(fetchMock: AppFetchMock) {
  const calls = fetchMock.mock.calls as [RequestInfo | URL, RequestInit?][];
  return calls.flatMap(([input, init]) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname !== '/api/github/pulls/checks' || url.searchParams.get('refresh') !== 'true') {
      return [];
    }

    const body = typeof init?.body === 'string'
      ? JSON.parse(init.body) as PullRequestChecksRequest
      : { pullRequests: [] };
    return body.pullRequests.map((pullRequest) => pullRequest.number);
  });
}

function pullRequestListUrls(fetchMock: AppFetchMock) {
  return requestUrls(fetchMock, '/api/github/pulls/graphql');
}

function requestUrls(fetchMock: AppFetchMock, pathname: string) {
  return fetchMock.mock.calls
    .map(([input]) => new URL(input.toString(), window.location.origin))
    .filter((url) => url.pathname === pathname);
}

function createDeferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((promiseResolve) => {
    resolve = promiseResolve;
  });
  return { promise, resolve };
}

async function waitFor(assertion: () => void, timeoutMs = 1_000) {
  const start = Date.now();
  let lastError: unknown;
  while (Date.now() - start < timeoutMs) {
    try {
      assertion();
      return;
    } catch (err) {
      lastError = err;
      await act(async () => {
        await new Promise((resolve) => window.setTimeout(resolve, 10));
      });
    }
  }

  throw lastError;
}

function installIntersectionObserverMock() {
  const observers: TestIntersectionObserver[] = [];

  class TestIntersectionObserver {
    private element: Element | null = null;
    private readonly callback: IntersectionObserverCallback;

    constructor(callback: IntersectionObserverCallback) {
      this.callback = callback;
      observers.push(this);
    }

    observe(element: Element) {
      this.element = element;
    }

    unobserve() {
      this.element = null;
    }

    disconnect() {
      this.element = null;
    }

    takeRecords() {
      return [];
    }

    trigger() {
      if (!this.element) {
        return;
      }

      this.callback([
        {
          isIntersecting: true,
          target: this.element,
        } as IntersectionObserverEntry,
      ], this as unknown as IntersectionObserver);
    }
  }

  vi.stubGlobal('IntersectionObserver', TestIntersectionObserver);
  return observers;
}
