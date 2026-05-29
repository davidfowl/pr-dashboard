import type { AttentionSignal } from '../types';

type SignalPillsProps = {
  signals: AttentionSignal[];
  className: string;
};

function SignalPills({ signals, className }: SignalPillsProps) {
  return (
    <span className={className}>
      {dedupeSignals(signals).map((signal) => (
        <span key={signal.label} className={`attention-signal ${signal.tone ?? 'muted'}`}>
          {signal.label}
        </span>
      ))}
    </span>
  );
}

function dedupeSignals(signals: AttentionSignal[]) {
  const seen = new Set<string>();
  return signals.filter((signal) => {
    if (seen.has(signal.label)) {
      return false;
    }

    seen.add(signal.label);
    return true;
  });
}

export default SignalPills;
