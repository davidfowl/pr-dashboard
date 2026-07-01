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
  };
}

function normalizeList(values: string[] | undefined) {
  return [...new Set((values ?? []).map((value) => value.trim()).filter(Boolean))];
}

function normalizeCheckFailureRules(rules: DashboardConfig['nonBlockingCheckFailureRules'] | undefined) {
  return (rules ?? [])
    .map((rule) => ({
      repository: rule.repository?.trim() ?? '',
      label: rule.label?.trim() ?? '',
      checkNames: normalizeList(rule.checkNames),
      checkNameContains: normalizeList(rule.checkNameContains),
    }))
    .filter((rule) => rule.repository && rule.label);
}
