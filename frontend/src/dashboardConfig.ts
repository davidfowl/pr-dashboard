import type { DashboardConfig } from './types';
import { readJson } from './utils/http';

export const emptyDashboardConfig: DashboardConfig = {
  repositories: [],
  repositoryInput: '',
  shipWeekRepositories: [],
  shipWeekRepositoryInput: '',
  coreTeamMembers: [],
  coreTeamMemberAliasSuffixes: [],
  communityRepositories: [],
  currentRelease: '',
  shipWeekReleaseBranch: '',
  docsFromCodeRepository: '',
  docsFromCodeLabel: '',
  doNotMergeLabels: [],
  botAuthors: [],
  nonBlockingCheckFailureRules: [],
  identityAliases: {},
};

let activeDashboardConfig = emptyDashboardConfig;

export function getDashboardConfig() {
  return activeDashboardConfig;
}

export function setActiveDashboardConfig(config: DashboardConfig) {
  activeDashboardConfig = normalizeDashboardConfig(config);
  return activeDashboardConfig;
}

export async function fetchDashboardConfig(signal?: AbortSignal) {
  const response = await fetch('/api/dashboard/config', { signal });
  return setActiveDashboardConfig(await readJson<DashboardConfig>(response));
}

function normalizeDashboardConfig(config: DashboardConfig): DashboardConfig {
  const repositories = normalizeList(config.repositories);
  const shipWeekRepositories = normalizeList(config.shipWeekRepositories);

  return {
    repositories,
    repositoryInput: config.repositoryInput?.trim() || repositories.join(', '),
    shipWeekRepositories,
    shipWeekRepositoryInput: config.shipWeekRepositoryInput?.trim() || shipWeekRepositories.join(', '),
    coreTeamMembers: normalizeList(config.coreTeamMembers),
    coreTeamMemberAliasSuffixes: normalizeList(config.coreTeamMemberAliasSuffixes),
    communityRepositories: normalizeList(config.communityRepositories),
    currentRelease: config.currentRelease?.trim() ?? '',
    shipWeekReleaseBranch: config.shipWeekReleaseBranch?.trim() ?? '',
    docsFromCodeRepository: config.docsFromCodeRepository?.trim() ?? '',
    docsFromCodeLabel: config.docsFromCodeLabel?.trim() ?? '',
    doNotMergeLabels: normalizeList(config.doNotMergeLabels),
    botAuthors: normalizeList(config.botAuthors),
    nonBlockingCheckFailureRules: normalizeCheckFailureRules(config.nonBlockingCheckFailureRules),
    identityAliases: normalizeIdentityAliases(config.identityAliases),
  };
}

// Lowercases alias keys and primaries so login canonicalization is case-insensitive.
function normalizeIdentityAliases(aliases: Record<string, string> | undefined) {
  const normalized: Record<string, string> = {};
  for (const [alias, primary] of Object.entries(aliases ?? {})) {
    const key = alias.trim().toLowerCase();
    const value = primary.trim().toLowerCase();
    if (key && value) {
      normalized[key] = value;
    }
  }

  return normalized;
}

function normalizeList(values: string[] | undefined) {
  const normalized: string[] = [];
  const seen = new Set<string>();
  for (const value of values ?? []) {
    const trimmed = value.trim();
    if (!trimmed) {
      continue;
    }

    const key = trimmed.toLowerCase();
    if (!seen.has(key)) {
      seen.add(key);
      normalized.push(trimmed);
    }
  }

  return normalized;
}

function normalizeCheckFailureRules(rules: DashboardConfig['nonBlockingCheckFailureRules'] | undefined) {
  return (rules ?? [])
    .map((rule) => ({
      repository: rule.repository?.trim() ?? '',
      label: rule.label?.trim() ?? '',
      checkNames: normalizeList(rule.checkNames),
      checkNameContains: normalizeList(rule.checkNameContains),
    }))
    .filter((rule) =>
      rule.repository
      && rule.label
      && (rule.checkNames.length > 0 || rule.checkNameContains.length > 0));
}
