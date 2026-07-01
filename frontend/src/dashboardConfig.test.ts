import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  emptyDashboardConfig,
  fetchDashboardConfig,
  getDashboardConfig,
  setActiveDashboardConfig,
} from './dashboardConfig';
import type { DashboardConfig } from './types';

describe('dashboard config store', () => {
  beforeEach(() => {
    setActiveDashboardConfig(emptyDashboardConfig);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    setActiveDashboardConfig(emptyDashboardConfig);
  });

  it('normalizes lists and drops incomplete non-blocking check rules', () => {
    const config = setActiveDashboardConfig({
      repositories: [' example/repo ', 'EXAMPLE/repo', '', 'example/other'],
      repositoryInput: '',
      shipWeekRepositories: [' example/repo ', 'example/repo'],
      shipWeekRepositoryInput: '',
      coreTeamMembers: [' octocat ', 'OCTOCAT', 'hubot'],
      coreTeamMemberAliasSuffixes: [' _corp ', '_corp', ''],
      communityRepositories: [' community/repo ', 'COMMUNITY/repo'],
      currentRelease: ' 13.4 ',
      shipWeekReleaseBranch: ' release/13.4 ',
      docsFromCodeRepository: ' docs/repo ',
      docsFromCodeLabel: ' docs-from-code ',
      doNotMergeLabels: [' no-merge ', 'NO-MERGE', 'needs-author-action'],
      botAuthors: [' dependabot ', 'DEPENDABOT'],
      nonBlockingCheckFailureRules: [
        {
          repository: ' example/repo ',
          label: ' flaky check ',
          checkNames: [' Build ', 'BUILD'],
          checkNameContains: [' proof ', ''],
        },
        {
          repository: 'example/repo',
          label: '',
          checkNames: [],
          checkNameContains: [],
        },
        {
          repository: '',
          label: 'missing repository',
          checkNames: [],
          checkNameContains: [],
        },
      ],
    });

    expect(config.repositories).toEqual(['example/repo', 'EXAMPLE/repo', 'example/other']);
    expect(config.repositoryInput).toBe('example/repo, EXAMPLE/repo, example/other');
    expect(config.shipWeekRepositories).toEqual(['example/repo']);
    expect(config.shipWeekRepositoryInput).toBe('example/repo');
    expect(config.coreTeamMembers).toEqual(['octocat', 'OCTOCAT', 'hubot']);
    expect(config.coreTeamMemberAliasSuffixes).toEqual(['_corp']);
    expect(config.communityRepositories).toEqual(['community/repo', 'COMMUNITY/repo']);
    expect(config.currentRelease).toBe('13.4');
    expect(config.shipWeekReleaseBranch).toBe('release/13.4');
    expect(config.docsFromCodeRepository).toBe('docs/repo');
    expect(config.docsFromCodeLabel).toBe('docs-from-code');
    expect(config.doNotMergeLabels).toEqual(['no-merge', 'NO-MERGE', 'needs-author-action']);
    expect(config.botAuthors).toEqual(['dependabot', 'DEPENDABOT']);
    expect(config.nonBlockingCheckFailureRules).toEqual([
      {
        repository: 'example/repo',
        label: 'flaky check',
        checkNames: ['Build', 'BUILD'],
        checkNameContains: ['proof'],
      },
    ]);
  });

  it('leaves the active config unchanged when fetching config fails', async () => {
    const previousConfig: DashboardConfig = {
      ...emptyDashboardConfig,
      repositories: ['example/repo'],
      repositoryInput: 'example/repo',
    };
    setActiveDashboardConfig(previousConfig);
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('network down'))));

    await expect(fetchDashboardConfig()).rejects.toThrow('network down');

    expect(getDashboardConfig()).toEqual(previousConfig);
  });
});
