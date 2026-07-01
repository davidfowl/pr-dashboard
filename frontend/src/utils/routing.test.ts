import { afterEach, describe, expect, it, vi } from 'vitest';
import type { DashboardConfig } from '../types';
import {
  createShipWeekUrl,
  parseRepositories,
  parseShipWeekRouteParams,
  replaceLegacyRepositorySearchParam,
} from './routing';

const dashboardConfig: DashboardConfig = {
  repositories: ['example/repo'],
  repositoryInput: 'example/repo',
  shipWeekRepositories: ['example/repo'],
  shipWeekRepositoryInput: 'example/repo',
  coreTeamMembers: [],
  coreTeamMemberAliasSuffixes: [],
  communityRepositories: [],
  currentRelease: '13.4',
  shipWeekReleaseBranch: '',
  docsFromCodeRepository: '',
  docsFromCodeLabel: '',
  doNotMergeLabels: [],
  botAuthors: [],
  nonBlockingCheckFailureRules: [],
};

function stubWindowLocation(href: string, state: unknown = null) {
  const location = new URL(href, 'http://localhost');
  let historyState = state;
  vi.stubGlobal('window', {
    location,
    history: {
      get state() {
        return historyState;
      },
      replaceState(nextState: unknown, _title: string, url: string) {
        historyState = nextState;
        location.href = new URL(url, location.href).href;
      },
    },
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('parseRepositories', () => {
  it('normalizes GitHub repository URLs', () => {
    expect(parseRepositories('https://github.com/example-org/example-repo')).toEqual([
      'example-org/example-repo',
    ]);
  });

  it('normalizes GitHub clone URLs', () => {
    expect(parseRepositories('https://github.com/example-org/example-repo.git')).toEqual([
      'example-org/example-repo',
    ]);
  });
});

describe('parseShipWeekRouteParams', () => {
  it('ignores repository query params and uses configured repositories', () => {
    expect(parseShipWeekRouteParams('?mode=ship&repos=secret/repo&milestone=13.5', dashboardConfig)).toEqual({
      repositoryInput: 'example/repo',
      milestoneInput: '13.5',
      releaseBranchInput: '',
    });
  });
});

describe('replaceLegacyRepositorySearchParam', () => {
  it('removes legacy repository query params while preserving the rest of the route', () => {
    stubWindowLocation('/?mode=ship&repos=secret%2Frepo&milestone=13.5#bucket/ci', { view: 'dashboard' });

    expect(replaceLegacyRepositorySearchParam()).toBe(true);
    expect(window.location.search).toBe('?mode=ship&milestone=13.5');
    expect(window.location.hash).toBe('#bucket/ci');
    expect(window.history.state).toEqual({ view: 'dashboard' });
  });

  it('leaves routes without repository query params unchanged', () => {
    stubWindowLocation('/?mode=ship&milestone=13.5');

    expect(replaceLegacyRepositorySearchParam()).toBe(false);
    expect(window.location.search).toBe('?mode=ship&milestone=13.5');
  });
});

describe('createShipWeekUrl', () => {
  it('does not include repository query params even when given a non-configured repository input', () => {
    stubWindowLocation('/?mode=ship&repos=secret%2Frepo&milestone=13.5');

    const url = createShipWeekUrl({
      repositoryInput: 'secret/repo',
      milestoneInput: '13.6',
      releaseBranchInput: '',
    }, dashboardConfig);

    expect(url).not.toContain('repos=');
    expect(url).toContain('mode=ship');
    expect(url).toContain('milestone=13.6');
  });
});
