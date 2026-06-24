import type { AttentionSignal } from '../types';

const statusAgeLabels = new Set(['newer', 'old first', 'review debt']);
const ageLabelPattern = /(^|\s)(just now|\d+\s*(m|h|d))($|\s)/i;
const updatedAgePattern = /^idle\s+\d+\s*(m|h|d)$/i;

export function isRowMetadataSignal(signal: AttentionSignal) {
  const normalized = normalizeSignalLabel(signal.label);
  return statusAgeLabels.has(normalized) || ageLabelPattern.test(normalized);
}

export function rowMetadataDisplaySignals(signals: AttentionSignal[]) {
  const seen = new Set<string>();
  return signals.filter((signal) => {
    if (!isRowMetadataSignal(signal) || isUpdatedAgeSignal(signal)) {
      return false;
    }

    const normalized = normalizeSignalLabel(signal.label);
    if (seen.has(normalized)) {
      return false;
    }

    seen.add(normalized);
    return true;
  });
}

function isUpdatedAgeSignal(signal: AttentionSignal) {
  return updatedAgePattern.test(normalizeSignalLabel(signal.label));
}

function normalizeSignalLabel(label: string) {
  return label.trim().toLowerCase().replace(/\s+/g, ' ');
}
