import type { AttentionSignal } from '../types';

const statusAgeLabels = new Set(['newer', 'old first', 'review debt']);
const ageLabelPattern = /(^|\s)(just now|\d+\s*(m|h|d))($|\s)/i;

export function isRowMetadataSignal(signal: AttentionSignal) {
  const normalized = signal.label.trim().toLowerCase().replace(/\s+/g, ' ');
  return statusAgeLabels.has(normalized) || ageLabelPattern.test(normalized);
}
