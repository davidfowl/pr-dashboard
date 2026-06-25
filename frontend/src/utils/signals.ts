import type { AttentionSignal } from '../types';

// Several independent generators (bucket labels, action signals, review-progress signals) can emit
// the same underlying concept in different casing — e.g. "Needs review" / "needs reviewer" /
// "no reviews", or "Ready to merge" / "merge". Rendering all of them produces redundant pills, so
// signals are collapsed to one per concept. The first occurrence wins, which keeps the leading
// bucket label (passed first) over the lower-value computed duplicates.
//
// The unresolved-feedback action verb ("resolve feedback") is deliberately NOT grouped with the
// unresolved-thread count here. On the attention board the count reason pill ("N unresolved threads")
// is passed first, so folding the action verb into the same concept would drop the action pill
// entirely — but the count and the call-to-action carry different information (and different tones),
// so both should survive. The two count phrasings still collapse via the "unresolved" substring rule
// in signalConcept below.
const synonymGroups: string[][] = [
  ['needs review', 'needs reviewer', 'no reviews'],
  ['ready to merge', 'merge'],
  ['re-review needed', 're-review', 'commit after review'],
  ['ci failing', 'fix ci'],
  ['wait for ci', 'ci running'],
  ['author response', 'author fix', 'changes requested'],
  ['quick wins', 'quick win'],
  ['stalled', 'unstick'],
  ['docs', 'docs review'],
  ['community toolkit', 'toolkit review'],
  ['bots / automation', 'automation', 'bot'],
];

const conceptByLabel = new Map<string, string>(
  synonymGroups.flatMap((group) => group.map((label) => [label, group[0]] as const)),
);

function signalConcept(label: string): string {
  const normalized = label.trim().toLowerCase();
  // CI failing carries a variable suffix ("CI failing · 4 checks"); fold every variant together.
  if (normalized.startsWith('ci failing')) {
    return 'ci failing';
  }

  // Unresolved-thread pills carry a variable count and come in two phrasings — the lane-leading
  // "N unresolved threads" reason and the shorter computed "N unresolved" pill. Fold both (and the
  // bare "unresolved feedback" label) together so only one survives.
  if (normalized.includes('unresolved')) {
    return 'unresolved feedback';
  }

  return conceptByLabel.get(normalized) ?? normalized;
}

export function dedupeSignals(signals: AttentionSignal[]) {
  const seen = new Set<string>();
  return signals.filter((signal) => {
    const concept = signalConcept(signal.label);
    if (seen.has(concept)) {
      return false;
    }

    seen.add(concept);
    return true;
  });
}
