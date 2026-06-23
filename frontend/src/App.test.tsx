// @vitest-environment jsdom

import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import type {
  AppInfoResponse,
  AuthStatus,
  CheckState,
  PullRequestChecksRequest,
  PullRequestChecksResponse,
  PullRequestStreamItem,
  PullRequestSummary,
  ShipWeekResponse,
  TimelineResponse,
} from './types';

type ActEnvironment = typeof globalThis & {
  IS_REACT_ACT_ENVIRONMENT?: boolean;
};

(globalThis as ActEnvironment).IS_REACT_ACT_ENVIRONMENT = true;

describe('App navigation', () => {
  afterEach(() => {
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

  it('force-refreshes visible checks even when loaded CI state is preserved', async () => {
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

    await clickButton('Refresh now');

    await waitFor(() => {
      expect(checksRequestUrls(fetchMock).some((url) => url.searchParams.get('refresh') === 'true')).toBe(true);
    });

    await unmountApp(root);
  });

  it('force-refreshes rows that become visible after an earlier checks refresh flush', async () => {
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

    await clickButton('Refresh now');
    await waitFor(() => {
      expect(observers.length).toBeGreaterThan(1);
    });

    await act(async () => {
      observers[0].trigger();
    });
    await waitFor(() => {
      expect(refreshChecksRequestNumbers(fetchMock).length).toBeGreaterThan(0);
    });

    await act(async () => {
      for (const observer of observers.slice(1)) {
        observer.trigger();
      }
    });
    await waitFor(() => {
      expect(new Set(refreshChecksRequestNumbers(fetchMock))).toEqual(new Set([101, 102]));
    });

    await unmountApp(root);
  });

  it('keeps cached rows visible during hard refresh and finalizes only completed live rows', async () => {
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
      expect(document.body.textContent).toContain('Stale-only cached row');
      expect(document.body.textContent).not.toContain('Live refreshed row');
    });
    expect(streamRequestUrls(fetchMock).some((url) => url.searchParams.get('refresh') === 'true')).toBe(true);

    await act(async () => {
      liveRefresh.resolve();
    });

    await waitFor(() => {
      expect(document.body.textContent).toContain('Live refreshed row');
      expect(document.body.textContent).not.toContain('Stale-only cached row');
    });
    await waitFor(() => {
      expect(checksRequestUrls(fetchMock).some((url) => url.searchParams.get('refresh') === 'true')).toBe(true);
    });

    await unmountApp(root);
  });

  it('keeps stale-preserving live baseline rows out of the finalized rendered list', async () => {
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
      expect(document.body.textContent).toContain('Live baseline row');
      expect(document.body.textContent).not.toContain('Live enriched row');
    });
    expect((getButton('Refreshing...') as HTMLButtonElement).disabled).toBe(true);

    await act(async () => {
      liveRefresh.resolve();
    });

    await waitFor(() => {
      expect(document.body.textContent).toContain('Live enriched row');
      expect(document.body.textContent).not.toContain('Cached enriched row');
      expect(document.body.textContent).not.toContain('Live baseline row');
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
  checksState?: CheckState;
  visibleChecksState?: CheckState;
};

function createFetchMock(options: FetchMockOptions = {}) {
  let authenticated = options.authenticated ?? false;
  const pullRequest = createPullRequest(options.checksState ?? 'none');
  const streamItem = createStreamItem(pullRequest);

  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated,
        configured: true,
        canLogin: true,
        login: authenticated ? 'octocat' : undefined,
        message: authenticated ? 'Signed in.' : 'Using anonymous public cache.',
      });
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

    if (url.pathname === '/api/github/pulls/stream') {
      if (url.searchParams.get('label')) {
        return jsonLinesResponse([]);
      }

      const repository = url.searchParams.get('repo');
      return jsonLinesResponse(repository === pullRequest.repository ? [streamItem] : []);
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
  const staleOnly = createPullRequest('success', {
    number: 102,
    title: 'Stale-only cached row',
    htmlUrl: 'https://github.com/microsoft/aspire/pull/102',
    updatedAt: '2026-01-02T00:00:00Z',
  });
  const live = createPullRequest('success', {
    title: 'Live refreshed row',
    updatedAt: '2026-01-03T00:00:00Z',
  });

  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
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

    if (url.pathname === '/api/github/pulls/stream') {
      if (url.searchParams.get('label')) {
        return jsonLinesResponse([]);
      }

      if (url.searchParams.get('repo') !== cached.repository) {
        return jsonLinesResponse([]);
      }

      if (url.searchParams.get('refresh') === 'true') {
        return jsonLinesStreamResponse(
          [createStreamItem(staleOnly, { isStale: true })],
          liveRefresh,
          [
            createStreamItem(live),
            { repository: live.repository, isComplete: true },
          ],
        );
      }

      return jsonLinesResponse([createStreamItem(cached)]);
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
            number: staleOnly.number,
            headSha: staleOnly.headSha ?? '',
            checks: staleOnly.checks,
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

function createStalePreservingRefreshFetchMock(liveRefresh: Promise<void>) {
  const cached = createPullRequest('success', {
    title: 'Cached enriched row',
    updatedAt: '2026-01-01T00:00:00Z',
  });
  const liveBaseline = createPullRequest('unknown', {
    title: 'Live baseline row',
    updatedAt: '2026-01-03T00:00:00Z',
  });
  const liveEnriched = createPullRequest('success', {
    title: 'Live enriched row',
    updatedAt: '2026-01-03T00:00:00Z',
  });

  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
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

    if (url.pathname === '/api/github/pulls/stream') {
      if (url.searchParams.get('label')) {
        return jsonLinesResponse([]);
      }

      if (url.searchParams.get('repo') !== cached.repository) {
        return jsonLinesResponse([]);
      }

      if (url.searchParams.get('refresh') === 'true') {
        return jsonLinesStreamResponse(
          [
            createStreamItem(cached, { isStale: true }),
            createStreamItem(liveBaseline, { isStale: true }),
          ],
          liveRefresh,
          [
            createStreamItem(liveEnriched),
            { repository: liveEnriched.repository, isComplete: true },
          ],
        );
      }

      return jsonLinesResponse([createStreamItem(cached)]);
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: cached.repository,
        pullRequests: [cached, liveBaseline, liveEnriched].map((pullRequest) => ({
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

    if (url.pathname === '/api/github/pulls/stream') {
      if (url.searchParams.get('label')) {
        return jsonLinesResponse([]);
      }

      return jsonLinesResponse(
        url.searchParams.get('repo') === pullRequests[0].repository
          ? pullRequests.map((pullRequest) => createStreamItem(pullRequest))
          : [],
      );
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

function createStreamItem(
  pullRequest: PullRequestSummary,
  options: Pick<PullRequestStreamItem, 'isStale'> = {},
): PullRequestStreamItem {
  return {
    repository: pullRequest.repository,
    pullRequest: withoutRepository(pullRequest),
    ...options,
  };
}

function jsonResponse<T>(body: T, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function jsonLinesResponse<T>(items: T[]) {
  return new Response(items.map((item) => JSON.stringify(item)).join('\n'), {
    headers: { 'Content-Type': 'application/x-ndjson' },
  });
}

function jsonLinesStreamResponse<T>(beforeWait: T[], wait: Promise<void>, afterWait: T[]) {
  const encoder = new TextEncoder();
  return new Response(new ReadableStream({
    async start(controller) {
      enqueueJsonLines(controller, encoder, beforeWait);
      await wait;
      enqueueJsonLines(controller, encoder, afterWait);
      controller.close();
    },
  }), {
    headers: { 'Content-Type': 'application/x-ndjson' },
  });
}

function enqueueJsonLines<T>(
  controller: ReadableStreamDefaultController<Uint8Array>,
  encoder: TextEncoder,
  items: T[],
) {
  for (const item of items) {
    controller.enqueue(encoder.encode(`${JSON.stringify(item)}\n`));
  }
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

function getButton(name: string) {
  const button = [...document.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim() === name);
  if (!button) {
    throw new Error(`Unable to find button: ${name}`);
  }

  return button;
}

type AppFetchMock =
  | ReturnType<typeof createFetchMock>
  | ReturnType<typeof createHardRefreshFetchMock>
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

function streamRequestUrls(fetchMock: ReturnType<typeof createHardRefreshFetchMock>) {
  return requestUrls(fetchMock, '/api/github/pulls/stream');
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
