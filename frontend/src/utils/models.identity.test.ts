import { afterEach, describe, expect, it } from 'vitest';
import { emptyDashboardConfig, setActiveDashboardConfig } from '../dashboardConfig';
import { actorIdentityKey } from './models';

afterEach(() => {
  setActiveDashboardConfig(emptyDashboardConfig);
});

describe('actorIdentityKey identity aliases', () => {
  it('folds a configured alias login to the primary identity key', () => {
    setActiveDashboardConfig({
      ...emptyDashboardConfig,
      coreTeamMembers: ['radical'],
      identityAliases: { ankj_microsoft: 'radical' },
    });

    expect(actorIdentityKey('ankj_microsoft')).toBe(actorIdentityKey('radical'));
    // Alias matching is case-insensitive.
    expect(actorIdentityKey('ANKJ_microsoft')).toBe(actorIdentityKey('radical'));
    // The Copilot attribution suffix still collapses to the same human before folding.
    expect(actorIdentityKey('ankj_microsoft/copilot')).toBe(actorIdentityKey('radical'));
  });

  it('leaves unmapped logins unchanged', () => {
    setActiveDashboardConfig({
      ...emptyDashboardConfig,
      identityAliases: { ankj_microsoft: 'radical' },
    });

    expect(actorIdentityKey('someone-else')).toBe('someoneelse');
  });
});
