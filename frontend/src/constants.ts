export const shipWeekReleaseBranchPlaceholder = 'Auto-detect latest release/*';
export const hourMs = 1000 * 60 * 60;
export const dayMs = hourMs * 24;

// Single source of truth for "For me" personal-pick action labels. Referenced both
// where picks are created and where they are scored, so the two cannot drift apart.
export const personalPickActions = {
  resolveConflicts: 'Resolve conflicts',
  needsAttention: 'Needs your attention',
  fixCi: 'Fix CI',
  reviewThis: 'Review this',
  respondHere: 'Respond here',
  finishThis: 'Finish this',
} as const;

export type PersonalPickAction = (typeof personalPickActions)[keyof typeof personalPickActions];
