import type { AttentionSignal } from '../types';
import { dedupeSignals } from '../utils/signals';

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

export default SignalPills;
